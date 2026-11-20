using Avalonia;
using LocalMind.AgentHub;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 3 checkpoint B through the view model the window uses: the revision the user has open, the inbox, the team channel and handoff previews.</summary>
internal static class CheckpointG
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

    /// <summary>Waits for a condition and fails at once, naming it, when it never holds, so fixture drift is reported at the first unmet expectation.</summary>
    private static async Task Until(Func<bool> done, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(done))] string? what = null)
    {
        for (int i = 0; i < 200 && !done(); i++) await Task.Delay(30);
        if (!done()) throw new InvalidOperationException($"timed out waiting for: {what}");
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static int n;
    private static string C() => "g" + ++n;

    private static HubViewModel NewHub() => new() { Workspaces = new WorkspaceService(new WorkerClient(new FakeWorker())), PickFile = () => Task.FromResult<string?>(null) };

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        await Place(Path.Combine(dir, "place"));
        await Journey(Path.Combine(dir, "journey"));
        await TwoClients(Path.Combine(dir, "two"));
        await Selection(Path.Combine(dir, "selection"));
        await Idle(Path.Combine(dir, "idle"));
        await Image(Path.Combine(dir, "image"));
    }

    private static string TaskOf(string db, string project, string title) =>
        One<string>(db, $"SELECT t.id FROM tasks t JOIN projects p ON p.id=t.project_id WHERE p.name='{project}' AND t.title='{title}'");

    // AH23 and AH31 · what is on screen belongs to the selection ---------------------------------------------------------------
    private static async Task Selection(string folder)
    {
        string db = MakeHandoffB(folder);
        var hub = NewHub();
        await hub.OpenWorkspaceFolderAsync(folder);
        await Until(() => hub.InboxCount == 3);
        string atlasDraft = TaskOf(db, "Atlas", "Draft"), betaDraft = TaskOf(db, "Beta", "Draft");
        var atlasRow = hub.InboxRows.Single(r => r.Item.TaskId == atlasDraft);
        var betaRow = hub.InboxRows.Single(r => r.Item.TaskId == betaDraft);
        Check(hub.InboxRows.Any(r => r.Item.ProjectName == "Atlas" && r.Item.TaskTitle == "Logo" && r.Item.Revision == 1), "the fixture also has Atlas's Logo waiting, so rows are chosen by task ID, not by project");

        // A finished preview does not follow the user to another project.
        await hub.OpenInboxItemCommand.RunAsync(atlasRow);
        await Until(() => hub.SelectedTask?.Id == atlasDraft && hub.Review?.OutputId != null);
        await hub.PreviewHandoffCommand.RunAsync("reviewer");
        Check(hub.HasHandoff && hub.HandoffText.Contains("ALPHA-MARK") && hub.HandoffSummary.Contains("estimated at 4 characters per token"), "Atlas's reviewer packet is previewed, and the estimate is labelled as an estimate");
        await hub.OpenInboxItemCommand.RunAsync(betaRow);
        await Until(() => hub.SelectedTask?.Id == betaDraft && hub.Review?.OutputId != null);
        Check(hub.ProjectHeading == "Beta" && !hub.HasHandoff && !hub.HandoffText.Contains("ALPHA-MARK") && !hub.HandoffSummary.Contains("Atlas"), "after opening Beta's Draft the inspector holds no packet from Atlas");
        await hub.PreviewHandoffCommand.RunAsync("reviewer");
        Check(hub.HasHandoff && hub.HandoffText.Contains("BETA-MARK") && !hub.HandoffText.Contains("ALPHA-MARK"), "Beta's own preview shows only Beta's evidence");

        // A preview still being built is dropped if the user moves on.
        var building = hub.PreviewHandoffCommand.RunAsync("reviewer");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Spec");
        await building;
        Check(!hub.HasHandoff, "a packet still loading when the task changes is discarded");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Draft");
        building = hub.PreviewHandoffCommand.RunAsync("reviewer");
        hub.SelectedProject = hub.Projects.Single(p => p.Name == "Atlas");
        await building;
        await Until(() => hub.ProjectHeading == "Atlas" && hub.Tasks.Count == 4);
        Check(!hub.HasHandoff && !hub.HandoffText.Contains("BETA-MARK"), "a packet still loading when the project changes is discarded");

        // An inbox row names a revision, and that is the revision it opens.
        await hub.OpenInboxItemCommand.RunAsync(betaRow); // somewhere else
        await Until(() => hub.SelectedTask?.Id == betaDraft);
        TaskStore.RunRole(db, C(), atlasDraft, "builder", null, NewId); // Atlas revision 3 arrives; the held row still says revision 2
        await hub.OpenInboxItemCommand.RunAsync(atlasRow);
        await Until(() => hub.SelectedTask?.Id == atlasDraft && hub.Review?.OutputId != null);
        Check(hub.Review is { Revision: 2, LatestRevision: 3, IsStale: true } && !hub.CanDecide && !hub.AcceptOutputCommand.CanExecute(null) && hub.StaleNotice.Contains("Revision 3 has arrived"),
              "an inbox row for revision 2 opens revision 2 with approval off and a path to revision 3, even though revision 3 arrived first");
        await hub.AcceptOutputCommand.RunAsync();
        Check(One<long>(db, "SELECT count(*) FROM decisions WHERE task_id='" + atlasDraft + "' AND content_revision>=2") == 0, "approving from that screen does nothing");
        await hub.OpenLatestRevisionCommand.RunAsync();
        await Until(() => hub.Review?.Revision == 3);
        Check(hub.CanDecide, "the explicit path opens revision 3, which can be decided");

        // A fresh row leads to the revision printed on it, even when the inspector was left on an older one.
        await hub.OpenInboxItemCommand.RunAsync(atlasRow);
        await Until(() => hub.Review?.Revision == 2);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.InboxRows.Any(r => r.Item.TaskId == atlasDraft && r.Item.Revision == 3));
        Check(hub.Review?.Revision == 2, "refreshing keeps the revision the inspector was left on");
        var fresh = hub.InboxRows.Single(r => r.Item.TaskId == atlasDraft);
        await hub.OpenInboxItemCommand.RunAsync(fresh);
        await Until(() => hub.Review?.Revision == 3);
        Check(fresh.Item.Revision == 3 && hub.Review is { Revision: 3, IsStale: false } && hub.CanDecide, "a current row opens the revision printed on it, not the one the inspector was left on");
        await hub.ReleaseWriteLockAsync();
    }

    // AH26 · an idle window keeps up -----------------------------------------------------------------------------------------
    private static async Task Idle(string folder)
    {
        string db = MakeHandoffB(folder);
        var owner = NewHub();
        await owner.OpenWorkspaceFolderAsync(folder);
        var watcher = NewHub();
        await watcher.OpenWorkspaceFolderAsync(folder);
        await Until(() => watcher.ChannelRows.Count > 0);
        await watcher.ShowChannelCommand.RunAsync();
        Check(watcher.ReadOnlyReason != null && owner.ReadOnlyReason == null && watcher.ChannelVisible, "the watching window is read-only and has the channel open");
        watcher.StartPolling(TimeSpan.FromMilliseconds(40));
        int Count(string text) => watcher.ChannelRows.ToArray().Count(r => r.Message.Text.Contains(text));
        int before = watcher.ChannelRows.Count;

        await owner.CreateTaskAsync("Late task", "");
        await Until(() => Count("Late task") == 1);
        await Task.Delay(300);
        Check(Count("Late task") == 1 && watcher.ChannelRows.Count == before + 1, "an event written by the owner appears in the idle window once, with no action from the user");

        await watcher.RefreshCommand.RunAsync(); // a reconnect
        await owner.CreateTaskAsync("Second late", "");
        await Until(() => Count("Second late") == 1);
        await Task.Delay(300);
        var seqs = watcher.ChannelRows.ToArray().Select(r => r.Message.Seq).ToList();
        Check(Count("Late task") == 1 && Count("Second late") == 1 && seqs.Distinct().Count() == seqs.Count && seqs.SequenceEqual(seqs.Order()), "across a reconnect nothing repeats and nothing is missed, in order");

        watcher.SelectedProject = watcher.Projects.Single(p => p.Name == "Beta");
        owner.SelectedProject = owner.Projects.Single(p => p.Name == "Beta");
        await Until(() => owner.ProjectHeading == "Beta" && watcher.ProjectHeading == "Beta" && watcher.ChannelRows.Count > 0 && watcher.ChannelRows.ToArray().All(r => r.Message.ProjectId == owner.Projects.Single(p => p.Name == "Beta").Id));
        await owner.CreateTaskAsync("Beta late", "");
        await Until(() => Count("Beta late") == 1);
        Check(Count("Beta late") == 1 && Count("Late task") == 0 && watcher.ChannelRows.ToArray().Select(r => r.Message.ProjectId).Distinct().Count() == 1, "after switching project the idle window shows only that project, and its new event arrives on its own");

        watcher.SelectedProject = watcher.Projects.Single(p => p.Name == "Atlas");
        await Until(() => Count("Second late") == 1);
        await Task.Delay(200);
        var again = watcher.ChannelRows.ToArray().Select(r => r.Message.Seq).ToList();
        Check(Count("Beta late") == 0 && Count("Late task") == 1 && again.Distinct().Count() == again.Count, "switching back shows the first project's messages once each");
        watcher.StopPolling();
        await owner.ReleaseWriteLockAsync();
        await watcher.ReleaseWriteLockAsync();
    }

    // AH32 · a real image is decoded and shown; a header-only file says why it is not --------------------------------------------
    internal static async Task Image(string folder)
    {
        bool platform = true;
        try { Avalonia.AppBuilder.Configure<HubApp>().UsePlatformDetect().SetupWithoutStarting(); }
        catch (Exception ex) { platform = false; Console.WriteLine("NOTE no Avalonia platform in this harness: " + ex.GetType().Name); }
        SynchronizationContext.SetSynchronizationContext(null); // setup installs a context that nothing pumps, so awaits would never resume
        var hub = NewHub();
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        await hub.CreateTaskAsync("Logo", "");
        await hub.CreateTaskAsync("Broken", "");
        string logo = hub.Tasks.Single(t => t.Title == "Logo").Id, broken = hub.Tasks.Single(t => t.Title == "Broken").Id;
        TaskStore.AddFixtureOutput(db, C(), logo, "image", CheckpointF.RealPng(48, 24), "builder (fixture)", NewId);
        TaskStore.AddFixtureOutput(db, C(), broken, "image", CheckpointF.Png(64, 32), "builder (fixture)", NewId);
        await hub.RefreshCommand.RunAsync();
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Logo");
        await Until(() => hub.Review?.OutputId != null);
        await Until(() => hub.HasOutputImage || hub.OutputNote.Contains("could not be decoded"));
        Check(platform && hub.HasOutputImage && hub.OutputImage!.PixelSize.Width == 48 && hub.OutputImage.PixelSize.Height == 24 && hub.OutputNote.Contains("48×24"),
              "a real PNG is decoded into a displayed bitmap of its true size");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Broken");
        await Until(() => hub.Review?.OutputId != null && hub.Review.OutputId != null && !hub.HasOutputImage);
        await Until(() => hub.OutputNote.Contains("could not be decoded"));
        Check(!hub.HasOutputImage && hub.OutputNote.Contains("could not be decoded") && hub.OutputText == "", "a header-only PNG passes the size check, fails to decode, and the inspector says so");
        await hub.ReleaseWriteLockAsync();
    }

    // AH31 · the same pending decision open in two windows ------------------------------------------------------------------
    private static async Task TwoClients(string folder)
    {
        var first = NewHub();
        await first.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        await first.CreateTaskAsync("Draft", "");
        first.SelectedTask = first.Tasks.Single();
        await first.ToggleReviewCommand.RunAsync();
        await Until(() => first.SelectedTask?.ReviewRequired == true);
        await first.AskBuilderCommand.RunAsync();
        await Until(() => first.CanDecide);
        var second = NewHub();
        await second.OpenWorkspaceFolderAsync(folder);
        second.SelectedTask = second.Tasks.Single();
        await Until(() => second.InboxCount == 1 && second.Review?.OutputId != null);
        Check(first.InboxCount == 1 && second.InboxCount == 1 && second.ReadOnlyReason != null && !second.AcceptOutputCommand.CanExecute(null) && first.CanDecide,
              "two windows show the same waiting decision; only the one that owns the workspace can decide it");
        await first.AcceptOutputCommand.RunAsync();
        await Until(() => first.Review?.AcceptedBy != null);
        await second.RefreshCommand.RunAsync();
        await Until(() => second.InboxCount == 0 && second.Review?.AcceptedBy != null);
        Check(second.InboxCount == 0 && second.StateLine.StartsWith("Accepted by owner") && One<long>(db, "SELECT count(*) FROM decisions") == 1 && One<long>(db, "SELECT count(*) FROM events WHERE kind='output_accepted'") == 1,
              "the second window learns the outcome on refresh: one decision, one audit event, an empty inbox");
        await first.ReleaseWriteLockAsync();
        await second.ReleaseWriteLockAsync();
    }

    /// <summary>Development-only (the Handoff B walkthrough): Atlas and Beta, same task titles, each with Draft revision 2 waiting after a request for changes.</summary>
    public static string MakeHandoffB(string folder)
    {
        var info = ProjectStore.Create(folder, "Atlas", NewId);
        string db = info.DatabasePath;
        string beta = NewId();
        Sql(db, $"INSERT INTO projects VALUES('{beta}','Beta')");
        foreach (var (project, marker) in new[] { (info.ProjectId, "ALPHA-MARK"), (beta, "BETA-MARK") })
        {
            string spec = TaskStore.CreateTask(db, C(), project, "Spec", $"Criteria {marker}", NewId).Id;
            string draft = TaskStore.CreateTask(db, C(), project, "Draft", $"Write the launch note for {marker}", NewId).Id;
            string qa = TaskStore.CreateTask(db, C(), project, "QA", null, NewId).Id;
            TaskStore.AddDependency(db, C(), draft, spec, 0);
            TaskStore.UpdateTask(db, C(), draft, 1, null, null, null, reviewRequired: true);
            TaskStore.AddDependency(db, C(), qa, draft, 0);
            TaskStore.MoveTask(db, C(), spec, "in_progress", 0);
            TaskStore.MoveTask(db, C(), spec, "done", 1);
            var o1 = TaskStore.RunRole(db, C(), draft, "builder", null, NewId);
            TaskStore.RunRole(db, C(), draft, "reviewer", null, NewId);
            TaskStore.DecideOutput(db, C(), draft, o1.Id, 1, 1, "changes_requested", "Add a one-line summary.", "owner", NewId);
            TaskStore.RunRole(db, C(), draft, "builder", null, NewId);
        }
        // A real image waiting for review, for the native image preview.
        string logo = TaskStore.CreateTask(db, C(), info.ProjectId, "Logo", "A small gradient", NewId).Id;
        TaskStore.UpdateTask(db, C(), logo, 0, null, null, null, reviewRequired: true);
        TaskStore.AddFixtureOutput(db, C(), logo, "image", CheckpointF.RealPng(96, 48), "builder (fixture)", NewId);
        return db;
    }

    // AH90 · the decision beside the change; the user's place survives a newer revision ---------------------------------------
    private static async Task Place(string folder)
    {
        var hub = NewHub();
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        await hub.CreateTaskAsync("Draft", "Write the summary");
        hub.SelectedTask = hub.Tasks.Single();
        string task = hub.SelectedTask!.Id;
        await hub.ToggleReviewCommand.RunAsync();
        await Until(() => hub.SelectedTask?.ReviewRequired == true);
        await hub.AskBuilderCommand.RunAsync();
        await Until(() => hub.HasOutput);
        Check(hub.ChangeSummary.Contains("first revision") && hub.ChecksText.StartsWith("No checks were run") && hub.EffectText.Contains("will be marked Done"),
              "revision 1 says there is nothing to compare, that no checks were run, and what accepting does");
        await hub.AskReviewerCommand.RunAsync();
        await hub.RequestChangesCommand.RunAsync(); // no dialog in the harness: nothing happens
        var r1 = hub.Review!;
        await hub.AskBuilderCommand.RunAsync();
        await Until(() => hub.Review?.Revision == 2);
        Check(hub.ChangeHeading == "What changed since revision 1" && hub.ChangeSummary.Contains("Summary (added)") && hub.ChangeLines.Any(l => l.StartsWith("+ Summary:")) && hub.CanDecide,
              "revision 2 shows what changed since revision 1 beside the decision");
        await hub.ReviewDecisionCommand.RunAsync();
        Check(One<long>(db, "SELECT count(*) FROM decisions") == 0, "opening the comparison and the decision surface records no decision");

        // Revision 3 arrives (another process, or the builder) while revision 2 is open.
        TaskStore.RunRole(db, C(), task, "builder", null, NewId);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.Review?.LatestRevision == 3);
        Check(hub.Review is { Revision: 2, IsStale: true } && hub.IsStale && !hub.CanDecide && !hub.AcceptOutputCommand.CanExecute(null) && hub.StaleNotice.Contains("Revision 3"),
              "revision 3 arriving keeps revision 2 on screen, with approval switched off and a notice");
        await hub.AcceptOutputCommand.RunAsync();
        Check(One<long>(db, "SELECT count(*) FROM decisions") == 0, "a stale approval from the keyboard or the pointer does nothing");
        await hub.OpenLatestRevisionCommand.RunAsync();
        await Until(() => hub.Review?.Revision == 3);
        Check(hub.CanDecide && !hub.IsStale && hub.ChangeHeading.EndsWith("revision 2"), "the link opens revision 3, which can be decided and compares with revision 2");
        await hub.AcceptOutputCommand.RunAsync();
        await Until(() => hub.Review?.AcceptedBy != null);
        Check(One<long>(db, "SELECT count(*) FROM decisions") == 1 && One<int>(db, "SELECT content_revision FROM decisions") == 3, "accepting records the decision on exactly revision 3");

        // Checks that were run are shown as they were recorded.
        string out3 = One<string>(db, "SELECT id FROM outputs WHERE revision=3");
        Sql(db, $"INSERT INTO output_checks(output_id,name,status,detail,created_at) VALUES('{out3}','format','passed','0 warnings','1'),('{out3}','tests','failed','2 of 9','2')");
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.ChecksText.StartsWith("Checks:"));
        Check(hub.ChecksText == "Checks: format passed (0 warnings); tests failed (2 of 9)", "recorded check results are shown exactly, in order");
        _ = r1;
    }

    // AH31, AH26, AH23 · the same journey Handoff B describes ------------------------------------------------------------------
    private static async Task Journey(string folder)
    {
        var hub = NewHub();
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        string alpha = One<string>(db, "SELECT id FROM projects");
        string beta = NewId();
        Sql(db, $"INSERT INTO projects VALUES('{beta}','Beta')");
        (string Spec, string Draft, string Qa) Seed(string project, string marker)
        {
            string spec = TaskStore.CreateTask(db, C(), project, "Spec", $"Criteria {marker}", NewId).Id;
            string draft = TaskStore.CreateTask(db, C(), project, "Draft", $"Write it for {marker}", NewId).Id;
            string qa = TaskStore.CreateTask(db, C(), project, "QA", null, NewId).Id;
            TaskStore.AddDependency(db, C(), draft, spec, 0);
            TaskStore.UpdateTask(db, C(), draft, 1, null, null, null, reviewRequired: true);
            TaskStore.AddDependency(db, C(), qa, draft, 0);
            TaskStore.MoveTask(db, C(), spec, "in_progress", 0);
            TaskStore.MoveTask(db, C(), spec, "done", 1);
            var o1 = TaskStore.RunRole(db, C(), draft, "builder", null, NewId);
            TaskStore.RunRole(db, C(), draft, "reviewer", null, NewId);
            TaskStore.DecideOutput(db, C(), draft, o1.Id, 1, 1, "changes_requested", "Add a one-line summary.", "owner", NewId);
            TaskStore.RunRole(db, C(), draft, "builder", null, NewId);
            return (spec, draft, qa);
        }
        var a = Seed(alpha, "ALPHA-MARK");
        var b = Seed(beta, "BETA-MARK");
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.InboxCount == 2);

        Check(hub.InboxCount == 2 && hub.InboxLabel == "Inbox (2)" && hub.InboxRows.Select(r => r.Item.ProjectName).Order().SequenceEqual(["Atlas", "Beta"])
              && hub.InboxRows.All(r => r.Item.Revision == 2 && r.Unlocks.Contains("can then start")), "the inbox lists both projects' waiting decisions with their exact revision and what accepting unlocks");

        // Open the Beta request from the inbox; the path back returns to the inbox.
        var row = hub.InboxRows.Single(r => r.Item.ProjectName == "Beta");
        await hub.OpenInboxItemCommand.RunAsync(row);
        await Until(() => hub.SelectedTask?.Id == b.Draft && hub.Review?.OutputId != null);
        Check(hub.ProjectHeading == "Beta" && hub.SelectedTask!.Id == b.Draft && hub.BoardVisible && hub.CameFromInbox && hub.ChangeSummary.Contains("Revision 2 against revision 1") && hub.CanDecide,
              "opening an inbox item switches project, selects the task and shows the revision and what changed");
        await hub.BackToInboxCommand.RunAsync();
        Check(hub.InboxVisible && !hub.CameFromInbox, "the path back returns to the inbox");
        await hub.OpenInboxItemCommand.RunAsync(row);
        await Until(() => hub.CanDecide && hub.SelectedTask?.Id == b.Draft);

        // The handoff the next agent would get is this project's alone.
        await hub.PreviewHandoffCommand.RunAsync("reviewer");
        Check(hub.HasHandoff && hub.HandoffText.Contains("BETA-MARK") && !hub.HandoffText.Contains("ALPHA-MARK") && hub.HandoffSummary.Contains("project Beta") && hub.HandoffText.Contains("DRAFT, not accepted"),
              "the handoff preview shows only the selected project's evidence and labels the draft");

        // Act, then follow the event in the channel.
        await hub.AcceptOutputCommand.RunAsync();
        await Until(() => hub.ChannelRows.Any(r => r.Message.Kind == "output_accepted"));
        var rows = hub.ChannelRows.ToList();
        Check(rows.All(r => r.Message.ProjectId == beta) && rows.Select(r => r.Message.Seq).SequenceEqual(rows.Select(r => r.Message.Seq).Order()) && rows.Select(r => r.Message.Seq).Distinct().Count() == rows.Count
              && rows.Last().Message is { Kind: "output_accepted", ActorKind: "human" } && rows.Any(r => r.Message.ActorKind == "agent"),
              "after acting, the decision is the newest message in this project's channel, in order and shown once");
        Check(hub.InboxCount == 1 && hub.InboxRows[0].Item.ProjectName == "Atlas", "the inbox, the board and the channel agree after the decision");

        // Reconnect: a new event arrives while the channel is being read again.
        int before = hub.ChannelRows.Count;
        TaskStore.RunRole(db, C(), b.Draft, "builder", null, NewId);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.ChannelRows.Count > before);
        Check(hub.ChannelRows.Count == before + 1 && hub.ChannelRows.Select(r => r.Message.Seq).Distinct().Count() == hub.ChannelRows.Count, "reconnecting continues from the last cursor and shows the new message once");
        hub.SelectedProject = hub.Projects.Single(p => p.Id == alpha);
        await Until(() => hub.ChannelRows.Count > 0 && hub.ChannelRows.All(r => r.Message.ProjectId == alpha));
        Check(hub.ChannelRows.All(r => r.Message.ProjectId == alpha) && !hub.ChannelRows.Any(r => r.Message.Text.Contains("BETA")) && hub.ChannelRows.Any(r => r.Message.TaskId == a.Draft), "switching project shows that project's own channel");
        await hub.OpenChannelTaskCommand.RunAsync(hub.ChannelRows.First(r => r.Message.TaskId == a.Draft));
        await Until(() => hub.SelectedTask?.Id == a.Draft);
        Check(hub.SelectedTask?.Id == a.Draft && hub.BoardVisible, "Go to card from a message opens that task on the board");

        // A relaunch: a fresh window on the same workspace reads the same counts.
        await hub.ReleaseWriteLockAsync();
        var again = NewHub();
        await again.OpenWorkspaceFolderAsync(folder);
        await Until(() => again.InboxRows.Count == 2);
        Check(again.InboxCount == 2 && again.InboxRows.Single(r => r.Item.ProjectName == "Atlas").Item is { TaskId: var t1, Revision: 2 } && t1 == a.Draft
              && again.InboxRows.Single(r => r.Item.ProjectName == "Beta").Item.Revision == 3,
              "after a relaunch the inbox shows the same two waiting decisions: Atlas revision 2 and the new Beta revision 3");
        await again.ReleaseWriteLockAsync();
    }
}
