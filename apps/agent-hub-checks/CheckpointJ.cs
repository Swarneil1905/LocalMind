using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>
/// Day 5. First the four Day 4 review findings (reviewer target, unknown charges, bound Send, admitted file versions), then the plan,
/// scheduler, graph, navigation and measurement stories. Models are scripted local endpoints; nothing here needs a paid credential.
/// </summary>
internal static partial class CheckpointJ
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    internal static T One<T>(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar() is { } v and not DBNull ? (T)Convert.ChangeType(v, typeof(T)) : default!;
    }

    internal static async Task Until(Func<bool> done, int ms = 30000, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(done))] string? what = null)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end && !done()) await Task.Delay(25);
        if (!done()) throw new InvalidOperationException($"timed out waiting for: {what}");
    }

    internal static async Task<WorkerError> Refused(Func<Task> act)
    {
        try { await act(); }
        catch (WorkerException e) { return e.Error; }
        throw new InvalidOperationException("expected a worker error");
    }

    private static int n;
    internal static string C() => "j" + ++n;

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        await ReviewerTarget(Path.Combine(dir, "reviewer"));
        await UnknownCharge(Path.Combine(dir, "charge"));
        await BoundSend(Path.Combine(dir, "bound"));
        await BoundInstruction(Path.Combine(dir, "instruction"));
        await AdmittedFiles(Path.Combine(dir, "files"));
        await Stress(Path.Combine(dir, "stress"));
        await ReviewFixes(Path.Combine(dir, "review"));
        await PlanChecks(Path.Combine(dir, "plans"));
    }

    // The Day 5 review's intermittent "SQLite Error 8": a reader opening while the last writer closes and cleans up the WAL files.
    private static async Task Stress(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        string task = b.Task("Hot task", "Race the readers");
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                try { RunEngine.ForTask(b.Db, task); }
                catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
        })).ToArray();
        for (int i = 0; i < 1200; i++) await b.W.AddTaskFileAsync(b.Db, "st-" + i, task, "f" + (i % 5) + ".txt", new string('x', 4000) + i);
        stop.Cancel();
        await Task.WhenAll(readers);
        // The Day 5 failure, injected: SQLite error 8 on the first query of a read-only open is retried; a store that keeps failing still fails.
        int tries = 0;
        ProjectStore.BeforeReadProbe = a => { tries = a; if (a < 3) throw new SqliteException("attempt to write a readonly database", 8); };
        var survived = RunEngine.ForTask(b.Db, task);
        bool retried = tries == 3;
        ProjectStore.BeforeReadProbe = a => throw new SqliteException("attempt to write a readonly database", 8);
        bool gaveUp = false;
        try { RunEngine.ForTask(b.Db, task); } catch (SqliteException e) when (e.SqliteErrorCode == 8) { gaveUp = true; }
        ProjectStore.BeforeReadProbe = null;
        // The Day 6 clean-copy failure: the error arrived on a later query of an already open connection, so every read retries, not only the open.
        int reads = 0;
        ProjectStore.BeforeRead = a => { reads++; if (a < 3) throw new SqliteException("attempt to write a readonly database", 8); };
        var later = RunEngine.ForTask(b.Db, task);
        ProjectStore.BeforeRead = null;
        retried &= reads >= 3 && later != null;
        Check(retried && gaveUp && survived != null, "SQLite error 8 on a read-only open is retried briefly and succeeds; a store that keeps failing still reports the error");
        Check(errors.IsEmpty, "reading while another connection writes and closes never surfaces a transient SQLite error" + (errors.IsEmpty ? "" : ": " + errors.First()));
    }

    // Finding 1 · a review is about the draft that was in its packet --------------------------------------------------------------
    private static async Task ReviewerTarget(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        string big = new string('x', 20_000);
        await CheckpointI.LocalTeam(b, answer: r => r.Model == "builder-a:1" ? CheckpointI.Text(r, big) : CheckpointI.Text(r, "APPROVE\nLooks fine.", 300));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string task = b.Task("Long draft", "Write something very long");
        var built = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        var preview = await b.W.PreviewDispatchAsync(b.Db, task, "reviewer");
        var refused = await Refused(() => b.W.DispatchAsync(b.Db, C(), task, "reviewer"));
        Check(built.Status == "completed" && preview.Blocker != null && !preview.PacketText.Contains("Output under review") && refused.Code == "blocked"
              && !b.Local.Seen.Any(x => x.Path == "/api/chat" && x.Model == "reviewer-b:2") && One<long>(b.Db, "SELECT count(*) FROM reviews") == 0,
              "a draft that does not fit the packet blocks the reviewer: zero review requests, zero recommendations");

        await using var m = await CheckpointI.NewBench(Path.Combine(folder, "mid"));
        await CheckpointI.LocalTeam(m, answer: r => r.Model == "builder-a:1" ? CheckpointI.Text(r, "Revision text from " + r.Packet.Length) : CheckpointI.Text(r, "APPROVE\nGood.", 400));
        await m.W.SetProjectSettingsAsync(m.Db, C(), m.Settings with { AutoReview = false });
        string t = m.Task("Short draft", "Write something short");
        await m.Settled((await m.W.DispatchAsync(m.Db, C(), t, "builder")).Id);
        string out1 = One<string>(m.Db, "SELECT id FROM outputs WHERE revision=1");
        var review = await m.W.DispatchAsync(m.Db, C(), t, "reviewer");
        await Until(() => m.Attempt(review.Id).Active);
        var second = await m.Settled((await m.W.DispatchAsync(m.Db, C(), t, "builder")).Id);
        var done = await m.Settled(review.Id);
        var a = m.Attempt(review.Id);
        Check(second.Revision == 2 && done.Status == "completed" && One<string>(m.Db, "SELECT output_id FROM reviews") == out1 && One<string>(m.Db, "SELECT review_output_id FROM attempts WHERE id='" + review.Id + "'") == out1
              && a.Revision == 1, "a newer revision arriving mid-review leaves the recommendation on the output ID and revision the reviewer received");
    }

    // Finding 2 · an unknown charge keeps its reservation --------------------------------------------------------------------------
    private static async Task UnknownCharge(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.CloudTeam(b);
        // Synthetic price: output-only, so one dispatch estimates $0.80 against a $1 cap.
        await b.W.SetPriceAsync(b.Db, C(), b.GatewayConn!, "vendor/model-x", "USD", 0, 0.8 / 600 * 1e6, "synthetic", DateTime.UtcNow.AddHours(-1));
        b.Gateway.Handle = r => r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "No usage reported", null)) : ScriptedServer.ModelList(("vendor/model-x", true));
        string t1 = b.Task("One", "Write it"), t2 = b.Task("Two", "Write it");
        var first = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t1, "builder")).Id);
        var held = Team.Read(b.Db, b.Project).Budget;
        Check(first is { Status: "completed", Cost: null } && held.UnknownUsage == 1 && Math.Abs(held.Reserved - 0.8) < 1e-6 && Math.Abs(held.Remaining!.Value - 0.2) < 1e-6,
              "usage the provider omitted keeps the reservation: the cap shows $0.80 held and $0.20 left");
        await b.Fake.RestartAsync();
        int requests = b.Gateway.Requests;
        var refused = await Refused(() => b.W.DispatchAsync(b.Db, C(), t2, "builder"));
        Check(refused.Code == "blocked" && b.Gateway.Requests == requests && Math.Abs(Team.Read(b.Db, b.Project).Budget.Reserved - 0.8) < 1e-6,
              "after a restart a second $0.80 dispatch is refused with no request sent");
        string cmd = C();
        var r1 = await b.W.ReconcileAttemptAsync(b.Db, cmd, first.Id, 0.5);
        var r2 = await b.W.ReconcileAttemptAsync(b.Db, cmd, first.Id, 0.5);   // the same receipt again
        var r3 = await b.W.ReconcileAttemptAsync(b.Db, C(), first.Id, 0.5);   // delivered again under another command ID
        var wrong = await Refused(() => b.W.ReconcileAttemptAsync(b.Db, C(), first.Id, 0.7));
        var after = Team.Read(b.Db, b.Project).Budget;
        Check(!r1.Duplicate && r2.Duplicate && !r3.Duplicate && wrong.Code == "already_reconciled" && Math.Abs(after.Spent - 0.5) < 1e-9 && after.Reserved == 0 && Math.Abs(after.Remaining!.Value - 0.5) < 1e-9
              && One<long>(b.Db, $"SELECT count(*) FROM budget_ledger WHERE attempt_id='{first.Id}' AND kind='settle'") == 1,
              "reconciling at $0.50 and receiving the receipt twice leaves exactly $0.50 spent and $0.50 remaining");
        await b.Fake.RestartAsync();
        Check(Math.Abs(Team.Read(b.Db, b.Project).Budget.Remaining!.Value - 0.5) < 1e-9, "the reconciled balance survives a restart");
    }

    // Finding 3 · Send is bound to the preview --------------------------------------------------------------------------------------
    private static async Task BoundSend(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.CloudTeam(b, files: ["a.txt"]);
        await b.W.GrantConsentAsync(b.Db, C(), b.Project, b.GatewayConn!, "vendor/model-y", ["a.txt"]);
        await b.W.SetPriceAsync(b.Db, C(), b.GatewayConn!, "vendor/model-y", "USD", 1, 2, "synthetic", DateTime.UtcNow.AddDays(-1));
        string task = b.Task("Notes", "Summarise a.txt");
        await b.W.AddTaskFileAsync(b.Db, C(), task, "a.txt", "Added dark mode");
        var shown = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        int requests = b.Gateway.Requests;

        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.GatewayConn!, "vendor/model-y");
        var swapped = await Refused(() => b.W.DispatchAsync(b.Db, C(), task, "builder", binding: shown.Binding));
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.GatewayConn!, "vendor/model-x");
        await b.W.AddTaskFileAsync(b.Db, C(), task, "a.txt", "Added dark mode and light mode");
        var edited = await Refused(() => b.W.DispatchAsync(b.Db, C(), task, "builder", binding: shown.Binding));
        await b.W.AddTaskFileAsync(b.Db, C(), task, "a.txt", "Added dark mode");
        await b.W.SetPriceAsync(b.Db, C(), b.GatewayConn!, "vendor/model-x", "USD", 5, 9, "synthetic", DateTime.UtcNow.AddMinutes(-1));
        var repriced = await Refused(() => b.W.DispatchAsync(b.Db, C(), task, "builder", binding: shown.Binding));
        var missing = await Refused(async () => { var r = await b.W.CallAsync("dispatch", new DispatchArgs(b.Db, C(), task, "builder", false, false)); if (!r.Ok) throw new WorkerException(r.Error!); });
        Check(swapped.Code == "preview_changed" && edited.Code == "preview_changed" && repriced.Code == "preview_changed" && missing.Code == "preview_required" && b.Gateway.Requests == requests
              && One<long>(b.Db, "SELECT count(*) FROM attempts") == 0, "a reassigned model, an edited file or a new price makes Send refuse; the provider receives zero requests");
        var fresh = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        Check(fresh.Binding != shown.Binding && fresh.Estimate != shown.Estimate && b.Gateway.Requests == requests, "a refreshed preview shows the new terms and still sends nothing");
        var sent = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder", binding: fresh.Binding)).Id);
        Check(sent.Status == "completed" && b.Gateway.Requests > requests && sent.PacketText == fresh.PacketText, "after the owner confirms the refreshed preview the provider receives exactly that packet");
    }

    private static async Task BoundInstruction(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b);
        string task = b.Task("Notes", "Summarise");
        var previews = await b.W.PreviewInstructionAsync(b.Db, task, ["builder"], "Use British spelling.");
        var stale = previews.ToDictionary(p => p.Role, p => p.Binding);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.LocalConn!, "spare:1");
        int chats = b.Local.Seen.Count(x => x.Path == "/api/chat");
        var refused = await Refused(() => b.W.SendInstructionAsync(b.Db, C(), task, ["builder"], "Use British spelling.", stale));
        Check(refused.Code == "preview_changed" && b.Local.Seen.Count(x => x.Path == "/api/chat") == chats && One<long>(b.Db, "SELECT count(*) FROM instructions") == 0 && One<long>(b.Db, "SELECT count(*) FROM attempts") == 0,
              "an instruction sent from a stale preview is recorded nowhere and reaches no model");
    }

    // Finding 4 · the file tool reads only what the attempt was admitted with -------------------------------------------------------
    private static async Task AdmittedFiles(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        int asked = 0;
        string wanted = "big.txt";
        await CheckpointI.LocalTeam(b, answer: r => ++asked % 2 == 1 ? new(200, ContentType: "application/x-ndjson", Chunks: ScriptedServer.OllamaToolCall(r.Model, wanted), DelayMs: 400) : CheckpointI.Text(r, "done"));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string task = b.Task("Notes", "Use the Supplied file “big.txt” and small.txt.");
        await b.W.AddTaskFileAsync(b.Db, C(), task, "big.txt", "TOPSECRET " + new string('y', 20_000));
        await b.W.AddTaskFileAsync(b.Db, C(), task, "small.txt", "original text");
        var preview = await b.W.PreviewDispatchAsync(b.Db, task, "builder");
        var run = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id);
        string toolText = string.Concat(b.Local.Seen.Last(x => x.Path == "/api/chat").Json!["messages"]!.AsArray().Where(m => m?["role"]?.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>()));
        Check(preview.FileNames.SequenceEqual(["small.txt"]) && run.ToolCalls == 1 && toolText.Contains("was not part of this handoff") && !toolText.Contains("TOPSECRET")
              && One<long>(b.Db, $"SELECT count(*) FROM attempt_files WHERE attempt_id='{run.Id}'") == 1,
              "an omitted file whose name appears in ordinary text grants no access: the tool says no and the bytes never leave");

        wanted = "small.txt";
        var started = await b.W.DispatchAsync(b.Db, C(), task, "builder");
        await Until(() => b.Attempt(started.Id).Status is "sending" or "streaming");
        await b.W.AddTaskFileAsync(b.Db, C(), task, "small.txt", "EDITED after the preview");
        var read = await b.Settled(started.Id);
        string tool2 = string.Concat(b.Local.Seen.Last(x => x.Path == "/api/chat").Json!["messages"]!.AsArray().Where(m => m?["role"]?.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>()));
        Check(read.Status == "completed" && tool2.Contains("original text") && !tool2.Contains("EDITED"), "a file edited after the attempt was admitted cannot change what the model reads");
    }
}
