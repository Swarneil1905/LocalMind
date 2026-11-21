using System.Text;
using System.Text.Json.Nodes;
using LocalMind.AgentHub;
using LocalMind.AgentHub.Core;

/// <summary>Day 6 through the window's own commands: the composer, evidence in a message, documents, backup, replay, and recovering interrupted work.</summary>
internal static class CheckpointO
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }
    private static Task Until(Func<bool> f, int ms = 10000, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(f))] string? what = null) => CheckpointJ.Until(f, ms, what);
    private static string C() => CheckpointJ.C();

    private static HubViewModel Hub(WorkerClient w, string folder)
    {
        var hub = CheckpointK.NewHub(w, Path.Combine(folder, "layouts"));
        hub.Drafts = new DraftStore(Path.Combine(folder, "drafts"));
        return hub;
    }

    /// <summary>
    /// Development-only (the Day 6 native journey): a disposable Atlas with sixty tasks (task 45 waits on task 03, so following its blocker scrolls the board), a project document,
    /// one attempt left in "needs your check" (seeded directly, since a real crash cannot be scripted in a window), and three real local models.
    /// </summary>
    public static async Task<string> MakeDay6(string folder, string endpoint, string builder, string reviewer, string planner)
    {
        var fake = new FakeWorker("day6-fixture", new MemoryVault());
        var w = new WorkerClient(fake);
        var info = await w.CreateWorkspaceAsync(folder, "Atlas");
        string db = info.DatabasePath;
        var ids = new List<string>();
        for (int i = 1; i <= 60; i++) ids.Add((await w.CreateTaskAsync(db, C(), info.ProjectId, $"Task {i:00}", $"Work item {i}")).Id);
        await w.AddDependencyAsync(db, C(), ids[44], ids[2], 0);
        string conn = (await w.AddConnectionAsync(db, C(), "ollama", endpoint, "Ollama on this computer")).Id;
        await w.ProbeConnectionAsync(db, conn);
        await w.AssignRoleAsync(db, C(), info.ProjectId, "builder", conn, builder);
        await w.AssignRoleAsync(db, C(), info.ProjectId, "reviewer", conn, reviewer);
        await w.AssignRoleAsync(db, C(), info.ProjectId, "planner", conn, planner);
        await w.AddDocumentAsync(db, C(), info.ProjectId, "refund-policy.md", "Refunds are accepted within 30 days of purchase, with a receipt.\n\nShipping takes five business days inside the country.");
        string now = DateTime.UtcNow.ToString("O");
        CheckpointN.Sql(db, $"INSERT INTO runs(id,task_id,role,created_at) VALUES('seed-run','{ids[1]}','builder','{now}'); " +
                $"INSERT INTO attempts(id,command_id,run_id,project_id,task_id,role,connection_id,provider,requested_model,status,created_at,sent_at,ended_at,stop_reason,partial_text,packet_text,steps,estimate,currency,price_status) " +
                $"VALUES('seed-attempt-0001','seed-cmd','seed-run','{info.ProjectId}','{ids[1]}','builder','{conn}','ollama','{builder}','needs_reconciliation','{now}','{now}','{now}','The lease expired while the request was out. The provider may have accepted it.','Half a draft that was streaming when the app was killed','packet',1,0,'USD','local')");
        await w.ReleaseWorkspaceAsync(db);
        await fake.DisposeAsync();
        return db;
    }

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        await Composer(Path.Combine(dir, "composer"));
        await Evidence(Path.Combine(dir, "evidence"));
        await Docs(Path.Combine(dir, "docs"));
        await DataAndReplay(Path.Combine(dir, "data"));
        await Recovery(Path.Combine(dir, "recovery"));
    }

    // AH84 · AH85 -----------------------------------------------------------------------------------------------------------------------------------
    private static async Task Composer(string folder)
    {
        await using var b = await CheckpointI.NewBench(Path.Combine(folder, "one"));
        await CheckpointI.LocalTeam(b);
        string task = b.Task("Draft", "Write it");
        var two = await b.W.CreateWorkspaceAsync(Path.Combine(folder, "two"), "Two"); // one worker, two workspaces, as in one window
        await b.W.CreateTaskAsync(two.DatabasePath, C(), two.ProjectId, "Elsewhere", "Other work");
        var hub = Hub(b.W, folder);
        await hub.OpenAsync(b.Db, "One");
        await Until(() => hub.CanWrite && hub.Tasks.Count == 1);
        hub.SelectedTask = hub.Tasks[0];
        await hub.ShowTeamCommand.RunAsync();
        await Until(() => hub.ComposerRecipient.Contains("builder-a:1"));
        string pasted = "First line\nSecond line\r\n\r\nFourth, after a blank line";
        hub.InstructionText = pasted;
        string[] composition = ["n", "ni", "nǐ", "你"];
        foreach (var step in composition) hub.InstructionText = step; // an input method rewrites the text as the owner composes
        bool ime = hub.InstructionText == "你" && RunEngine.ForTask(b.Db, task).Count == 0;
        hub.InstructionText = pasted;
        Check(hub.InstructionText == pasted && ime && hub.ComposerRecipient.Contains("To the builder: builder-a:1 on Scripted Ollama (this computer)") && hub.SendInstructionCommand.CanExecute(null),
              "pasted line breaks are kept exactly, a completed composition stays in the editor and sends nothing, and the recipient and its model are shown");

        // Attach, then reopen with a different window and a different workspace in between.
        var png = CheckpointN.Png(800, 600, "draft-shot");
        await hub.AttachImageAsync("draft.png", png);
        hub.RecipientIndex = 1;
        var again = Hub(b.W, folder);
        await again.OpenAsync(b.Db, "One");
        await Until(() => again.CanWrite && again.Tasks.Count == 1);
        await again.ShowTeamCommand.RunAsync();
        await Until(() => again.Attachments.Count == 1);
        Check(again.InstructionText == pasted && again.RecipientIndex == 1 && again.Attachments.Single().Image.Hash == ProjectStore.Hash(png) && again.Attachments.Single().Details.Contains("800×600") && !again.Attachments.Single().Missing,
              "reopening restores the draft's text, recipient and pending attachment, with the attachment's bytes verified by hash");
        await again.OpenAsync(two.DatabasePath, "Two");
        await Until(() => again.CanWrite && again.ProjectHeading == "Two" && again.Tasks.Count == 1);
        bool empty = again.InstructionText == "" && again.Attachments.Count == 0;
        again.InstructionText = "A different message for the other workspace";
        await again.OpenAsync(b.Db, "One");
        await Until(() => again.ProjectHeading == "Atlas" && again.Tasks.Count == 1 && again.InstructionText == pasted);
        Check(empty && again.Attachments.Count == 1 && again.InstructionText == pasted, "switching workspaces never shows or overwrites another workspace's draft");

        // A recipient who no longer has a model must be chosen again.
        string key = One(b.Db, "SELECT id FROM workspace");
        var store = new DraftStore(Path.Combine(folder, "drafts"));
        var file = store.Load(key);
        string project = file.Composer.Keys.Single();
        file.Composer[project] = file.Composer[project] with { Recipient = "planner" };
        store.Save(key, file);
        var third = Hub(b.W, folder);
        await third.OpenAsync(b.Db, "One");
        await Until(() => third.CanWrite && third.Tasks.Count == 1);
        await third.ShowTeamCommand.RunAsync();
        await Until(() => third.RecipientNeedsReselect);
        third.SelectedTask = third.Tasks[0];
        bool blocked = !third.SendInstructionCommand.CanExecute(null) && third.ComposerNote.Contains("has no model");
        third.RecipientIndex = 0;
        Check(blocked && !third.RecipientNeedsReselect && third.SendInstructionCommand.CanExecute(null), "a draft whose recipient has no model cannot be sent until the owner chooses a recipient again");
        foreach (var h in new[] { hub, again, third }) await h.ReleaseWriteLockAsync();
    }

    private static string One(string db, string sql) => CheckpointJ.One<string>(db, sql);

    // AH33 · AH86 -----------------------------------------------------------------------------------------------------------------------------------
    private static async Task Evidence(string folder)
    {
        await using var b = await CheckpointI.NewBench(Path.Combine(folder, "local"));
        CheckpointI.ScriptOllama(b.Local, ["seer:1", "blind:1"], r => CheckpointI.Text(r, "Seen."));
        var inner = b.Local.Handle;
        b.Local.Handle = r => r.Path == "/api/show" ? new(200, r.Json?["model"]?.GetValue<string>() == "seer:1" ? "{\"capabilities\":[\"completion\",\"vision\"]}" : "{\"capabilities\":[\"completion\"]}") : inner(r);
        b.LocalConn = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", b.Local.Url, "Scripted Ollama")).Id;
        await b.W.ProbeConnectionAsync(b.Db, b.LocalConn);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.LocalConn, "seer:1");
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "reviewer", b.LocalConn, "blind:1");
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        b.Task("Layout", "Look at it");
        var hub = Hub(b.W, folder);
        await hub.OpenAsync(b.Db, "Atlas");
        await Until(() => hub.CanWrite && hub.Tasks.Count == 1);
        hub.SelectedTask = hub.Tasks[0];
        await hub.ShowTeamCommand.RunAsync();
        await Until(() => hub.ComposerRecipient.Contains("seer:1"));
        var png = CheckpointN.Png(1024, 768, "vm-shot");
        string? refused = await hub.AttachImageAsync("notes.txt", Encoding.UTF8.GetBytes("not an image"));
        await hub.AttachImageAsync("shot.png", png);
        hub.InstructionText = "What is wrong with this layout?";
        await hub.InspectAttachmentCommand.RunAsync(hub.Attachments[0]);
        await hub.PreviewInstructionCommand.RunAsync();
        Check(refused != null && hub.Attachments.Count == 1 && hub.ImageInspectText.Contains("1024×768") && hub.ImageInspectText.Contains(ProjectStore.Hash(png)) && hub.ImageInspectText.Contains("seer:1") && hub.ImageInspectText.Contains("will receive it") && hub.InstructionPreview.Contains("1 image(s)"),
              "an unsupported file is refused; the owner can inspect the exact image, its size and the destination that would receive it before sending");
        await hub.SendInstructionCommand.RunAsync();
        await Until(() => b.Local.Seen.Any(x => x.Path == "/api/chat" && x.Model == "seer:1"));
        var got = b.Local.Seen.Last(x => x.Path == "/api/chat" && x.Model == "seer:1");
        Check(got.Images.SequenceEqual([Convert.ToBase64String(png)]) && hub.Attachments.Count == 0 && hub.InstructionText == "", "the recipient receives exactly the previewed bytes, and the composer is empty afterwards");

        // Remove before sending: no bytes leave.
        await Until(() => RunEngine.ForTask(b.Db, hub.Tasks[0].Id).All(a => !a.Active));
        await hub.AttachImageAsync("second.png", CheckpointN.Png(10, 10, "to-remove"));
        await hub.RemoveAttachmentCommand.RunAsync(hub.Attachments[0]);
        hub.InstructionText = "Describe it in words instead.";
        await hub.PreviewInstructionCommand.RunAsync();
        await hub.SendInstructionCommand.RunAsync();
        await Until(() => b.Local.Seen.Count(x => x.Path == "/api/chat" && x.Model == "seer:1") == 2);
        Check(b.Local.Seen.Last(x => x.Path == "/api/chat").Images.Count == 0 && !b.Local.Seen.Last(x => x.Path == "/api/chat").Body.Contains("PIXELS"), "an attachment removed before sending is not in the outgoing request");

        // A recipient without vision gets an explanation, not a silent drop.
        hub.RecipientIndex = 1;
        await hub.AttachImageAsync("again.png", CheckpointN.Png(20, 20, "blind"));
        hub.InstructionText = "Look.";
        await hub.PreviewInstructionCommand.RunAsync();
        Check(hub.InstructionPreview.Contains("cannot accept images") || hub.InstructionPreview.Contains("blocked"), "a recipient without vision support is told so in the preview");

        // Cloud: confirmation belongs to these images and this destination.
        await using var c = await CheckpointI.NewBench(Path.Combine(folder, "cloud"));
        await CheckpointI.CloudTeam(c);
        string models = new JsonObject { ["data"] = new JsonArray(Model("vendor/model-x"), Model("vendor/model-y")) }.ToJsonString();
        c.Gateway.Handle = r => r.Path == "/models" ? new(200, models) : r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Looks fine.")) : new(404, "{}");
        await c.W.ProbeConnectionAsync(c.Db, c.GatewayConn!);
        await c.W.GrantConsentAsync(c.Db, C(), c.Project, c.GatewayConn!, "vendor/model-y", []);
        await c.W.SetPriceAsync(c.Db, C(), c.GatewayConn!, "vendor/model-y", "USD", 1, 2, "synthetic", DateTime.UtcNow.AddDays(-1));
        c.Task("Cloud look", "Look");
        var ch = Hub(c.W, folder);
        await ch.OpenAsync(c.Db, "Atlas");
        await Until(() => ch.CanWrite && ch.Tasks.Count == 1);
        ch.SelectedTask = ch.Tasks[0];
        await ch.ShowTeamCommand.RunAsync();
        await Until(() => ch.ComposerRecipient.Contains("vendor/model-x"));
        var shot = CheckpointN.Png(640, 480, "cloud-shot");
        await ch.AttachImageAsync("c.png", shot);
        ch.InstructionText = "Look.";
        await ch.PreviewInstructionCommand.RunAsync();
        int chats = c.Gateway.Seen.Count(x => x.Path == "/chat/completions");
        bool needs = ch.InstructionPreview.Contains("needs your confirmation") && ch.ConfirmImagesCommand.CanExecute(null);
        await ch.SendInstructionCommand.RunAsync();
        bool refusedSend = c.Gateway.Seen.Count(x => x.Path == "/chat/completions") == chats;
        await ch.ConfirmImagesCommand.RunAsync();
        bool ready = ch.InstructionPreview.Contains("ready");
        ch.RecipientIndex = 1; ch.RecipientIndex = 0; // choosing a recipient again clears confirmations
        await ch.PreviewInstructionCommand.RunAsync();
        bool cleared = ch.InstructionPreview.Contains("needs your confirmation");
        await ch.ConfirmImagesCommand.RunAsync();
        await ch.SendInstructionCommand.RunAsync();
        await Until(() => c.Gateway.Seen.Count(x => x.Path == "/chat/completions") == chats + 1);
        Check(needs && refusedSend && ready && cleared && c.Gateway.Seen.Last(x => x.Path == "/chat/completions").Images.SequenceEqual([Convert.ToBase64String(shot)]),
              "images for a cloud model are refused until confirmed; the confirmation is dropped when the recipient is chosen again; after confirming, the cloud receives exactly those bytes");
        await hub.ReleaseWriteLockAsync(); await ch.ReleaseWriteLockAsync();
    }

    private static JsonNode Model(string id) => new JsonObject
    {
        ["id"] = id, ["architecture"] = new JsonObject { ["input_modalities"] = new JsonArray("text", "image"), ["output_modalities"] = new JsonArray("text") }, ["supported_parameters"] = new JsonArray("max_tokens"),
    };

    // AH34 in the window ---------------------------------------------------------------------------------------------------------------------------
    private static async Task Docs(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: r => r.Model == "reviewer-b:2" ? CheckpointI.Text(r, "Refunds are accepted for 30 days [1].") : CheckpointI.Text(r, "ok"));
        var hub = Hub(b.W, folder);
        await hub.OpenAsync(b.Db, "Atlas");
        await Until(() => hub.CanWrite);
        await hub.ShowDocsCommand.RunAsync();
        hub.NewDocName = "policy.md"; hub.NewDocText = "Refunds are accepted within 30 days of purchase.\n\nShipping takes five business days.";
        await hub.AddDocCommand.RunAsync();
        await Until(() => hub.DocRows.Count == 1);
        hub.AskText = "How long is the refund window?";
        await hub.AskDocsCommand.RunAsync();
        await Until(() => hub.AnswerRows.Count == 1);
        var answer = hub.AnswerRows[0];
        await hub.OpenCitationCommand.RunAsync(answer.Citations.Single(c => c.Cited));
        Check(answer.Basis.Contains("from the passages below") && hub.PassageText.Contains("【Refunds are accepted within 30 days of purchase.】") && hub.PassageText.Contains("the current one"), "the window shows an answer with its citations and opens the cited passage at its revision");
        hub.SelectedDoc = hub.DocRows[0];
        hub.NewDocText = "Refunds close after 14 days.";
        await hub.EditDocCommand.RunAsync();
        await Until(() => hub.DocRows[0].Revision == 2);
        await hub.OpenCitationCommand.RunAsync(answer.Citations.Single(c => c.Cited));
        Check(hub.PassageText.Contains("30 days") && hub.PassageText.Contains("revision 2"), "after an edit, the old citation still opens the old words and says the document has moved on");
        hub.SelectedDoc = hub.DocRows[0];
        await hub.ForgetDocCommand.RunAsync();
        await Until(() => hub.DocRows[0].State == "forgotten");
        Check(hub.ForgetReportText.Contains("What remains") && hub.ForgetReportText.Contains("cannot be recalled"), "forgetting from the window explains what a forget cannot undo");
        await hub.ReleaseWriteLockAsync();
    }

    // AH47 · AH59 · AH51 in the window --------------------------------------------------------------------------------------------------------------
    private static async Task DataAndReplay(string folder)
    {
        var (b, a, c, output) = await Flow(folder);
        await using var _ = b;
        await b.W.DecideOutputAsync(b.Db, C(), a, output, 1, 1, "accepted", null);
        var hub = Hub(b.W, folder);
        await hub.OpenAsync(b.Db, "Atlas");
        await Until(() => hub.CanWrite && hub.Tasks.Count == 2);
        // Replay: no writes, no provider.
        var ops = new List<string>();
        b.Fake.BeforeHandle = r => { ops.Add(r.Op); return Task.CompletedTask; };
        int requests = b.Local.Requests;
        await hub.EnterReplayCommand.RunAsync();
        await Until(() => hub.Replaying && hub.ReplayLines.Count > 0);
        bool readOnly = !hub.CanWrite && !hub.MoveTaskCommand.CanExecute("done") && !hub.SendInstructionCommand.CanExecute(null);
        hub.SeekReplay(0);
        await Until(() => hub.ReplayPosition == 0 && hub.ReplayHeading.Contains("After event"));
        string first = string.Join("|", hub.ReplayLines);
        await hub.ReplayStepCommand.RunAsync(1);
        await hub.ReplayStepCommand.RunAsync(1000);
        await Until(() => hub.ReplayPosition == hub.ReplayRows.Count - 1);
        await hub.LeaveReplayCommand.RunAsync();
        b.Fake.BeforeHandle = null;
        Check(readOnly && first.Length > 0 && ops.All(o => o.StartsWith("replay")) && b.Local.Requests == requests && hub.CanWrite && !hub.Replaying,
              "replay in the window is read-only while it lasts, moves through the timeline, sends no provider request and no write, and hands editing back when left");

        // Export, restore, erase from the window.
        string parent = Path.Combine(folder, "exports");
        Directory.CreateDirectory(parent);
        int picks = 0;
        hub.PickFolder = () => Task.FromResult<string?>(picks++ switch { 0 => parent, 1 => Directory.GetDirectories(parent, "agenthub-export-*").Single(), _ => parent });
        await hub.ExportWorkspaceCommand.RunAsync();
        bool exported = hub.DataStatus.Contains("Exported 2 tasks") && Directory.GetDirectories(parent, "agenthub-export-*").Length == 1;
        await hub.RestoreWorkspaceCommand.RunAsync();
        string restored = Directory.GetDirectories(parent, "restored-*").Single();
        Check(exported && hub.DataStatus.Contains("Restored 2 tasks") && File.Exists(Path.Combine(restored, "agenthub.db")), "export and restore work from the window, each reporting what it did");
        var disposable = Hub(new WorkerClient(new FakeWorker("restored-profile", new MemoryVault())), folder);
        await disposable.OpenWorkspaceFolderAsync(restored);
        await Until(() => disposable.CanWrite && disposable.Tasks.Count == 2);
        disposable.ConfirmErase = _ => Task.FromResult(true);
        await disposable.EraseWorkspaceCommand.RunAsync();
        await Until(() => !File.Exists(Path.Combine(restored, "agenthub.db")));
        Check(disposable.DataStatus.Contains("Exports and backups") && File.Exists(b.Db), "erasing the disposable copy from the window lists what it cannot reach and leaves the original");

        // Diagnostics preview.
        hub.AttemptRows.Clear();
        hub.SelectedTask = hub.Tasks.First(t => t.Title == "Write");
        await Until(() => hub.AttemptRows.Count > 0);
        await hub.PreviewDiagnosticsCommand.RunAsync();
        Check(hub.DiagnosticsPreview.Contains("attemptId") && !hub.DiagnosticsPreview.Contains("Write it") && !hub.DiagnosticsPreview.Contains("Draft text"), "the diagnostic preview traces the attempt and holds no project text by default");
        await hub.ReleaseWriteLockAsync();
    }

    private static async Task<(CheckpointI.Bench B, string A, string C, string Out)> Flow(string folder)
    {
        var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: r => CheckpointI.Text(r, "Draft text"));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = false });
        string a = b.Task("Write", "Write it"), c = b.Task("Publish", "Publish it");
        await b.W.UpdateTaskAsync(b.Db, C(), a, 0, reviewRequired: true);
        await b.W.AddDependencyAsync(b.Db, C(), c, a, 0);
        await b.Settled((await b.W.DispatchAsync(b.Db, C(), a, "builder")).Id);
        return (b, a, c, CheckpointJ.One<string>(b.Db, $"SELECT id FROM outputs WHERE task_id='{a}'"));
    }

    // AH67 · AH103 · AH104 in the window ------------------------------------------------------------------------------------------------------------
    private static async Task Recovery(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.CloudTeam(b, answer: r => new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Partial draft that never finishes", 40, 25), DropAfter: 2, DelayMs: 20));
        string t1 = b.Task("One", "Write"), t2 = b.Task("Two", "Write");
        var a1 = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t1, "builder")).Id);
        var a2 = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t2, "builder")).Id);
        var hub = Hub(b.W, folder);
        await hub.OpenAsync(b.Db, "Atlas");
        await Until(() => hub.CanWrite && hub.Tasks.Count == 2);
        hub.SelectedTask = hub.Tasks.First(t => t.Title == "One");
        await Until(() => hub.AttemptRows.Count == 1);
        var row = hub.AttemptRows[0];
        Check(row.Uncertain && row.Intended.Contains("builder") && row.Receipt.Contains("No receipt") && row.Receipt.Contains("partial output were kept") && row.CanResolve && row.CanAbandon,
              "the run inspector shows the attempt, its intended effect and the receipt (or the lack of one) together, with the three choices");
        int requests = b.Gateway.Requests;
        hub.ReceiptText = "";
        await hub.ConfirmAttemptCommand.RunAsync(row);
        bool needReceipt = hub.Notice != null && hub.Notice.Contains("receipt");
        hub.ReceiptText = "0.0200";
        await hub.ConfirmAttemptCommand.RunAsync(row);
        await Until(() => hub.AttemptRows.Count == 1 && hub.AttemptRows[0].A.Status == "confirmed");
        Check(needReceipt && hub.AttemptRows[0].Decision.Contains("confirmed") && Math.Abs(Team.Read(b.Db, b.Project).Budget.Spent - 0.02) < 1e-9 && b.Gateway.Requests == requests, "confirming from the window needs a receipt, records it once, updates the budget, and sends nothing");
        hub.SelectedTask = hub.Tasks.First(t => t.Title == "Two");
        await Until(() => hub.AttemptRows.Count == 1 && hub.AttemptRows[0].A.Id == a2.Id);
        await hub.AbandonAttemptCommand.RunAsync(hub.AttemptRows[0]);
        await Until(() => hub.AttemptRows[0].A.Status == "abandoned");
        Check(hub.AttemptRows[0].Decision.Contains("abandoned") && b.Gateway.Requests == requests && Team.Read(b.Db, b.Project).Budget.UnknownUsage == 0, "abandoning from the window releases the reservation and sends nothing");

        // The Day 5 unresolved charge: a finished attempt whose usage the provider never reported.
        b.Gateway.Handle = r => r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Done, but no usage", null)) : ScriptedServer.ModelList(("vendor/model-x", true));
        string t3 = b.Task("Three", "Write");
        var done = await b.Settled((await b.W.DispatchAsync(b.Db, C(), t3, "builder")).Id);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.Tasks.Count == 3 && hub.CanWrite);
        hub.SelectedTask = hub.Tasks.First(t => t.Title == "Three");
        await Until(() => hub.AttemptRows.Count > 0);
        var unreported = hub.AttemptRows[0];
        hub.ReceiptText = "0.0150";
        await hub.RecordChargeCommand.RunAsync(unreported);
        await Until(() => hub.AttemptRows[0].A.Cost is { } cost && Math.Abs(cost - 0.015) < 1e-9);
        Check(unreported.CanRecordCharge && done.Cost == null && Math.Abs(Team.Read(b.Db, b.Project).Budget.Spent - 0.035) < 1e-9, "a finished attempt with an unreported charge can be reconciled from the window, once");

        // Wake: a write whose answer was lost.
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.Tasks.Count == 3 && hub.CanWrite);
        hub.SelectedTask = hub.Tasks.First(t => t.Title == "Three");
        int moves = 0;
        b.Fake.AfterHandle = r => { if (r.Op == "moveTask") { moves++; throw new WorkerException(new("outcome_unknown", "The worker stopped while handling the move.", "Check, then repeat.")); } return Task.CompletedTask; };
        await hub.MoveTaskCommand.RunAsync("in_progress");
        b.Fake.AfterHandle = null;
        bool uncertain = hub.Notice != null && hub.Notice.Contains("worker stopped");
        await hub.WakeAsync();
        await Until(() => hub.Tasks.First(t => t.Title == "Three").Status == "in progress");
        Check(uncertain && moves == 1 && hub.Notice == null, "after wake, a write whose answer was lost is checked with a read-only status lookup and shown as applied, without being sent again");

        // Closing during a run, with a pending review comment.
        b.Gateway.Handle = r => r.Path == "/chat/completions" ? new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "A slow draft that is still arriving when the window closes", 40, 25), DelayMs: 500) : ScriptedServer.ModelList(("vendor/model-x", true));
        string t4 = b.Task("Four", "Write");
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.Tasks.Count == 4 && hub.CanWrite);
        var running = await b.W.DispatchAsync(b.Db, C(), t4, "builder");
        await Until(() => b.Attempt(running.Id).Active);
        hub.SelectedTask = hub.Tasks.First(t => t.Title == "Four");
        await Until(() => hub.AttemptRows.Count > 0 && hub.HasActiveRuns);
        string action = await hub.ClosePolicyAsync("stop");
        var stopped = b.Attempt(running.Id);
        Check(action == "close" && stopped.Status == "cancelled" && !hub.HasActiveRuns, "choosing Stop on close cancels the running attempt within the bound and lets go of the workspace");
        await hub.ReleaseWriteLockAsync();
    }
}
