using System.Text;
using System.Text.Json.Nodes;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>
/// Day 4: models in the worker. Every model call goes through the real Ollama or gateway adapter to a scripted local HTTP endpoint,
/// so these checks replay the same code a real model would exercise.
/// </summary>
internal static class CheckpointI
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static T One<T>(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar() is { } v and not DBNull ? (T)Convert.ChangeType(v, typeof(T)) : default!;
    }

    private static void Sql(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static async Task Until(Func<bool> done, int ms = 8000, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(done))] string? what = null)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end && !done()) await Task.Delay(25);
        if (!done()) throw new InvalidOperationException($"timed out waiting for: {what}");
    }

    private static async Task<WorkerError> Refused(Func<Task> act)
    {
        try { await act(); }
        catch (WorkerException e) { return e.Error; }
        throw new InvalidOperationException("expected a worker error");
    }

    private static int n;
    private static string C() => "i" + ++n;

    internal sealed class Bench : IAsyncDisposable
    {
        public required WorkerClient W;
        public required FakeWorker Fake;
        public required MemoryVault Vault;
        public required string Db, Project, Folder;
        public ScriptedServer Local = new(), Gateway = new();
        public string? LocalConn, GatewayConn;
        public ValueTask DisposeAsync() { Local.Dispose(); Gateway.Dispose(); return Fake.DisposeAsync(); }

        public string Task(string title, string? outcome = null) => W.CreateTaskAsync(Db, C(), Project, title, outcome).GetAwaiter().GetResult().Id;
        public AttemptView Attempt(string id) => RunEngine.Get(Db, id);
        public async Task<AttemptView> Settled(string id, int ms = 8000)
        {
            await Until(() => !Attempt(id).Active, ms);
            return Attempt(id);
        }
        public ProjectSettings Settings => Team.Read(Db, Project).Settings;
    }

    internal static async Task<Bench> NewBench(string folder)
    {
        var vault = new MemoryVault();
        var fake = new FakeWorker("i-profile", vault);
        var w = new WorkerClient(fake);
        var info = await w.CreateWorkspaceAsync(folder, "Atlas");
        return new Bench { W = w, Fake = fake, Vault = vault, Db = info.DatabasePath, Project = info.ProjectId, Folder = folder };
    }

    /// <summary>
    /// Development-only (the Day 4 native receipt): a disposable Atlas with release notes to write from three supplied changes, QA after it,
    /// and an Ollama connection with a builder and a reviewer on two different model IDs. Nothing here is scripted: the models are real.
    /// </summary>
    public static async Task<string> MakeDay4(string folder, string endpoint, string builder, string reviewer)
    {
        var fake = new FakeWorker("day4-fixture", new MemoryVault());
        var w = new WorkerClient(fake);
        var info = await w.CreateWorkspaceAsync(folder, "Atlas");
        string db = info.DatabasePath;
        string draft = (await w.CreateTaskAsync(db, C(), info.ProjectId, "Release notes", "Write release notes as a bulleted list, one bullet for each supplied change. Cover every change.")).Id;
        string qa = (await w.CreateTaskAsync(db, C(), info.ProjectId, "QA", "Check the release notes against the build.")).Id;
        await w.UpdateTaskAsync(db, C(), draft, 0, reviewRequired: true);
        await w.AddDependencyAsync(db, C(), qa, draft, 0);
        await SeedReleaseNotes(w, db, info.ProjectId, draft);
        string conn = (await w.AddConnectionAsync(db, C(), "ollama", endpoint, "Ollama on this computer")).Id;
        await w.ProbeConnectionAsync(db, conn);
        await w.AssignRoleAsync(db, C(), info.ProjectId, "builder", conn, builder);
        await w.AssignRoleAsync(db, C(), info.ProjectId, "reviewer", conn, reviewer);
        await w.ReleaseWorkspaceAsync(db);
        await fake.DisposeAsync();
        return db;
    }

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        await Discovery(Path.Combine(dir, "discovery"));
        await Local(Path.Combine(dir, "local"));
        await Credentials(Path.Combine(dir, "credentials"));
        await Gateway(Path.Combine(dir, "gateway"));
        await Consent(Path.Combine(dir, "consent"));
        await Leases(Path.Combine(dir, "leases"));
        await Bounds(Path.Combine(dir, "bounds"));
        await Prices(Path.Combine(dir, "prices"));
        await Budget(Path.Combine(dir, "budget"));
        await Roles(Path.Combine(dir, "roles"));
        await Controls(Path.Combine(dir, "controls"));
        await Throttling(Path.Combine(dir, "throttling"));
        await Routing(Path.Combine(dir, "routing"));
        await Addressing(Path.Combine(dir, "addressing"));
        await Journey(Path.Combine(dir, "journey"));
        await Desktop(Path.Combine(dir, "desktop"));
    }

    // Scripted models -------------------------------------------------------------------------------------------------
    /// <summary>An Ollama that lists the given models; /api/chat answers with <paramref name="answer"/>.</summary>
    internal static void ScriptOllama(ScriptedServer s, string[] models, Func<ScriptedRequest, ScriptedReply> answer, bool withCapabilities = true) =>
        s.Handle = r => r.Path switch
        {
            "/api/tags" => ScriptedServer.Tags(models),
            "/api/show" when withCapabilities => new(200, "{\"capabilities\":[\"completion\",\"tools\"]}"),
            "/api/show" => new(200, "{}"),
            "/api/chat" when !models.Contains(r.Model) => new(404, $"{{\"error\":\"model '{r.Model}' not found, try pulling it first\"}}"),
            "/api/chat" => answer(r),
            _ => new(404, "{}"),
        };

    internal static ScriptedReply Text(ScriptedRequest r, string text, int delay = 0) => new(200, ContentType: "application/x-ndjson", Chunks: ScriptedServer.OllamaText(r.Model, text), DelayMs: delay);

    /// <summary>Connects a scripted Ollama, probes it and assigns builder and reviewer.</summary>
    internal static async Task LocalTeam(Bench b, string builder = "builder-a:1", string reviewer = "reviewer-b:2", Func<ScriptedRequest, ScriptedReply>? answer = null)
    {
        ScriptOllama(b.Local, [builder, reviewer, "spare:1"], answer ?? (r => Text(r, "Draft from " + r.Model)));
        b.LocalConn = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", b.Local.Url, "Scripted Ollama")).Id;
        await b.W.ProbeConnectionAsync(b.Db, b.LocalConn);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.LocalConn, builder);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "reviewer", b.LocalConn, reviewer);
    }

    /// <summary>Connects a scripted gateway with a key, allows cloud use with a budget, gives consent, records a price and assigns the builder.</summary>
    internal static async Task CloudTeam(Bench b, string model = "vendor/model-x", Func<ScriptedRequest, ScriptedReply>? answer = null, double budget = 1.0, string[]? files = null)
    {
        b.Gateway.Handle = r => r.Headers.GetValueOrDefault("authorization") != "Bearer sk-test-SYNTHETIC-1234" && r.Headers.GetValueOrDefault("authorization") != "Bearer sk-test-ROTATED-5678"
            ? new(401, "{\"error\":{\"message\":\"Invalid key\"}}")
            : r.Path switch
            {
                "/models" => ScriptedServer.ModelList((model, true), ("vendor/model-y", false)),
                "/chat/completions" => answer?.Invoke(r) ?? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Cloud draft")),
                _ => new(404, "{}"),
            };
        b.GatewayConn = (await b.W.AddConnectionAsync(b.Db, C(), "openrouter", b.Gateway.Url, "Scripted gateway")).Id;
        await b.W.SetCredentialAsync(b.Db, C(), b.GatewayConn, "sk-test-SYNTHETIC-1234");
        await b.W.ProbeConnectionAsync(b.Db, b.GatewayConn);
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { LocalOnly = false, BudgetLimit = budget });
        await b.W.SetPriceAsync(b.Db, C(), b.GatewayConn, model, "USD", 1.0, 2.0, "synthetic rate card", DateTime.UtcNow.AddDays(-1));
        await b.W.GrantConsentAsync(b.Db, C(), b.Project, b.GatewayConn, model, files ?? []);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.GatewayConn, model);
    }

    // AH14 · discover capabilities ---------------------------------------------------------------------------------------
    private static async Task Discovery(string folder)
    {
        await using var b = await NewBench(folder);
        ScriptOllama(b.Local, ["m-with-caps:1", "m-old:1"], r => Text(r, "ok"));
        // An older Ollama build reports no capabilities for m-old.
        var inner = b.Local.Handle;
        b.Local.Handle = r => r.Path == "/api/show" && r.Json?["model"]?.GetValue<string>() == "m-old:1" ? new(200, "{}") : inner(r);
        string conn = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", b.Local.Url, "Scripted Ollama")).Id;
        await b.W.ProbeConnectionAsync(b.Db, conn);
        var view = Team.Read(b.Db, b.Project).Connections.Single();
        var withCaps = view.Models.Single(m => m.Id == "m-with-caps:1");
        var old = view.Models.Single(m => m.Id == "m-old:1");
        Check(view is { Provider: "ollama", IsCloud: false, LastProbeOk: true, LastProbeAt: not null } && view.Endpoint == b.Local.Url
              && withCaps.Capabilities is { Text: true, Tools: true, Vision: false } && old.Capabilities.Text == null && old.Capabilities.Tools == null && old.Capabilities.Describe().Contains("text unknown"),
              "a probe records provider, endpoint, each model ID with its capabilities, unknown ones as unknown, and when it last succeeded");

        // The model disappears between discovery and dispatch.
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", conn, "m-old:1");
        string task = b.Task("Draft", "Write it");
        ScriptOllama(b.Local, ["m-with-caps:1"], r => Text(r, "ok"));
        var a = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        var after = Team.Read(b.Db, b.Project).Connections.Single().Models.Single(m => m.Id == "m-old:1");
        Check(a is { Status: "failed", ErrorCode: "model_unavailable" } && a.StopReason!.Contains("nothing was charged") && !after.Available,
              "a model removed after discovery fails the dispatch before generation, is marked unavailable, and nothing is charged");
        Check((await Refused(() => b.W.DispatchAsync(b.Db, C(), task, "builder"))).Message.Contains("not listed"), "the next dispatch is refused at once and says the model was not listed");
    }

    // AH19 · a local model, start to stop -------------------------------------------------------------------------------
    private static async Task Local(string folder)
    {
        await using var b = await NewBench(folder);
        ScriptOllama(b.Local, ["local-a:1"], r => new(200, ContentType: "application/x-ndjson", Chunks: ScriptedServer.OllamaText(r.Model, string.Concat(Enumerable.Range(0, 30).Select(i => $"word{i} "))), DelayMs: 120));
        string conn = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", b.Local.Url, "Scripted Ollama")).Id;
        await b.W.ProbeConnectionAsync(b.Db, conn);
        var smoke = await b.W.SmokeTestAsync(b.Db, C(), conn, "local-a:1", "Say thirty words.");
        await Until(() => b.Attempt(smoke.Id).PartialText.Length > 0);
        string partial = b.Attempt(smoke.Id).PartialText;
        await b.W.CancelAttemptAsync(b.Db, C(), smoke.Id);
        await Task.Delay(600);
        var stopped = b.Attempt(smoke.Id);
        Check(stopped.Status == "cancelled" && stopped.PartialText.Length > 0 && stopped.PartialText.Length < 40 * 6 && stopped.PartialText.StartsWith(partial[..Math.Min(4, partial.Length)]),
              "a test prompt streams its answer into the attempt and stops when the owner stops it, keeping what arrived");

        var dead = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", "http://localhost:1", "Nothing here")).Id;
        Check((await Refused(() => b.W.ProbeConnectionAsync(b.Db, dead))).Code == "endpoint_unavailable" && Team.Read(b.Db, b.Project).Connections.Single(c => c.Id == dead) is { LastProbeOk: false, LastError: not null },
              "an unavailable endpoint is reported as unreachable and the failed probe is recorded");
        var missing = await b.W.SmokeTestAsync(b.Db, C(), conn, "never-pulled:1", "hi");
        Check(await b.Settled(missing.Id) is { Status: "failed", ErrorCode: "model_unavailable" }, "a model that is not installed is reported as unavailable");
        ScriptOllama(b.Local, ["local-a:1"], r => new(200, ContentType: "application/x-ndjson", Chunks: ["{\"error\":\"model is incomplete: the download was interrupted, pull it again\"}\n"]));
        var broken = await b.W.SmokeTestAsync(b.Db, C(), conn, "local-a:1", "hi");
        Check(await b.Settled(broken.Id) is { Status: "failed", ErrorCode: "model_error" } f && f.StopReason!.Contains("download was interrupted"), "an interrupted model download is reported with the model's own reason");

        // A local-only project makes no cloud request: nothing reaches the gateway, whatever is assigned.
        ScriptOllama(b.Local, ["local-a:1"], r => Text(r, "Local draft"));
        b.Gateway.Handle = _ => new(200, "{}");
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", conn, "local-a:1");
        string task = b.Task("Draft", "Write it");
        var done = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        string gw = (await b.W.AddConnectionAsync(b.Db, C(), "openrouter", b.Gateway.Url, "Gateway")).Id;
        var refusedAssign = await Refused(() => b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", gw, "any/model"));
        Check(done.Status == "completed" && b.Settings.LocalOnly && b.Gateway.Requests == 0 && refusedAssign.Code is "model_unknown" or "local_only",
              "a local-only project runs on the local model and makes zero requests to the gateway");
    }

    // AH15 · keys live in the vault -------------------------------------------------------------------------------------
    private static async Task Credentials(string folder)
    {
        await using var b = await NewBench(folder);
        const string secret = "sk-test-SYNTHETIC-1234";
        const string rotated = "sk-test-ROTATED-5678";
        await CloudTeam(b);
        var conn = Team.Read(b.Db, b.Project).Connections.Single(c => c.Id == b.GatewayConn);
        Check(conn.CredentialStatus == "stored" && b.Vault.Read($"LocalMind.AgentHub/i-profile/{b.GatewayConn}") == secret, "the key is in the vault; the store knows only that one is stored");
        await b.W.SetCredentialAsync(b.Db, C(), b.GatewayConn!, rotated);
        Check(Team.Read(b.Db, b.Project).Connections.Single(c => c.Id == b.GatewayConn).CredentialStatus == "rotated" && b.Vault.Read($"LocalMind.AgentHub/i-profile/{b.GatewayConn}") == rotated
              && (await b.W.ProbeConnectionAsync(b.Db, b.GatewayConn!)).GetProperty("models").GetInt32() == 2, "rotating replaces the key, and the new key works");
        string task = b.Task("Draft", "Write it");
        var used = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        string diag = Path.Combine(folder, "diagnostics.json");
        await b.W.ExportDiagnosticsAsync(b.Db, diag);
        GC.Collect(); GC.WaitForPendingFinalizers();
        byte[] dbBytes = File.ReadAllBytes(b.Db);
        bool InBytes(byte[] hay, string s) => Encoding.UTF8.GetString(hay).Contains(s) || Encoding.Unicode.GetString(hay).Contains(s);
        Check(used.Status == "completed" && !InBytes(dbBytes, secret) && !InBytes(dbBytes, rotated) && !File.ReadAllText(diag).Contains("sk-test") && File.ReadAllText(diag).Contains("rotated")
              && !(File.Exists(b.Db + "-wal") && InBytes(File.ReadAllBytes(b.Db + "-wal"), "sk-test")),
              "neither the first nor the rotated key appears in the database file, its journal or the exported diagnostics");
        b.Vault.Locked = true;
        var locked = await Refused(() => b.W.SetCredentialAsync(b.Db, C(), b.GatewayConn!, "sk-test-OTHER-0000"));
        var probe = await Refused(() => b.W.ProbeConnectionAsync(b.Db, b.GatewayConn!));
        Check(locked.Code == "vault_unavailable" && locked.Message.Contains("locked") && probe.Code == "vault_unavailable" && Team.Read(b.Db, b.Project).Connections.Single(c => c.Id == b.GatewayConn).CredentialStatus == "rotated",
              "with the vault locked, storing and using a key both say so, and the recorded status is unchanged");
        b.Vault.Locked = false;
        await b.W.SetCredentialAsync(b.Db, C(), b.GatewayConn!, null);
        Check(Team.Read(b.Db, b.Project).Connections.Single(c => c.Id == b.GatewayConn).CredentialStatus == "deleted" && b.Vault.Count == 0
              && (await Refused(() => b.W.ProbeConnectionAsync(b.Db, b.GatewayConn!))).Code == "auth", "deleting removes the key from the vault, and the connection then has none");

        // The real Windows vault, with a disposable credential.
        if (OperatingSystem.IsWindows())
        {
            var real = Vaults.ForPlatform();
            string reference = "LocalMind.AgentHub.check/" + Guid.NewGuid().ToString("N");
            real.Write(reference, "synthetic-value-" + reference[^6..]);
            string? back = real.Read(reference);
            real.Write(reference, "synthetic-rotated");
            string? back2 = real.Read(reference);
            real.Delete(reference);
            Check(back == "synthetic-value-" + reference[^6..] && back2 == "synthetic-rotated" && real.Read(reference) == null, "Windows Credential Manager stores, rotates and deletes a disposable credential");
        }
        else Console.WriteLine("NOTE the OS vault check runs on Windows only; this platform's vault reports itself unavailable");
    }

    // AH16 · a gateway: one key, two models, distinct failures -------------------------------------------------------------
    private static async Task Gateway(string folder)
    {
        await using var b = await NewBench(folder);
        await CloudTeam(b);
        var conn = Team.Read(b.Db, b.Project).Connections.Single(c => c.Id == b.GatewayConn);
        Check(conn.Models.Select(m => m.Id).Order().SequenceEqual(["vendor/model-x", "vendor/model-y"]) && conn.Models.Single(m => m.Id == "vendor/model-y").Capabilities.Tools == false && conn.IsCloud,
              "one key discovers two model IDs, with tool support read from the gateway's listing");
        string bad = (await b.W.AddConnectionAsync(b.Db, C(), "openrouter", b.Gateway.Url, "Wrong key")).Id;
        await b.W.SetCredentialAsync(b.Db, C(), bad, "sk-test-WRONG-0000");
        var invalid = await Refused(() => b.W.ProbeConnectionAsync(b.Db, bad));
        Check(invalid.Code == "auth" && invalid.Message.Contains("rejected this key"), "an invalid key is reported as a rejected key");

        string task = b.Task("Draft", "Write it");
        int calls = 0;
        b.Gateway.Handle = r => r.Path == "/chat/completions" ? (++calls == 1
                ? new(429, "{\"error\":{\"message\":\"slow down\"}}", Headers: new() { ["Retry-After"] = "1" })
                : new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Cloud draft")))
            : r.Path == "/models" ? ScriptedServer.ModelList(("vendor/model-x", true), ("vendor/model-y", false)) : new(404, "{}");
        var throttled = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Check(throttled.Status == "completed" && calls == 2 && throttled.StopReason == "Finished.", "throttling with Retry-After inside the run's time is waited out and retried once");

        b.Gateway.Handle = r => r.Path == "/chat/completions" && r.Json?["tools"] != null ? new(400, "{\"error\":{\"message\":\"tools are not supported by this model\"}}")
            : r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "ok"))
            : ScriptedServer.ModelList(("vendor/model-x", true), ("vendor/model-y", false));
        await b.W.AddTaskFileAsync(b.Db, C(), task, "notes.txt", "a supplied file");
        await b.W.GrantConsentAsync(b.Db, C(), b.Project, b.GatewayConn!, "vendor/model-x", ["notes.txt"]);
        var unsupported = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        await b.W.SetPriceAsync(b.Db, C(), b.GatewayConn!, "vendor/model-y", "USD", 1, 1, "synthetic", DateTime.UtcNow.AddDays(-1));
        await b.W.GrantConsentAsync(b.Db, C(), b.Project, b.GatewayConn!, "vendor/model-y", ["notes.txt"]);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.GatewayConn!, "vendor/model-y");
        var noTools = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Check(unsupported is { Status: "failed", ErrorCode: "unsupported" } && unsupported.StopReason!.Contains("tools are not supported") && noTools.Status == "completed",
              "a rejected feature is its own outcome; a model listed without tools is simply not offered the file tool");
    }

    // AH109 · see the packet before it leaves, and keep consent to that destination and scope --------------------------------
    private static async Task Consent(string folder)
    {
        await using var b = await NewBench(folder);
        await CloudTeam(b, files: ["change-a.txt"]);
        string task = b.Task("Release notes", "Summarise the supplied changes");
        await b.W.AddTaskFileAsync(b.Db, C(), task, "change-a.txt", "Added dark mode");
        var preview = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        Check(preview is { Role: "builder", Model: "vendor/model-x", IsCloud: true, ConsentStatus: "granted", Blocker: null, PriceStatus: "known" } && preview.ConnectionLabel == "Scripted gateway"
              && preview.FileNames.SequenceEqual(["change-a.txt"]) && preview.PacketText.Contains("Added dark mode") && preview.Estimate > 0, "the preview names the recipient, model, files, the exact packet text and the estimate");
        await b.W.AddTaskFileAsync(b.Db, C(), task, "change-b.txt", "Fixed crash on export");
        var widened = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        int requestsBefore = b.Gateway.Requests;
        var refused = await Refused(() => b.W.DispatchAsync(b.Db, C(), task, "builder"));
        Check(widened.ConsentStatus == "files_not_covered" && widened.Blocker!.Contains("change-b.txt") && refused.Code == "consent_required" && b.Gateway.Requests == requestsBefore,
              "a new file outside the consent blocks the dispatch in the worker, before any request is made");
        await b.W.GrantConsentAsync(b.Db, C(), b.Project, b.GatewayConn!, "vendor/model-x", ["change-a.txt", "change-b.txt"]);
        var shown = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        var sent = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        var request = b.Gateway.Seen.Last(r => r.Path == "/chat/completions");
        Check(sent.Status == "completed" && sent.PacketText == shown.PacketText && request.Packet == shown.PacketText, "what was dispatched is exactly the packet that was previewed, byte for byte");

        // Local-only: external dispatch is refused in the worker, and a local model asking for a file outside its packet gets nothing.
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { LocalOnly = true });
        int before = b.Gateway.Requests;
        var blocked = await Refused(() => b.W.DispatchAsync(b.Db, C(), task, "builder"));
        Check(blocked.Code == "local_only" && b.Gateway.Requests == before, "a local-only project refuses external dispatch in the worker, with no request sent");
        int asked = 0;
        ScriptOllama(b.Local, ["local-a:1"], r => ++asked == 1 ? new(200, ContentType: "application/x-ndjson", Chunks: ScriptedServer.OllamaToolCall(r.Model, "secrets.env")) : Text(r, "done without it"));
        string local = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", b.Local.Url, "Scripted Ollama")).Id;
        await b.W.ProbeConnectionAsync(b.Db, local);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", local, "local-a:1");
        var local1 = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        var second = b.Local.Seen.Last(r => r.Path == "/api/chat").Json!["messages"]!.AsArray();
        Check(local1.Status == "completed" && local1.ToolCalls == 1 && second.Any(m => m?["role"]?.GetValue<string>() == "tool" && m["content"]!.GetValue<string>().Contains("was not part of this handoff"))
              && b.Gateway.Requests == before, "when a model asks for a file outside its packet it is told no, and nothing goes to the cloud");
    }

    // AH12 · one owner per attempt; an expired lease is told apart by whether the request was sent ---------------------------
    private static async Task Leases(string folder)
    {
        await using var b = await NewBench(folder);
        await LocalTeam(b, answer: r => Text(r, "slow draft", 150));
        // This fixture is about leases. An automatic review of each draft would hold project slots (limit 2) at unrelated moments, so it is switched off here;
        // the limit itself and the review queue have their own checks in CheckpointJ.
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string task = b.Task("Draft", "Write it");
        var one = new RunEngine(new MemoryVault(), () => Guid.NewGuid().ToString("N"));
        var two = new RunEngine(new MemoryVault(), () => Guid.NewGuid().ToString("N"));
        var go = new Barrier(2);
        var race = new[] { one, two }.Select((engine, i) => Task.Run(() =>
        {
            go.SignalAndWait();
            try { return (Id: engine.Dispatch(b.Db, "race-" + i, task, "builder", new()).Id, Error: (string?)null); }
            catch (StoreException e) { return (Id: (string?)null, Error: e.Error.Code); }
        })).ToArray();
        var results = await Task.WhenAll(race);
        Check(results.Count(r => r.Id != null) == 1 && results.Count(r => r.Error == "attempt_active") == 1 && One<long>(b.Db, $"SELECT count(*) FROM attempts WHERE task_id='{task}'") == 1,
              "two runners racing for the same task: one owns the attempt, the other is told it is already running");
        await one.Completion(results.First(r => r.Id != null).Id!);
        await two.Completion(results.First(r => r.Id != null).Id!);

        // Expired leases, one never sent, one sent.
        string past = DateTime.UtcNow.AddMinutes(-5).ToString("O");
        string t2 = b.Task("Unsent"), t3 = b.Task("Sent");
        Sql(b.Db, $"""
            INSERT INTO attempts(id,command_id,run_id,project_id,task_id,role,connection_id,provider,requested_model,status,lease_owner,lease_expires,created_at,packet_text,price_status,currency)
            VALUES('x-unsent','c-unsent','r1','{b.Project}','{t2}','builder','{b.LocalConn}','ollama','builder-a:1','leased','dead','{past}','{past}','p','local','USD'),
                  ('x-sent','c-sent','r2','{b.Project}','{t3}','builder','{b.LocalConn}','ollama','builder-a:1','streaming','dead','{past}','{past}','p','local','USD');
            UPDATE attempts SET sent_at='{past}', partial_text='half a dra' WHERE id='x-sent';
            """);
        await b.W.RecoverRunsAsync(b.Db);
        Check(b.Attempt("x-unsent") is { Status: "abandoned" } u && u.StopReason!.Contains("before anything was sent") && b.Attempt("x-sent") is { Status: "needs_reconciliation", PartialText: "half a dra" } s && s.StopReason!.Contains("may have accepted"),
              "on expiry an unsent attempt is abandoned and safe to start again, while a sent one is kept for reconciliation with its partial text");
        var fresh = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t2, "builder")).Id);
        var guarded = await Refused(() => b.W.DispatchAsync(b.Db, C(), t3, "builder"));
        var chosen = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t3, "builder", acceptRepeatCost: true)).Id);
        Check(fresh.Status == "completed" && guarded.Code == "needs_reconciliation" && chosen.Status == "completed", "the abandoned task starts again freely; the possibly-accepted one needs the owner's explicit choice");
    }

    // AH20 · steps, time and tool calls are limited in one place ---------------------------------------------------------------
    private static async Task Bounds(string folder)
    {
        await using var b = await NewBench(folder);
        int loops = 0;
        await LocalTeam(b, answer: r => { loops++; return new(200, ContentType: "application/x-ndjson", Chunks: ScriptedServer.OllamaToolCall(r.Model, "change-a.txt")); });
        string task = b.Task("Loop", "Loop forever");
        await b.W.AddTaskFileAsync(b.Db, C(), task, "change-a.txt", "Added dark mode");
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { MaxToolCalls = 2, MaxSteps = 10, AutoReview = false });
        var tools = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Check(tools is { Status: "stopped", ToolCalls: 3 } && tools.StopReason == "Stopped at the 2-tool-call limit." && loops == 3, "a looping model stops once at the tool-call limit, with the reason on the attempt");
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { MaxToolCalls = 20, MaxSteps = 2 });
        var steps = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Check(steps is { Status: "stopped", Steps: 2 } && steps.StopReason == "Stopped at the 2-step limit.", "the step limit stops it at two model turns");
        ScriptOllama(b.Local, ["builder-a:1", "reviewer-b:2"], r => new(200, ContentType: "application/x-ndjson", Chunks: ScriptedServer.OllamaText(r.Model, string.Concat(Enumerable.Range(0, 60).Select(i => $"w{i} "))), DelayMs: 2500));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { MaxSteps = 4, MaxSeconds = 5 });
        var slow = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id, 15000);
        Check(slow is { Status: "stopped" } && slow.StopReason == "Stopped at the 5-second limit." && slow.PartialText.Length > 0, "the time limit stops a slow stream and keeps what had arrived");
        await b.Fake.RestartAsync();
        var reread = RunEngine.ForTask(b.Db, task);
        Check(reread.Count(a => a.Status == "stopped") == 3 && reread.Single(a => a.Id == slow.Id).PartialText == slow.PartialText && reread.Single(a => a.Id == tools.Id).StopReason == tools.StopReason
              && One<string>(b.Db, $"SELECT transcript FROM attempts WHERE id='{tools.Id}'").Contains("read_file"), "after a restart each stopped attempt still shows its reason, partial text and transcript");
    }

    // AH64 · versioned prices ---------------------------------------------------------------------------------------------------
    private static async Task Prices(string folder)
    {
        await using var b = await NewBench(folder);
        await CloudTeam(b);
        string task = b.Task("Draft", "Write it");
        var known = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        var row = Team.Read(b.Db, b.Project).Prices.Single();
        Check(known.PriceStatus == "known" && row is { Currency: "USD", InputPerMTok: 1.0, OutputPerMTok: 2.0, Source: "synthetic rate card" } && row.EffectiveAt.Length > 0
              && Math.Abs(known.Estimate!.Value - (known.PacketTokens / 1e6 * 1.0 + known.MaxOutputTokens / 1e6 * 2.0)) < 1e-12,
              "a price keeps model, currency, input and output rates, source and effective date, and the estimate uses them");
        await b.W.SetPriceAsync(b.Db, C(), b.GatewayConn!, "vendor/model-x", "USD", 5, 5, "next quarter's card", DateTime.UtcNow.AddDays(30));
        Check((await b.W.PreviewDispatchAsync(b.Db, task, "builder")).Estimate == known.Estimate, "a price that takes effect later is stored but not used yet");
        Sql(b.Db, "DELETE FROM prices");
        await b.W.SetPriceAsync(b.Db, C(), b.GatewayConn!, "vendor/model-x", "USD", 1, 2, "last year's card", DateTime.UtcNow.AddDays(-200));
        var stale = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        Sql(b.Db, "DELETE FROM prices");
        var unknown = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        Check(stale.PriceStatus == "stale" && stale.Blocker!.Contains("stale") && unknown.PriceStatus == "unknown" && unknown.Estimate == null && unknown.Blocker!.Contains("unknown"),
              "a stale or unknown price is shown before starting and blocks until the owner chooses");
        var go = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder", acceptPriceRisk: true)).Id);
        Check(go is { Status: "completed", PriceStatus: "unknown", Cost: null }, "with the owner's explicit choice it runs, and its cost stays unknown rather than guessed");
    }

    // AH21 · reserve, reconcile, never double-charge ------------------------------------------------------------------------------
    private static async Task Budget(string folder)
    {
        await using var b = await NewBench(folder);
        await CloudTeam(b, answer: r => new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Cloud draft", 1000, 200), DelayMs: 100));
        string t1 = b.Task("One", "Write it"), t2 = b.Task("Two", "Write it");
        var p = await b.W.PreviewDispatchAsync(b.Db, t1, "builder");
        // A budget with room for exactly one estimate.
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { BudgetLimit = p.Estimate!.Value * 1.5 });
        var go = new Barrier(2);
        var race = new[] { t1, t2 }.Select(t => Task.Run(async () =>
        {
            go.SignalAndWait();
            try { return (Id: (await b.W.DispatchAsync(b.Db, "b-" + t, t, "builder")).Id, Error: (string?)null); }
            catch (WorkerException e) { return (Id: (string?)null, Error: e.Error.Code); }
        })).ToArray();
        var results = await Task.WhenAll(race);
        Check(results.Count(r => r.Id != null) == 1 && results.Count(r => r.Error == "blocked") == 1, "two requests racing for the last of the budget: one reserves it, the other is refused");
        string won = results.Single(r => r.Id != null).Id!;
        var budgetDuring = Team.Read(b.Db, b.Project).Budget;
        var settled = await b.Settled(won);
        var after = Team.Read(b.Db, b.Project).Budget;
        double expected = 1000 / 1e6 * 1.0 + 200 / 1e6 * 2.0;
        Check(budgetDuring.Reserved > 0 && settled.Cost is { } cost && Math.Abs(cost - expected) < 1e-12 && after.Reserved == 0 && Math.Abs(after.Spent - expected) < 1e-12,
              "the reservation is replaced by the reported usage at the recorded price");
        await b.W.CommandStatusAsync(b.Db, "finish-" + won); // a repeated finish event is a duplicate, not a second charge
        Check(One<long>(b.Db, $"SELECT count(*) FROM budget_ledger WHERE attempt_id='{won}' AND kind='settle'") == 1 && Math.Abs(Team.Read(b.Db, b.Project).Budget.Spent - expected) < 1e-12,
              "the settlement exists once per attempt; repeating it changes nothing");

        b.Gateway.Handle = r => r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "No usage", null)) : ScriptedServer.ModelList(("vendor/model-x", true));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { BudgetLimit = 1.0 });
        var silent = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t2, "builder")).Id);
        var budget = Team.Read(b.Db, b.Project).Budget;
        Check(silent is { Status: "completed", Cost: null } && silent.StopReason!.Contains("did not report usage") && budget.UnknownUsage == 1, "usage the provider never reported stays visible as unknown");
    }

    // AH22 · roles: provider and model are separate, capabilities explained, cloud needs consent ------------------------------------
    private static async Task Roles(string folder)
    {
        await using var b = await NewBench(folder);
        ScriptOllama(b.Local, ["text-model:1", "image-only:1"], r => Text(r, "ok"));
        var inner = b.Local.Handle;
        b.Local.Handle = r => r.Path == "/api/show" && r.Json?["model"]?.GetValue<string>() == "image-only:1" ? new(200, "{\"capabilities\":[\"vision\"]}") : inner(r);
        string conn = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", b.Local.Url, "Scripted Ollama")).Id;
        await b.W.ProbeConnectionAsync(b.Db, conn);
        foreach (var role in new[] { "planner", "builder", "reviewer" }) await b.W.AssignRoleAsync(b.Db, C(), b.Project, role, conn, "text-model:1");
        var view = Team.Read(b.Db, b.Project);
        Check(view.Assignments.Count == 3 && view.Assignments.All(a => a.ConnectionId == conn && a.ModelId == "text-model:1") && view.Connections.Single().Provider == "ollama",
              "planner, builder and reviewer are stored per project, each with its connection (provider) and model ID kept apart");
        var incompatible = await Refused(() => b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", conn, "image-only:1"));
        Check(incompatible.Code == "incompatible" && incompatible.Message.Contains("does not produce text"), "a model that cannot produce text is refused with the reason");
        b.Gateway.Handle = r => r.Path == "/models" ? ScriptedServer.ModelList(("vendor/model-x", true)) : new(404, "{}");
        string gw = (await b.W.AddConnectionAsync(b.Db, C(), "openrouter", b.Gateway.Url, "Gateway")).Id;
        await b.W.SetCredentialAsync(b.Db, C(), gw, "sk-test-SYNTHETIC-1234");
        await b.W.ProbeConnectionAsync(b.Db, gw);
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { LocalOnly = false, BudgetLimit = 1 });
        var noConsent = await Refused(() => b.W.AssignRoleAsync(b.Db, C(), b.Project, "reviewer", gw, "vendor/model-x"));
        await b.W.GrantConsentAsync(b.Db, C(), b.Project, gw, "vendor/model-x", []);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "reviewer", gw, "vendor/model-x");
        Check(noConsent.Code == "consent_required" && Team.Read(b.Db, b.Project).Assignments.Single(a => a.Role == "reviewer").ModelId == "vendor/model-x", "moving a role to a cloud model needs the project's consent first");
    }

    // AH24 · cancel, pause, resume ----------------------------------------------------------------------------------------------
    private static async Task Controls(string folder)
    {
        await using var b = await NewBench(folder);
        await LocalTeam(b, answer: r => new(200, ContentType: "application/x-ndjson", Chunks: ScriptedServer.OllamaText(r.Model, string.Concat(Enumerable.Range(0, 40).Select(i => $"t{i} "))), DelayMs: 700));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string task = b.Task("Draft", "Write it");
        var a = await b.W.DispatchAsync(b.Db, C(), task, "builder");
        await Until(() => b.Attempt(a.Id).PartialText.Length > 3);
        await b.W.CancelAttemptAsync(b.Db, C(), a.Id);
        string atCancel = b.Attempt(a.Id).PartialText;
        await Task.Delay(1200);
        Check(b.Attempt(a.Id) is { Status: "cancelled" } c && c.PartialText == atCancel && One<long>(b.Db, $"SELECT count(*) FROM outputs WHERE task_id='{task}'") == 0,
              "after cancel, tokens that were still on their way change nothing and no revision is written");

        var p = await b.W.DispatchAsync(b.Db, C(), task, "builder");
        await Until(() => b.Attempt(p.Id).PartialText.Length > 3);
        await b.W.PauseAttemptAsync(b.Db, C(), p.Id);
        string kept = b.Attempt(p.Id).PartialText;
        await b.Fake.RestartAsync();
        var resumeCmd = C();
        var r1 = await b.W.ResumeAttemptAsync(b.Db, resumeCmd, p.Id);
        var r2 = await b.W.ResumeAttemptAsync(b.Db, resumeCmd, p.Id);
        var r3 = await Refused(() => b.W.ResumeAttemptAsync(b.Db, C(), p.Id));
        Check(r2.Duplicate && r2.Id == r1.Id && r3.Code == "attempt_active" && One<long>(b.Db, $"SELECT count(*) FROM attempts WHERE retry_of='{p.Id}'") == 1 && b.Attempt(p.Id) is { Status: "paused" } paused && paused.PartialText == kept,
              "after pause and a restart, resume starts exactly one new attempt, and the paused one keeps what it produced");
        var resumed = await b.Settled(r1.Id, 15000);
        Check(resumed.Status == "completed" && resumed.RetryOf == p.Id && One<long>(b.Db, $"SELECT count(*) FROM outputs WHERE task_id='{task}'") == 1, "the resumed attempt finishes and writes the one revision");
    }

    // AH105 · throttling inside the budget, dropped streams kept --------------------------------------------------------------------
    private static async Task Throttling(string folder)
    {
        await using var b = await NewBench(folder);
        await CloudTeam(b, answer: r => new(429, "{\"error\":{\"message\":\"busy\"}}", Headers: new() { ["Retry-After"] = "600" }));
        string task = b.Task("Draft", "Write it");
        var refused = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Check(refused is { Status: "failed", ErrorCode: "throttled" } && refused.StopReason!.Contains("600") && refused.StopReason.Contains("nothing was charged") && Team.Read(b.Db, b.Project).Budget.Reserved == 0,
              "a Retry-After beyond the run's time is not waited out: the refusal before generation is reported and its reservation released");
        b.Gateway.Handle = r => r.Path == "/chat/completions"
            ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "The first part of a long answer"), DropAfter: 2)
            : ScriptedServer.ModelList(("vendor/model-x", true));
        var dropped = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Check(dropped is { Status: "interrupted", ErrorCode: "dropped" } && dropped.PartialText.Length > 0 && dropped.StopReason!.Contains("after output began") && dropped.StopReason.Contains("could cost again"),
              "a stream that drops after visible output is kept as interrupted, with its partial text, and is told apart from a refusal");
        var guarded = await Refused(() => b.W.ResumeAttemptAsync(b.Db, C(), dropped.Id));
        b.Gateway.Handle = r => r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Whole answer")) : ScriptedServer.ModelList(("vendor/model-x", true));
        var retried = await b.Settled((await b.W.ResumeAttemptAsync(b.Db, C(), dropped.Id, acceptRepeatCost: true)).Id);
        Check(guarded.Code == "repeat_cost" && retried.Status == "completed" && retried.RetryOf == dropped.Id, "retrying it needs an explicit choice, because a second request could cost money");
    }

    // AH106 · requested and reported route -----------------------------------------------------------------------------------------
    private static async Task Routing(string folder)
    {
        await using var b = await NewBench(folder);
        await CloudTeam(b, answer: r => new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse("vendor/model-x", "Vendor", "Routed as asked")));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { OnlyProviders = ["Vendor"] });
        string task = b.Task("Draft", "Write it");
        var asked = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        var body = b.Gateway.Seen.Last(r => r.Path == "/chat/completions").Json!;
        Check(asked is { ReportedModel: "vendor/model-x", ReportedProvider: "Vendor", RouteNote: null } && body["provider"]!["allow_fallbacks"]!.GetValue<bool>() == false
              && body["provider"]!["only"]!.AsArray().Single()!.GetValue<string>() == "Vendor" && body["models"] == null,
              "the request pins the model, allows only the project's providers and turns gateway fallbacks off; the reported route matches");
        b.Gateway.Handle = r => r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse("vendor/cheaper-model", "Elsewhere", "Substituted")) : ScriptedServer.ModelList(("vendor/model-x", true));
        var swapped = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Check(swapped.RouteNote!.Contains("Unexpected route: vendor/cheaper-model answered instead of vendor/model-x") && swapped.RouteNote.Contains("Elsewhere is not one of the allowed providers")
              && swapped.ReportedModel == "vendor/cheaper-model", "a substituted model and a provider outside the restriction are both surfaced");
        b.Gateway.Handle = r => r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(null, null, "Anonymous")) : ScriptedServer.ModelList(("vendor/model-x", true));
        var silent = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Check(silent is { ReportedModel: null, ReportedProvider: null } && silent.RouteNote!.Contains("did not report which model") && silent.RouteNote.Contains("did not report the upstream provider"),
              "missing routing fields are shown as unknown, not assumed");
    }

    // AH27 · one agent or the team ------------------------------------------------------------------------------------------------
    private static async Task Addressing(string folder)
    {
        await using var b = await NewBench(folder);
        await LocalTeam(b, answer: r => Text(r, r.Packet.Contains("Instruction from the owner") ? "Followed: " + r.Model : "Draft by " + r.Model));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string task = b.Task("Draft", "Write it");
        var preview = await b.W.PreviewInstructionAsync(b.Db, task, ["builder"], "Use British spelling.");
        Check(preview.Single() is { Role: "builder", Model: "builder-a:1", IsCloud: false, Estimate: 0 } p && p.PacketText.Contains("Use British spelling.") && p.PacketText.Contains("to the builder only"),
              "the preview of a private instruction names its one recipient, its model, the packet it will get and the estimate");
        var priv = await b.W.SendInstructionAsync(b.Db, C(), task, ["builder"], "Use British spelling.");
        var builderRun = await b.Settled(priv.Attempts.Single().Attempt!.Id);
        Check(priv.Attempts.Single().Role == "builder" && builderRun.PacketText.Contains("Use British spelling.") && !b.Local.Seen.Any(r => r.Path == "/api/chat" && r.Model == "reviewer-b:2"),
              "a private instruction reaches only the builder; the reviewer receives nothing");

        string cmd = C();
        var everyone = await b.W.SendInstructionAsync(b.Db, cmd, task, ["everyone"], "Keep it under 100 words.");
        var again = await b.W.SendInstructionAsync(b.Db, cmd, task, ["everyone"], "Keep it under 100 words.");
        var ids = everyone.Attempts.Where(x => x.Attempt != null).Select(x => x.Attempt!.Id).ToList();
        foreach (var id in ids) await b.Settled(id);
        var mine = RunEngine.ForTask(b.Db, task).Where(x => x.InstructionId == everyone.Instruction.Id).ToList();
        Check(everyone.Attempts.Select(x => x.Role).Order().SequenceEqual(["builder", "reviewer"]) && mine.Count == 2 && mine.Select(x => x.Role).Order().SequenceEqual(["builder", "reviewer"])
              && again.Instruction.Duplicate && again.Attempts.All(x => x.Attempt?.Duplicate == true) && mine.All(x => x.PacketText.Contains("Keep it under 100 words.")),
              "a broadcast starts one attempt per assigned role, each identified by its instruction, and sending it again starts nothing new");
    }

    // AH25 · builder → reviewer on a different model → owner -----------------------------------------------------------------------
    internal static Func<ScriptedRequest, ScriptedReply> ReleaseNotesTeam(string builder, string reviewer) => r =>
    {
        if (r.Model == builder)
        {
            bool corrected = r.Packet.Contains("Reviewer feedback") || r.Packet.Contains("omit");
            return Text(r, "Release notes\n- Added dark mode\n- Fixed crash on export" + (corrected ? "\n- Faster startup" : ""));
        }
        string draft = r.Packet.Contains("Output under review") ? r.Packet[r.Packet.IndexOf("Output under review")..] : "";
        draft = draft.Contains("## Supplied file") ? draft[..draft.IndexOf("## Supplied file")] : draft;
        return Text(r, draft.Contains("Faster startup") ? "APPROVE\nAll three supplied changes are covered." : "REQUEST CHANGES\nThe notes omit change-c.txt (Faster startup).");
    };

    internal static async Task SeedReleaseNotes(WorkerClient w, string db, string project, string task)
    {
        await w.AddTaskFileAsync(db, C(), task, "change-a.txt", "Added dark mode");
        await w.AddTaskFileAsync(db, C(), task, "change-b.txt", "Fixed crash on export");
        await w.AddTaskFileAsync(db, C(), task, "change-c.txt", "Faster startup");
    }

    private static async Task Journey(string folder)
    {
        await using var b = await NewBench(folder);
        await LocalTeam(b, "scripted-builder:1", "scripted-reviewer:2", ReleaseNotesTeam("scripted-builder:1", "scripted-reviewer:2"));
        string draft = b.Task("Release notes", "Write release notes covering every supplied change.");
        string qa = b.Task("QA", null);
        await b.W.UpdateTaskAsync(b.Db, C(), draft, 0, reviewRequired: true);
        await b.W.AddDependencyAsync(b.Db, C(), qa, draft, 0);
        await SeedReleaseNotes(b.W, b.Db, b.Project, draft);

        var first = await b.Settled((await b.W.DispatchAsync(b.Db, C(), draft, "builder")).Id);
        await Until(() => RunEngine.ForTask(b.Db, draft).Any(a => a.Role == "reviewer" && !a.Active));
        var review1 = RunEngine.ForTask(b.Db, draft).Single(a => a.Role == "reviewer");
        Check(first is { Status: "completed", RequestedModel: "scripted-builder:1", ReportedModel: "scripted-builder:1", Revision: 1 } && review1 is { Status: "completed", RequestedModel: "scripted-reviewer:2", Revision: 1 }
              && One<string>(b.Db, "SELECT recommendation FROM reviews ORDER BY rowid DESC LIMIT 1") == "request_changes" && One<string>(b.Db, "SELECT comment FROM reviews ORDER BY rowid DESC LIMIT 1").Contains("change-c.txt"),
              "the builder's revision 1 starts the reviewer on a different model, which names the omitted change");
        var qaBlocked = await Refused(() => b.W.MoveTaskAsync(b.Db, C(), qa, "in_progress", 1));
        string out1 = One<string>(b.Db, "SELECT id FROM outputs WHERE revision=1");
        await b.W.DecideOutputAsync(b.Db, C(), draft, out1, 1, 1, "changes_requested", "Add the startup change, as the reviewer says.");
        var second = await b.Settled((await b.W.DispatchAsync(b.Db, C(), draft, "builder")).Id);
        await Until(() => RunEngine.ForTask(b.Db, draft).Count(a => a.Role == "reviewer" && !a.Active) == 2);
        var review2 = RunEngine.ForTask(b.Db, draft).Last(a => a.Role == "reviewer");
        var diff = Outputs.Compare(b.Db, out1, One<string>(b.Db, "SELECT id FROM outputs WHERE revision=2"));
        Check(qaBlocked.Code == "blocked" && second.Revision == 2 && diff.Lines.Any(l => l is { Op: '+', Text: "- Faster startup" }) && review2.Revision == 2
              && One<string>(b.Db, "SELECT recommendation FROM reviews ORDER BY rowid DESC LIMIT 1") == "approve_recommended",
              "QA waits; after the owner asks for changes, revision 2 adds the missing change and the reviewer recommends approval");
        var stillBlocked = await Refused(() => b.W.MoveTaskAsync(b.Db, C(), qa, "in_progress", 1));
        string out2 = One<string>(b.Db, "SELECT id FROM outputs WHERE revision=2");
        await b.W.DecideOutputAsync(b.Db, C(), draft, out2, 2, 1, "accepted", null);
        var moved = await b.W.MoveTaskAsync(b.Db, C(), qa, "in_progress", 1);
        Check(stillBlocked.Code == "blocked" && moved.Revision > 0, "a recommendation is not acceptance: QA moves only after the owner accepts revision 2");

        await b.Fake.RestartAsync();
        var runs = RunEngine.ForTask(b.Db, draft);
        Check(runs.Count == 4 && runs.All(r => r.Status == "completed" && r.InputTokens == 40 && r.OutputTokens == 25 && r.Cost == 0) && runs.Select(r => r.RequestedModel).Distinct().Count() == 2
              && One<string>(b.Db, "SELECT decision FROM decisions WHERE content_revision=2") == "accepted" && One<string>(b.Db, $"SELECT author_role FROM outputs WHERE id='{out2}'") == "builder · scripted-builder:1 (Scripted Ollama)",
              "after a restart the outputs, the decision, both models' attempts and their usage are all read back");
    }
    // AH25 through the desktop view model: connect, assign, preview, send, review, decide, restart ----------------------------
    private static async Task Desktop(string folder)
    {
        using var local = new ScriptedServer();
        ScriptOllama(local, ["scripted-builder:1", "scripted-reviewer:2"], ReleaseNotesTeam("scripted-builder:1", "scripted-reviewer:2"));
        var vault = new MemoryVault();
        var fake = new FakeWorker("desk", vault);
        var hub = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(new WorkerClient(fake)), PickFile = () => Task.FromResult<string?>(null) };
        await hub.CreateWorkspaceAsync("Atlas", folder);
        hub.StartPolling(TimeSpan.FromMilliseconds(100)); // as the window does
        string db = Path.Combine(folder, "agenthub.db");
        string project = One<string>(db, "SELECT id FROM projects");
        var w = new WorkerClient(fake);
        await hub.CreateTaskAsync("Release notes", "Write release notes covering every supplied change.");
        await hub.CreateTaskAsync("QA", "");
        var draft = hub.Tasks.Single(t => t.Title == "Release notes");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.AddPrerequisiteAsync(draft.Id);
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Release notes");
        await hub.ToggleReviewCommand.RunAsync();
        await Until(() => hub.SelectedTask?.ReviewRequired == true);
        await SeedReleaseNotes(w, db, project, draft.Id);

        // Connections: add the local endpoint and probe it. Team: a different model for each role.
        hub.ShowConnectionsCommand.Execute(null);
        hub.NewProviderIndex = 0;
        hub.NewEndpoint = local.Url;
        hub.NewLabel = "Scripted Ollama";
        await hub.AddConnectionCommand.RunAsync();
        await Until(() => hub.ConnectionRows.Count == 1);
        await hub.ProbeCommand.RunAsync(hub.ConnectionRows[0]);
        await Until(() => hub.ConnectionRows.Count == 1 && hub.ConnectionRows[0].ModelLines.Count == 2);
        Check(hub.ConnectionRows[0].Probe.StartsWith("Last successful probe") && hub.ConnectionRows[0].ModelLines.Any(l => l.StartsWith("scripted-reviewer:2 — text")),
              "the Connections view lists the discovered models with their capabilities and the last successful probe");
        hub.ShowTeamCommand.Execute(null);
        await Until(() => hub.RoleRows.Count == 3 && hub.RoleRows[1].Choices.Count == 2);
        var builderRow = hub.RoleRows.Single(r => r.Role == "builder");
        builderRow.Selected = builderRow.Choices.Single(c => c.ModelId == "scripted-builder:1");
        await Until(() => Team.Read(db, project).Assignments.Count == 1);
        var reviewerRow = hub.RoleRows.Single(r => r.Role == "reviewer");
        reviewerRow.Selected = reviewerRow.Choices.Single(c => c.ModelId == "scripted-reviewer:2");
        await Until(() => Team.Read(db, project).Assignments.Count == 2);
        await Until(() => hub.RoleRows.Single(r => r.Role == "reviewer").Explanation.Contains("Provider: Ollama"));
        Check(hub.RoleRows.Single(r => r.Role == "builder").Selected!.ModelId == "scripted-builder:1" && hub.RoleRows.Single(r => r.Role == "reviewer").Explanation.Contains("Model: scripted-reviewer:2"),
              "the team editor assigns a different model to builder and reviewer and shows provider and model separately");

        // Preview, then send the brief from the ordinary workspace.
        hub.ShowBoardCommand.Execute(null);
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Release notes");
        await Until(() => hub.CanEdit);
        await hub.PreviewBuilderCommand.RunAsync();
        Check(hub.HasPreview && hub.PreviewTitle.Contains("scripted-builder:1") && hub.PreviewDetails.Contains("change-a.txt, change-b.txt, change-c.txt") && hub.PreviewDetails.Contains("Nothing leaves this computer")
              && hub.PreviewPacket.Contains("Faster startup") && hub.CanSendPreview, "the preview shows the recipient, its model, the files and the exact packet before sending");
        await hub.SendPreviewCommand.RunAsync();
        await Until(() => hub.AttemptRows.Count == 2 && hub.AttemptRows.All(r => !r.Active), 15000);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.Review?.HasOutput == true && hub.HasFeedback);
        await Until(() => hub.AttemptRows.Count == 2);
        var rows = hub.AttemptRows.ToList();
        Check(rows.Any(r => r.Title.StartsWith("Builder · scripted-builder:1") && r.Status.Contains("wrote revision 1")) && rows.Any(r => r.Title.StartsWith("Reviewer · scripted-reviewer:2") && r.Status.Contains("reviewed revision 1"))
              && rows.All(r => r.Route.Contains("answered by scripted-") && r.Usage.Contains("40 tokens in, 25 out") && r.Usage.Contains("no cost (local model)")) && rows.All(r => r.Packet.Length > 0)
              && hub.FeedbackText.Contains("change-c.txt"),
              "the run inspector shows both attempts: who received what, which model answered, and their usage; the reviewer's reason is in the review panel");

        // The owner asks for the correction and compares revision 2 without opening a log.
        await hub.FreezeRequestChanges()("Please add the missing change.");
        await hub.PreviewBuilderCommand.RunAsync();
        await hub.SendPreviewCommand.RunAsync();
        await Until(() => hub.AttemptRows.Count == 4 && hub.AttemptRows.All(r => !r.Active), 15000);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.Review?.Revision == 2 && hub.HasFeedback && hub.FeedbackText.Contains("approve"));
        Check(hub.ChangeLines.Any(l => l == "+ - Faster startup") && hub.CanDecide && hub.EffectText.Contains("“QA” can then start"), "revision 2 shows the added change beside the decision, and the reviewer now recommends approval");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.MoveTaskCommand.RunAsync("in_progress");
        Check(hub.Tasks.Single(t => t.Title == "QA").Status == "open", "QA still waits: the reviewer's approval is only a recommendation");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Release notes");
        await Until(() => hub.CanDecide);
        await hub.AcceptOutputCommand.RunAsync();
        await Until(() => hub.Review?.AcceptedBy != null);
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.MoveTaskCommand.RunAsync("in_progress");
        await Until(() => hub.Tasks.Single(t => t.Title == "QA").Status == "in progress");

        // Restart: outputs, decision and usage records are all still there.
        hub.StopPolling();
        await hub.ReleaseWriteLockAsync();
        var again = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(new WorkerClient(new FakeWorker("desk2", vault))), PickFile = () => Task.FromResult<string?>(null) };
        await again.OpenWorkspaceFolderAsync(folder);
        again.SelectedTask = again.Tasks.Single(t => t.Title == "Release notes");
        await Until(() => again.AttemptRows.Count == 4 && again.Review?.AcceptedBy != null);
        Check(again.DecisionBadge.StartsWith("Accepted · revision 2") && again.AttemptRows.Select(r => r.A.RequestedModel).Distinct().Count() == 2 && again.AttemptRows.All(r => r.A.Status == "completed" && r.A.InputTokens == 40),
              "after a restart the accepted revision, its badge and all four attempts with their usage are read back");
        await again.ReleaseWriteLockAsync();
    }
}
