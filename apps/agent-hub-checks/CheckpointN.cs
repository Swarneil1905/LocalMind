using System.Text;
using System.Text.Json.Nodes;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 6 · evidence in a message, restore and replay, and recovering interrupted work. Scripted endpoints, disposable workspaces, synthetic secrets.</summary>
internal static class CheckpointN
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }
    private static T One<T>(string db, string sql) => CheckpointJ.One<T>(db, sql);
    private static Task Until(Func<bool> f, int ms = 10000, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(f))] string? what = null) => CheckpointJ.Until(f, ms, what);
    private static Task<WorkerError> Refused(Func<Task> a) => CheckpointJ.Refused(a);
    private static string C() => CheckpointJ.C();

    internal static void Sql(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    /// <summary>A header-valid PNG of the stated size. The salt makes each one distinct; the header is all the worker reads.</summary>
    internal static byte[] Png(int w, int h, string salt = "x")
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange([(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0, 0, 0, 0, 0]);
        b.AddRange(Encoding.UTF8.GetBytes("PIXELS-" + salt));
        return [.. b];
    }

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        await ImageChecks(Path.Combine(dir, "images"));
        await DocumentChecks(Path.Combine(dir, "docs"));
        await ReplayChecks(Path.Combine(dir, "replay"));
        await PortabilityChecks(Path.Combine(dir, "portable"));
        await MigrationChecks(Path.Combine(dir, "migrate"));
        await ForgetChecks(Path.Combine(dir, "forget"));
        await ReconcileChecks(Path.Combine(dir, "reconcile"));
        await LifecycleChecks(Path.Combine(dir, "lifecycle"));
        await DiagnosticsChecks(Path.Combine(dir, "diag"));
    }

    // AH33 · AH86 -----------------------------------------------------------------------------------------------------------------------------------
    private static async Task ImageChecks(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        CheckpointI.ScriptOllama(b.Local, ["seer:1", "blind:1"], r => CheckpointI.Text(r, "Seen."));
        var inner = b.Local.Handle;
        b.Local.Handle = r => r.Path == "/api/show" ? new(200, r.Json?["model"]?.GetValue<string>() == "seer:1" ? "{\"capabilities\":[\"completion\",\"vision\"]}" : "{\"capabilities\":[\"completion\"]}") : inner(r);
        b.LocalConn = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", b.Local.Url, "Scripted Ollama")).Id;
        await b.W.ProbeConnectionAsync(b.Db, b.LocalConn);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.LocalConn, "seer:1");
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "reviewer", b.LocalConn, "blind:1");
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string task = b.Task("Fix the layout", "Look at the screenshot");
        await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id); // a draft exists, so the reviewer could otherwise be asked
        var png = Png(1280, 720, "one");
        var img = new ImageArg("shot.png", Convert.ToBase64String(png));

        var previews = await b.W.PreviewInstructionAsync(b.Db, task, ["builder", "reviewer"], "What is wrong here?", [img]);
        var seer = previews.Single(p => p.Role == "builder"); var blind = previews.Single(p => p.Role == "reviewer");
        Check(seer.ImageStatus == "ok" && seer.Images!.Single().Contains("1280×720") && seer.Images![0].Contains("KB") && blind.ImageStatus == "no_vision" && blind.Blocker!.Contains("does not accept images"),
              "the preview shows each image's dimensions and size per recipient, and explains why a recipient without vision cannot take it");
        var sent = await b.W.SendInstructionAsync(b.Db, C(), task, ["builder", "reviewer"], "What is wrong here?", previews.ToDictionary(p => p.Role, p => p.Binding), [img]);
        var run = await b.Settled(sent.Attempts.Single(a => a.Role == "builder").Attempt!.Id);
        var seen = b.Local.Seen.Last(x => x.Path == "/api/chat" && x.Model == "seer:1");
        Check(run.Status == "completed" && seen.Images.Count == 1 && seen.Images[0] == img.Base64 && sent.Attempts.Single(a => a.Role == "reviewer").Error!.Contains("does not accept images") && !b.Local.Seen.Any(x => x.Path == "/api/chat" && x.Model == "blind:1"),
              "the scripted recipient receives exactly the attached bytes; the recipient without vision receives no request");

        // Removing the attachment removes its bytes from the request.
        var bare = await b.W.SendInstructionAsync(b.Db, C(), task, ["builder"], "Never mind, describe it in words.");
        await b.Settled(bare.Attempts.Single().Attempt!.Id);
        var after = b.Local.Seen.Last(x => x.Path == "/api/chat" && x.Model == "seer:1");
        Check(after.Images.Count == 0 && !after.Body.Contains(img.Base64[..40]), "an attachment removed before sending is absent from the outgoing request, bytes and all");
        var bad = await Refused(() => b.W.SendInstructionAsync(b.Db, C(), task, ["builder"], "x", null, [new("notes.txt", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")))]));
        Check(bad.Code == "unsupported_image", "a file that is not a supported image is refused with the reason");

        // Cloud: the owner confirms these images for this destination, and only that.
        await using var c = await CheckpointI.NewBench(Path.Combine(folder, "cloud"));
        await CheckpointI.CloudTeam(c);
        string models = new JsonObject { ["data"] = new JsonArray(Model("vendor/model-x"), Model("vendor/model-y")) }.ToJsonString();
        c.Gateway.Handle = r => r.Path == "/models" ? new(200, models) : r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Looks fine.")) : new(404, "{}");
        await c.W.ProbeConnectionAsync(c.Db, c.GatewayConn!);
        await c.W.GrantConsentAsync(c.Db, C(), c.Project, c.GatewayConn!, "vendor/model-y", []);
        await c.W.SetPriceAsync(c.Db, C(), c.GatewayConn!, "vendor/model-y", "USD", 1, 2, "synthetic", DateTime.UtcNow.AddDays(-1));
        string t2 = c.Task("Cloud look", "Look at it");
        var cp = (await c.W.PreviewInstructionAsync(c.Db, t2, ["builder"], "Look.", [img])).Single();
        int chats = c.Gateway.Seen.Count(x => x.Path == "/chat/completions");
        var noConfirm = (await c.W.SendInstructionAsync(c.Db, C(), t2, ["builder"], "Look.", new() { ["builder"] = cp.Binding }, [img])).Attempts.Single();
        Check(cp.ImageStatus == "needs_confirmation" && cp.ImageToken.Length > 10 && noConfirm.Attempt == null && noConfirm.Error!.Contains("cloud service") && c.Gateway.Seen.Count(x => x.Path == "/chat/completions") == chats,
              "images bound for a cloud model need the owner's confirmation first, and nothing is sent without it");
        var confirmed = (await c.W.PreviewInstructionAsync(c.Db, t2, ["builder"], "Look.", [img], new() { ["builder"] = cp.ImageToken })).Single();
        var ok = await c.W.SendInstructionAsync(c.Db, C(), t2, ["builder"], "Look.", new() { ["builder"] = confirmed.Binding }, [img], new() { ["builder"] = cp.ImageToken });
        await c.Settled(ok.Attempts.Single().Attempt!.Id);
        Check(confirmed.ImageStatus == "ok" && c.Gateway.Seen.Last(x => x.Path == "/chat/completions").Images.SequenceEqual([img.Base64]), "after confirming, the cloud recipient receives exactly those image bytes");
        await c.W.AssignRoleAsync(c.Db, C(), c.Project, "builder", c.GatewayConn!, "vendor/model-y");
        var other = (await c.W.PreviewInstructionAsync(c.Db, t2, ["builder"], "Look.", [img], new() { ["builder"] = cp.ImageToken })).Single();
        Check(other.ImageToken != cp.ImageToken && other.ImageStatus == "needs_confirmation", "changing the destination invalidates the earlier confirmation: the same image to another model asks again");
        var seerLocal = (await b.W.PreviewInstructionAsync(b.Db, task, ["builder"], "Look.", [img])).Single();
        Check(seerLocal.ImageToken != cp.ImageToken, "the local and cloud destinations have different confirmations");
    }

    private static JsonNode Model(string id) => new JsonObject
    {
        ["id"] = id, ["architecture"] = new JsonObject { ["input_modalities"] = new JsonArray("text", "image"), ["output_modalities"] = new JsonArray("text") },
        ["supported_parameters"] = new JsonArray("max_tokens"),
    };

    // AH34 ------------------------------------------------------------------------------------------------------------------------------------------
    private static async Task DocumentChecks(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: r => r.Model == "reviewer-b:2"
            ? CheckpointI.Text(r, r.Packet.Contains("14 days") ? "Refunds close after 14 days [1]." : r.Packet.Contains("30 days") ? "Refunds are accepted for 30 days [1]." : "I could not tell.")
            : CheckpointI.Text(r, "ok"));
        Sql(b.Db, "INSERT INTO projects VALUES('beta','Beta')");
        string policy = (await b.W.AddDocumentAsync(b.Db, C(), b.Project, "policy.md", "Refunds are accepted within 30 days of purchase.\n\nShipping takes five business days.")).Id;
        await b.W.AddDocumentAsync(b.Db, C(), "beta", "other.md", "Refund window is 99 days for Beta customers only.");
        var ask1 = await b.W.AskDocumentsAsync(b.Db, C(), b.Project, "How long is the refund window?", "reviewer");
        var a1 = (await b.W.ReadAnswersAsync(b.Db, b.Project)).Single();
        var cite = a1.Citations.Single(c => c.Cited);
        var packet = One<string>(b.Db, $"SELECT packet_text FROM doc_answers WHERE id='{ask1.Id}'");
        var open = await b.W.OpenPassageAsync(b.Db, cite.DocId, cite.Revision, cite.Start, cite.Length);
        Check(a1.Status == "answered" && a1.Answer.Contains("30 days") && cite.Revision == 1 && cite.DocName == "policy.md" && packet.Contains("30 days") && !packet.Contains("99 days")
              && open.Text == "Refunds are accepted within 30 days of purchase." && open.State == "current" && open.Note.Contains("current"),
              "an answer cites the document, revision and passage; the citation opens that exact text; another project's document is never used");

        await b.W.EditDocumentAsync(b.Db, C(), policy, "Refunds close after 14 days from purchase.\n\nShipping takes five business days.");
        await b.W.AskDocumentsAsync(b.Db, C(), b.Project, "How long is the refund window?", "reviewer");
        var answers = await b.W.ReadAnswersAsync(b.Db, b.Project);
        var old = await b.W.OpenPassageAsync(b.Db, cite.DocId, 1, cite.Start, cite.Length);
        var fresh = answers.Last().Citations.Single(c => c.Cited);
        Check(answers.Last().Answer.Contains("14 days") && fresh.Revision == 2 && old.Text.Contains("30 days") && old.State == "superseded" && old.Note.Contains("revision 2"),
              "after an edit, new answers use the new revision, and an old citation still opens the passage as it was, marked superseded");

        int chats = b.Local.Seen.Count(x => x.Path == "/api/chat");
        var unrelated = await b.W.AskDocumentsAsync(b.Db, C(), b.Project, "What colour is the moon?", "reviewer");
        var gap = (await b.W.ReadAnswersAsync(b.Db, b.Project)).Single(x => x.Id == unrelated.Id);
        await b.W.RemoveDocumentAsync(b.Db, C(), policy);
        var removed = await b.W.AskDocumentsAsync(b.Db, C(), b.Project, "How long is the refund window?", "reviewer");
        var none = (await b.W.ReadAnswersAsync(b.Db, b.Project)).Single(x => x.Id == removed.Id);
        var stillOpens = await b.W.OpenPassageAsync(b.Db, cite.DocId, 1, cite.Start, cite.Length);
        Check(gap.Status == "no_evidence" && gap.Answer.Contains("mention") && none.Status == "no_evidence" && none.Answer.Contains("no documents") && b.Local.Seen.Count(x => x.Path == "/api/chat") == chats && stillOpens.State == "removed",
              "with no eligible evidence the answer explains the gap and nothing is sent to a model; a removed document is no longer eligible but its old citations still open");
    }

    // AH100 · AH59 ----------------------------------------------------------------------------------------------------------------------------------
    private static async Task<(CheckpointI.Bench B, string A, string B2, string Out)> Flow(string folder)
    {
        var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: r => CheckpointI.Text(r, "Draft text"));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string a = b.Task("Write", "Write it"), c = b.Task("Publish", "Publish it");
        await b.W.UpdateTaskAsync(b.Db, C(), a, 0, reviewRequired: true);
        await b.W.AddDependencyAsync(b.Db, C(), c, a, 0);
        await b.Settled((await b.W.DispatchAsync(b.Db, C(), a, "builder")).Id);
        string output = One<string>(b.Db, $"SELECT id FROM outputs WHERE task_id='{a}'");
        return (b, a, c, output);
    }

    private static async Task ReplayChecks(string folder)
    {
        var (b, a, c, output) = await Flow(folder);
        await using var _ = b;
        long afterDraft = One<long>(b.Db, "SELECT max(seq) FROM events");
        await b.W.DecideOutputAsync(b.Db, C(), a, output, 1, 1, "accepted", null);
        await b.W.MoveTaskAsync(b.Db, C(), c, "in_progress", One<int>(b.Db, $"SELECT revision FROM tasks WHERE id='{c}'"));
        long latest = One<long>(b.Db, "SELECT max(seq) FROM events");
        var ops = new List<string>();
        b.Fake.BeforeHandle = r => { ops.Add(r.Op); return Task.CompletedTask; };
        int requests = b.Local.Requests;
        var timeline = await b.W.ReplayTimelineAsync(b.Db, b.Project);
        var early = await b.W.ReplayStateAsync(b.Db, b.Project, afterDraft);
        var mid = await b.W.ReplayStateAsync(b.Db, b.Project, timeline.First(e => e.Kind == "output_accepted").Seq);
        var end = await b.W.ReplayStateAsync(b.Db, b.Project, latest);
        b.Fake.BeforeHandle = null;
        var tA = (ReplayTask t) => t.Id == a;
        Check(early.Tasks.Single(tA) is { Status: "open", Revisions: 1, AcceptedRevision: null } && mid.Tasks.Single(tA) is { Status: "done", AcceptedRevision: 1 } && end.Tasks.Single(t => t.Id == c).Status == "in_progress"
              && early.Tasks.Single(t => t.Id == c).Status == "open" && end.Tasks.Single(tA).AcceptedRevision == 1 && timeline.Count >= 5,
              "replay shows the task state and accepted revision at a chosen event, and they match the live board at the end");
        Check(ops.All(o => o.StartsWith("replay")) && b.Local.Requests == requests, "entering, seeking and leaving replay sent no provider request and no write");

        // AH100 · the preceding schema replays into the same state; unknown fields survive; an irreversible unknown event stops replay.
        string old = Path.Combine(folder, "preceding");
        Directory.CreateDirectory(old);
        using (var src = ProjectStore.OpenRead(b.Db))
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(old, "agenthub.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        { dst.Open(); src.BackupDatabase(dst); }
        Downgrade(Path.Combine(old, "agenthub.db"));
        ProjectStore.Load(old);
        var migrated = Replay.StateAt(Path.Combine(old, "agenthub.db"), b.Project, long.MaxValue);
        Check(migrated.Tasks.Count == end.Tasks.Count && migrated.Tasks.Zip(end.Tasks).All(p => p.First == p.Second) && One<long>(Path.Combine(old, "agenthub.db"), "SELECT count(*) FROM events WHERE event_schema=1") == One<long>(Path.Combine(old, "agenthub.db"), "SELECT count(*) FROM events"),
              "the preceding schema (events without a schema number) migrates and replays into the same task and decision state");
        Sql(b.Db, $"INSERT INTO events(command_id,task_id,kind,detail,created_at,event_schema) VALUES('future-1','{a}','task_updated','{{\"id\":\"{a}\",\"future_field\":{{\"keep\":[1,2,3]}}}}','2099-01-01T00:00:00Z',2)");
        string exported = Replay.ExportEvents(b.Db);
        Check(exported.Contains("\"future_field\":{\"keep\":[1,2,3]}"), "an optional field this build does not know is preserved, unchanged, in an export");
        Sql(b.Db, $"INSERT INTO events(command_id,task_id,kind,detail,created_at,event_schema) VALUES('future-2','{a}','zz_skip','{{}}','2099-01-02T00:00:00Z',3)");
        long purge = One<long>(b.Db, "SELECT max(seq) FROM events") + 1;
        Sql(b.Db, $"INSERT INTO events(command_id,task_id,kind,detail,created_at,event_schema) VALUES('future-3','{a}','zz_purge_everything','{{\"irreversible\":true}}','2099-01-03T00:00:00Z',3)");
        var stopped = Replay.StateAt(b.Db, b.Project, long.MaxValue);
        Check(stopped.StoppedAt == purge && stopped.StopReason!.Contains(purge.ToString()) && stopped.Notes.Any(n => n.Contains("zz_skip")) && stopped.Tasks.Single(tA).AcceptedRevision == 1,
              "an unknown event is skipped and reported; an unknown irreversible one stops replay and names its sequence");
    }

    /// <summary>Makes a current-schema database look like the previous schema: events carry no schema number and the Day 6 tables are absent.</summary>
    internal static void Downgrade(string db)
    {
        Sql(db, """
            ALTER TABLE events DROP COLUMN event_schema;
            ALTER TABLE plan_tasks DROP COLUMN after_text;
            DROP TABLE start_queue; DROP TABLE instruction_images; DROP TABLE documents; DROP TABLE document_revisions; DROP TABLE doc_chunks; DROP TABLE doc_terms; DROP TABLE doc_thumbs;
            DROP TABLE doc_answers; DROP TABLE doc_citations; DROP TABLE reconciliations;
            UPDATE meta SET value='10' WHERE key='schema_version';
            """);
    }

    // AH47 ------------------------------------------------------------------------------------------------------------------------------------------
    private static async Task PortabilityChecks(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.CloudTeam(b);
        string task = b.Task("Ship notes", "Write them");
        await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        string package = Path.Combine(folder, "package");
        var manifest = await b.W.ExportWorkspaceAsync(b.Db, package);
        var bytes = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Select(File.ReadAllBytes).SelectMany(x => x).ToArray();
        Check(manifest.Files.Any(f => f.Path == "agenthub.db") && manifest.Files.Any(f => f.Path == "events.jsonl") && Encoding.UTF8.GetString(bytes).IndexOf("sk-test-SYNTHETIC-1234", StringComparison.Ordinal) < 0
              && One<string>(Path.Combine(package, "agenthub.db"), "SELECT credential_status FROM connections") == "none", "the export carries hashes of every artifact and none of the credentials");

        var vault2 = new MemoryVault();
        var fake2 = new FakeWorker("restored-profile", vault2);
        var w2 = new WorkerClient(fake2);
        string target = Path.Combine(folder, "restored");
        var restored = await w2.RestoreWorkspaceAsync(package, target);
        var info = await w2.LoadWorkspaceAsync(target);
        Check(restored.Tasks == manifest.Tasks && restored.Decisions == manifest.Decisions && restored.WorkspaceId != manifest.WorkspaceId && info!.SchemaVersion == ProjectStore.CurrentSchema
              && One<long>(restored.DatabasePath, "SELECT count(*) FROM attempts") == One<long>(b.Db, "SELECT count(*) FROM attempts"), "the package restores into a new profile under a new workspace ID with the same tasks, decisions and attempts");

        // Tampering is caught before anything is activated.
        string tampered = Path.Combine(folder, "tampered");
        CopyDir(package, tampered);
        var db = Path.Combine(tampered, "agenthub.db");
        var raw = File.ReadAllBytes(db); raw[^200] ^= 0xFF; File.WriteAllBytes(db, raw);
        string t3 = Path.Combine(folder, "never");
        var bad = await Refused(() => w2.RestoreWorkspaceAsync(tampered, t3));
        Check(bad.Code == "hash_mismatch" && !File.Exists(Path.Combine(t3, "agenthub.db")), "an altered artifact is refused on its hash, and no workspace appears in the target");
        string interrupted = Path.Combine(folder, "interrupted");
        Portability.BeforeActivation = () => throw new IOException("power cut");
        try { await w2.RestoreWorkspaceAsync(package, interrupted); } catch (WorkerException) { }
        Portability.BeforeActivation = null;
        Check(!File.Exists(Path.Combine(interrupted, "agenthub.db")), "a restore interrupted before activation leaves no half-made workspace");

        // Erase the disposable copy; the original is untouched.
        string secret = "LocalMind.AgentHub/x/y";
        vault2.Write(secret, "synthetic");
        var erased = await w2.EraseWorkspaceAsync(restored.DatabasePath);
        Check(!File.Exists(restored.DatabasePath) && erased.Report.Removed.Contains("agenthub.db") && erased.Report.ExternalCopies.Any(c => c.Contains("Exports and backups")) && erased.Report.ExternalCopies.Any(c => c.Contains("Cloud services")) && File.Exists(b.Db),
              "erasure removes the disposable copy's files, names the external copies that may remain (exports, backups, cloud providers), and leaves the original alone");
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to));
    }

    // AH102 -----------------------------------------------------------------------------------------------------------------------------------------
    private static async Task MigrationChecks(string folder)
    {
        var (b, a, c, output) = await Flow(Path.Combine(folder, "source"));
        await b.W.DecideOutputAsync(b.Db, C(), a, output, 1, 1, "accepted", null);
        int tasks = (int)One<long>(b.Db, "SELECT count(*) FROM tasks"), decisions = (int)One<long>(b.Db, "SELECT count(*) FROM decisions");
        await b.DisposeAsync();
        foreach (var phase in new[] { "preflight", "copy", "verify", "swap" })
        {
            string dir = Path.Combine(folder, phase);
            Directory.CreateDirectory(dir);
            string db = Path.Combine(dir, "agenthub.db");
            using (var src = ProjectStore.OpenRead(b.Db))
            using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            { dst.Open(); src.BackupDatabase(dst); }
            Downgrade(db);
            Migration.InterruptAt = p => { if (p == phase) throw new MigrationInterrupted(p); };
            try { ProjectStore.Load(dir); throw new InvalidOperationException("expected an interruption"); } catch (MigrationInterrupted) { }
            Migration.InterruptAt = null;
            var resolved = Migration.Resolve(dir);
            var info = ProjectStore.Load(dir);
            string expected = phase == "swap" ? "new" : "old";
            Check(resolved.Chosen == expected && info.SchemaVersion == ProjectStore.CurrentSchema && One<long>(db, "SELECT count(*) FROM tasks") == tasks && One<long>(db, "SELECT count(*) FROM decisions") == decisions
                  && !File.Exists(db + ".migrating") && !File.Exists(Path.Combine(dir, "agenthub.migration.json")),
                  $"an upgrade interrupted at {phase}: relaunch chose the {expected} profile, validated it, and kept {tasks} tasks and {decisions} decisions");
        }
        // Between the two renames with a damaged copy: the original is put back.
        string d2 = Path.Combine(folder, "swap-bad");
        Directory.CreateDirectory(d2);
        string db2 = Path.Combine(d2, "agenthub.db");
        using (var src = ProjectStore.OpenRead(b.Db))
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db2, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        { dst.Open(); src.BackupDatabase(dst); }
        Downgrade(db2);
        Migration.InterruptAt = p => { if (p == "swap") throw new MigrationInterrupted(p); };
        try { ProjectStore.Load(d2); } catch (MigrationInterrupted) { }
        Migration.InterruptAt = null;
        File.WriteAllBytes(db2 + ".migrating", [1, 2, 3, 4]);
        var back = Migration.Resolve(d2);
        Check(back.Chosen == "old" && One<long>(db2, "SELECT count(*) FROM tasks") == tasks && One<string>(db2, "SELECT value FROM meta WHERE key='schema_version'") == "10", "if the new copy cannot be verified after the swap began, the original is restored");
        string d3 = Path.Combine(folder, "failing");
        Directory.CreateDirectory(d3);
        string db3 = Path.Combine(d3, "agenthub.db");
        using (var src = ProjectStore.OpenRead(b.Db))
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db3, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        { dst.Open(); src.BackupDatabase(dst); }
        Downgrade(db3);
        ProjectStore.AfterMigrationStep = s => { if (s == 11) throw new InvalidOperationException("disk error"); };
        try { ProjectStore.Load(d3); } catch (InvalidOperationException) { }
        ProjectStore.AfterMigrationStep = null;
        Check(One<string>(db3, "SELECT value FROM meta WHERE key='schema_version'") == "10" && !File.Exists(db3 + ".migrating") && !File.Exists(Path.Combine(d3, "agenthub.migration.json")),
              "a failure inside the upgrade leaves the original untouched and no temporary files behind");
    }

    // AH110 -----------------------------------------------------------------------------------------------------------------------------------------
    private static async Task ForgetChecks(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: r => r.Model == "reviewer-b:2" ? CheckpointI.Text(r, "The code name is ZEBRA-MARKER-7731 [1].") : CheckpointI.Text(r, "ok"));
        string doc = (await b.W.AddDocumentAsync(b.Db, C(), b.Project, "secret-plan.md", "The launch code name is ZEBRA-MARKER-7731 and it stays internal.\n\nNothing else matters.")).Id;
        var png = Png(64, 64, "THUMB-MARKER-5512");
        string image = (await b.W.AddDocumentImageAsync(b.Db, C(), b.Project, new("diagram.png", Convert.ToBase64String(png)))).Id;
        var ask = await b.W.AskDocumentsAsync(b.Db, C(), b.Project, "What is the launch code name?", "reviewer");
        bool before = Contains(b.Db, "ZEBRA-MARKER-7731") && Contains(b.Db, "THUMB-MARKER-5512") && One<long>(b.Db, "SELECT count(*) FROM doc_terms") > 0;
        var forgot = await b.W.ForgetSourceAsync(b.Db, C(), doc);
        var forgotImage = await b.W.ForgetSourceAsync(b.Db, C(), image);
        var citation = (await b.W.ReadAnswersAsync(b.Db, b.Project)).Single().Citations.Single();
        var opened = await b.W.OpenPassageAsync(b.Db, doc, citation.Revision, citation.Start, citation.Length);
        Check(before && !Contains(b.Db, "ZEBRA-MARKER-7731") && !Contains(b.Db, "THUMB-MARKER-5512") && One<long>(b.Db, "SELECT count(*) FROM doc_terms") == 0 && One<long>(b.Db, "SELECT count(*) FROM doc_thumbs") == 0
              && !One<string>(b.Db, $"SELECT packet_text FROM doc_answers WHERE id='{ask.Id}'").Contains("ZEBRA") && !One<string>(b.Db, $"SELECT answer FROM doc_answers WHERE id='{ask.Id}'").Contains("ZEBRA")
              && opened.State == "forgotten" && opened.Text == "" && forgotImage.Report.ThumbnailErased && forgot.Report.RevisionsErased == 1,
              "forgetting a source erases its text, bytes, index, thumbnail and the passages in reusable packets and answers; the raw database file no longer holds them");
        Check(forgot.Report.TransmittedCopies.Any(t => t.Contains("this computer")) && forgot.Report.Remaining.Any(r => r.Contains("Exports made before")) && forgot.Report.Remaining.Any(r => r.Contains("cannot be recalled"))
              && (await RetrieveGap(b)), "the report names what a forget cannot undo (copies already sent, earlier exports, derived work) and the source is no longer retrievable");
        await b.Fake.RestartAsync();
        string package = Path.Combine(folder, "package");
        await b.W.ExportWorkspaceAsync(b.Db, package);
        bool exportClean = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).All(f => !Contains(f, "ZEBRA-MARKER-7731") && !Contains(f, "THUMB-MARKER-5512"));
        Check(exportClean && !Contains(b.Db, "ZEBRA-MARKER-7731"), "after a restart and in a new export, the forgotten content is still absent");
    }

    private static async Task<bool> RetrieveGap(CheckpointI.Bench b)
    {
        var again = await b.W.AskDocumentsAsync(b.Db, C(), b.Project, "What is the launch code name?", "reviewer");
        return (await b.W.ReadAnswersAsync(b.Db, b.Project)).Single(x => x.Id == again.Id).Status == "no_evidence";
    }
    private static bool Contains(string file, string marker)
    {
        var bytes = File.ReadAllBytes(file);
        return Encoding.UTF8.GetString(bytes).Contains(marker, StringComparison.Ordinal) || Encoding.Latin1.GetString(bytes).Contains(marker, StringComparison.Ordinal);
    }

    // AH67 ------------------------------------------------------------------------------------------------------------------------------------------
    private static async Task ReconcileChecks(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.CloudTeam(b, answer: r => new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Partial draft that never finishes", 40, 25), DropAfter: 2, DelayMs: 20));
        string t1 = b.Task("One", "Write"), t2 = b.Task("Two", "Write"), t3 = b.Task("Three", "Write");
        var a1 = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t1, "builder")).Id);
        var a2 = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t2, "builder")).Id);
        var held = Team.Read(b.Db, b.Project).Budget;
        Check(a1.Status == "interrupted" && a1.PartialText.Length > 0 && held.Reserved > 0 && held.UnknownUsage == 2, "an interrupted paid attempt keeps its partial text and its reservation, and its charge is unknown");
        int requests = b.Gateway.Requests;
        var noReceipt = await Refused(() => b.W.ResolveAttemptAsync(b.Db, C(), a1.Id, "confirm"));
        string cmd = C();
        var confirmed = await b.W.ResolveAttemptAsync(b.Db, cmd, a1.Id, "confirm", 0.0123);
        var again = await b.W.ResolveAttemptAsync(b.Db, cmd, a1.Id, "confirm", 0.0123);
        var other = await b.W.ResolveAttemptAsync(b.Db, C(), a1.Id, "confirm", 0.0123);
        var conflict = await Refused(() => b.W.ResolveAttemptAsync(b.Db, C(), a1.Id, "abandon"));
        var afterConfirm = Team.Read(b.Db, b.Project).Budget;
        Check(noReceipt.Code == "receipt_needed" && !confirmed.Duplicate && again.Duplicate && other.Detail == "already recorded" && conflict.Code == "already_resolved" && Math.Abs(afterConfirm.Spent - 0.0123) < 1e-9
              && b.Local.Requests == b.Local.Requests && b.Gateway.Requests == requests && One<long>(b.Db, $"SELECT count(*) FROM reconciliations WHERE attempt_id='{a1.Id}'") == 1 && RunEngine.Get(b.Db, a1.Id).Status == "confirmed",
              "confirming needs a receipt, is recorded once however often it is repeated, cannot be contradicted, and sends no request");
        var abandoned = await b.W.ResolveAttemptAsync(b.Db, C(), a2.Id, "abandon");
        var afterAbandon = Team.Read(b.Db, b.Project).Budget;
        Check(RunEngine.Get(b.Db, a2.Id).Status == "abandoned" && afterAbandon.UnknownUsage == 0 && afterAbandon.Reserved == 0 && b.Gateway.Requests == requests, "abandoning records that nothing was confirmed, releases the reservation, and sends nothing");
        var a3 = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t3, "builder")).Id);
        int beforeRetry = b.Gateway.Requests;
        var retry = await b.W.ResolveAttemptAsync(b.Db, C(), a3.Id, "retry");
        await b.Settled(RunEngine.ForTask(b.Db, t3).Single(x => x.RetryOf == a3.Id).Id);
        Check(b.Gateway.Requests == beforeRetry + 1 && RunEngine.Get(b.Db, a3.Id).Status == "abandoned" && RunEngine.ForTask(b.Db, t3).Count(x => x.RetryOf == a3.Id) == 1, "an explicit retry starts exactly one new attempt and records the choice on the old one");
    }

    // AH103 · AH104 ---------------------------------------------------------------------------------------------------------------------------------
    private static async Task LifecycleChecks(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: r => CheckpointI.Text(r, "This is a long answer that streams slowly while the computer sleeps and wakes again.", 250));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string t1 = b.Task("Sleeper", "Write");
        var run = await b.W.DispatchAsync(b.Db, C(), t1, "builder");
        await Until(() => b.Attempt(run.Id).Status == "streaming");
        var slept = await b.W.LifecycleAsync(b.Db, "sleep");
        Sql(b.Db, $"UPDATE attempts SET lease_expires='2000-01-01T00:00:00.0000000Z' WHERE id='{run.Id}'"); // a long sleep: the lease ran out
        int requests = b.Local.Requests;
        var woke = await b.W.LifecycleAsync(b.Db, "wake");
        var done = await b.Settled(run.Id);
        Check(slept.Refreshed >= 1 && woke.Refreshed >= 1 && woke.NeedsDecision.Count == 0 && done.Status == "completed" && done.PartialText.Contains("wakes again") && b.Local.Requests == requests,
              "a stream in flight across sleep and wake refreshes its lease, keeps its partial text, completes, and nothing is sent twice");

        // The stream breaks while asleep: partial text kept, nothing resent without the owner.
        b.Local.Handle = r => r.Path == "/api/chat" ? new(200, ContentType: "application/x-ndjson", Chunks: ScriptedServer.OllamaText(r.Model, "Half a thought that never ends cleanly"), DropAfter: 2, DelayMs: 40) : new(404, "{}");
        string t2 = b.Task("Broken", "Write");
        var broken = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t2, "builder")).Id);
        int before = b.Local.Requests;
        await b.W.LifecycleAsync(b.Db, "sleep");
        var wake2 = await b.W.LifecycleAsync(b.Db, "wake");
        Check(broken.Status == "interrupted" && broken.PartialText.Length > 0 && wake2.NeedsDecision.Contains(broken.Id) && b.Local.Requests == before,
              "after wake an interrupted attempt is listed for a decision with its partial output, and no request is made until the owner chooses");

        // Closing during a run: stop, finish, and a bounded wait.
        b.Local.Handle = r => r.Path == "/api/tags" ? ScriptedServer.Tags("builder-a:1", "reviewer-b:2") : r.Path == "/api/chat" ? CheckpointI.Text(r, "Slow draft text that takes a while to arrive", 400) : new(200, "{}");
        string t3 = b.Task("Stopper", "Write"), t4 = b.Task("Finisher", "Write");
        var s1 = await b.W.DispatchAsync(b.Db, C(), t3, "builder");
        await Until(() => b.Attempt(s1.Id).Active);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stop = await b.W.ShutdownAsync(b.Db, "stop", 5000);
        clock.Stop();
        var stopped = b.Attempt(s1.Id);
        var next1 = new FakeWorker("relaunch", new MemoryVault());
        var relaunched = await new WorkerClient(next1).AcquireWorkspaceAsync(b.Db);
        await b.W.ReleaseWorkspaceAsync(b.Db);
        var next2 = new FakeWorker("relaunch2", new MemoryVault());
        var again = await new WorkerClient(next2).AcquireWorkspaceAsync(b.Db);
        await next1.DisposeAsync(); await next2.DisposeAsync();
        Check(stop.Stopped == 1 && stopped.Status == "cancelled" && clock.ElapsedMilliseconds < 5000 && !relaunched.Held && again.Held, "stop cancels the run within the bound; the workspace is owned by the next launch only after this one lets go");
        await b.W.AcquireWorkspaceAsync(b.Db);
        var s2 = await b.W.DispatchAsync(b.Db, C(), t4, "builder");
        await Until(() => b.Attempt(s2.Id).Active);
        var quick = await b.W.ShutdownAsync(b.Db, "finish", 100);
        var finish = await b.W.ShutdownAsync(b.Db, "finish", 8000);
        Check(quick.StillRunning >= 1 && finish.StillRunning == 0 && b.Attempt(s2.Id).Status == "completed", "finish waits only as long as the bound allows, then lets the run complete and reports it did");
    }

    // AH51 · AH112 ----------------------------------------------------------------------------------------------------------------------------------
    private static async Task DiagnosticsChecks(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        const string note = "SYNTHETIC-PROVIDER-NOTE-4471", key = "sk-test-SYNTHETIC-1234", path = "C:\\Users\\synthetic-user\\private\\notes.txt", header = "x-api-key: SYNTHKEY-9", title = "SYNTHETIC-TITLE-77", file = "SYNTHETIC-FILE-55", tool = "SYNTH-TOOL-ERR-31";
        await CheckpointI.CloudTeam(b, files: ["notes.txt"]);
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        b.Gateway.Handle = r => r.Path == "/chat/completions"
            ? new(400, new JsonObject { ["error"] = new JsonObject { ["message"] = $"{note} {key} at {path}\nAuthorization: Bearer {key}\n{header}" } }.ToJsonString())
            : ScriptedServer.ModelList(("vendor/model-x", true));
        string task = b.Task(title, "Write");
        await b.W.AddTaskFileAsync(b.Db, C(), task, "notes.txt", file);
        var failed = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        Sql(b.Db, $"UPDATE attempts SET transcript='[{{\"role\":\"tool\",\"content\":\"{tool} {path}\"}}]', stop_reason=stop_reason || ' {note} {path} {header}' WHERE id='{failed.Id}'");
        int requests = b.Gateway.Requests;
        var report = RunDiagnostics.Build(b.Db, failed.Id);
        string text = RunDiagnostics.ToText(report);
        string target = Path.Combine(folder, "report.json");
        RunDiagnostics.Export(b.Db, failed.Id, false, target);
        string exported = File.ReadAllText(target);
        string[] markers = [note, key, path, "synthetic-user", header, "SYNTHKEY", title, file, tool, "Authorization"];
        Check(markers.All(m => !text.Contains(m) && !exported.Contains(m)), "a default diagnostic report omits every seeded marker: provider error text, tool output, paths, headers, keys, titles and file contents");
        var a = report.Attempts.Single();
        Check(a.AttemptId == failed.Id && a.RunId == failed.RunId && a.CommandId.Length > 0 && a.Status == "failed" && a.ErrorCode != null && report.Events.Any(e => e.TaskId == task && e.Kind == "attempt_started") && report.Events.Any(e => e.Kind.StartsWith("attempt_"))
              && report.Counters.Attempts == 1 && report.Ledger.Any(l => l.Kind == "reserve") && report.Ledger.Any(l => l.Kind is "release" or "settle") && !report.IncludesContent,
              "the report holds enough to trace the attempt: correlation IDs, state transitions, the error code and the budget counters");
        var withContent = RunDiagnostics.ToText(RunDiagnostics.Build(b.Db, failed.Id, true));
        Check(withContent.Contains("Content included") && !withContent.Contains(key) && !withContent.Contains("synthetic-user") && !withContent.Contains("SYNTHKEY"), "opting in to content still removes keys, paths and header values");
        Check(Redactor.Clean("Authorization: Bearer sk-abc123456\ncall https://u:p@h.example/x?token=zzz from C:\\Users\\me\\a.txt or /home/me/b and me@example.com").All(ch => true)
              && !Redactor.Clean("Authorization: Bearer sk-abc123456 https://u:p@h.example/x?token=zzz C:\\Users\\me\\a.txt /home/me/b me@example.com").Contains("zzz")
              && !Redactor.Clean("see /home/me/b now").Contains("/home/me"), "the redactor removes headers, bearer tokens, credentials in URLs, query strings, paths and addresses");
        Check(b.Gateway.Requests == requests && b.Gateway.Requests > 0, "building and exporting reports made no outbound request");
    }
}
