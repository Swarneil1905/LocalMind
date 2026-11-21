using LocalMind.AgentHub;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 5 · navigating a working project and following a handoff to its evidence. Drives the same view-model commands the window uses.</summary>
internal static class CheckpointK
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static Task Until(Func<bool> done, int ms = 8000, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(done))] string? what = null) => CheckpointJ.Until(done, ms, what);
    private static string C() => CheckpointJ.C();

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Pure();
        await Layout(Path.Combine(dir, "layout"));
        await Journey(Path.Combine(dir, "journey"));
    }

    internal static HubViewModel NewHub(WorkerClient w, string layouts) => new() { Workspaces = new WorkspaceService(w), PickFile = () => Task.FromResult<string?>(null), Layouts = new LayoutStore(layouts) };

    // The parts that need no window --------------------------------------------------------------------------------------------------
    private static void Pure()
    {
        // AH79 · a steady board: select around card 40, stream earlier and later arrivals.
        var steady = new SteadyBoard();
        var cards = Enumerable.Range(1, 60).Select(i => $"card-{i:00}").ToList();
        steady.Update(cards);
        foreach (var id in new[] { "card-39", "card-40", "card-41" }) steady.Selected.Add(id);
        steady.Focus = "card-40"; steady.Anchor = "card-40";
        int at = steady.IndexOf("card-40");
        steady.Update([.. Enumerable.Range(1, 5).Select(i => $"early-{i}"), .. cards, .. Enumerable.Range(1, 5).Select(i => $"late-{i}")]);
        Check(steady.IndexOf("card-40") == at && steady.Selected.SetEquals(["card-39", "card-40", "card-41"]) && steady.Focus == "card-40" && steady.Pending == 10 && steady.Shown.Count == 60,
              "ten cards arriving around card 40 change nothing under the owner's selection, focus or scroll anchor, and wait as a count");
        steady.Reveal();
        check60(steady);
        static void check60(SteadyBoard s) => Check(s.Shown.Count == 70 && s.IndexOf("card-40") == 5 + 39 && s.Selected.Count == 3 && s.Anchor == "card-40" && s.Pending == 0,
            "revealing them is an explicit act: the anchor keeps its card by ID, shifted by what arrived above it, and the selection stays");

        // AH80 / AH81 · animation belongs to a new persisted event.
        var anim = new HandoffAnimator();
        HandoffEvent E(long seq) => new(seq, "output_accepted", "t", ["qa"]);
        anim.Baseline(10);
        var replay = anim.Live([E(8), E(10)]);
        var fresh = anim.Live([E(11)]);
        var again = anim.Live([E(11)]);
        anim.Finish();
        anim.Paused = true;
        var paused = anim.Live([E(12)]);
        anim.Paused = false;
        var afterPause = anim.Live([E(12)]);
        anim.SetReduced(true);
        var reduced = anim.Live([E(13)]);
        Check(replay == null && fresh?.Seq == 11 && again == null && anim.Active == null && paused == null && afterPause == null && reduced == null,
              "a replayed or already-seen event never animates; only a new persisted one does; pausing and reduced motion leave the graph idle, and the event is not replayed later");

        // AH73 · a window whose monitor is gone comes back on a screen.
        var primary = new ScreenArea(0, 0, 1920, 1080, true);
        var gone = LayoutStore.Fit(new(3000, 200, 1200, 800), [primary]);
        var edge = LayoutStore.Fit(new(1700, 100, 1200, 800), [primary]);
        var second = LayoutStore.Fit(new(2000, 100, 1000, 700), [primary, new(1920, 0, 1920, 1080)]);
        Check(gone is { X: 360, Y: 140, Width: 1200, Height: 800 } && edge.X + edge.Width <= 1920 && second is { X: 2000, Y: 100 } && LayoutStore.Fit(new(5000, 5000, 4000, 3000), [primary]) is { Width: 1920, Height: 1080 },
              "a saved window on a missing monitor is centred on the primary one; one partly off-screen is pulled back; one on a present monitor stays");

        // AH75 · a filter's meaning.
        var t = new TaskItem("1", "Write notes", "open", [new Prerequisite("0", "Brief", "open")], "About release", "ana", 0, true, false);
        Check(new BoardFilter(Owner: "ana", BlockedOnly: true, Review: "needs").Matches(t) && !new BoardFilter(Owner: "bob").Matches(t) && !new BoardFilter(Review: "accepted").Matches(t) && new BoardFilter(Search: "RELEASE").Matches(t),
              "filters by owner, blocker, review state and search text match the card's own state");
    }

    // AH72 · AH73 · AH74 · AH75 ----------------------------------------------------------------------------------------------------------------------
    private static async Task Layout(string folder)
    {
        string layouts = Path.Combine(folder, "layouts");
        await using var one = await CheckpointI.NewBench(Path.Combine(folder, "w1"));
        await using var two = await CheckpointI.NewBench(Path.Combine(folder, "w2"));
        foreach (var b in new[] { one, two }) { b.Task("Alpha " + b.Folder[^2..], "Do it"); b.Task("Beta", "Do it"); }
        // A second project in the first workspace, so a switch has somewhere to go.
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = one.Db, Pooling = false }.ToString()))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO projects VALUES('p2','Second'); INSERT INTO tasks(id,project_id,title,status,created_at,revision) VALUES('p2t','p2','Other work','open','1',0)";
            q.ExecuteNonQuery();
        }
        var hub = NewHub(one.W, layouts);
        await hub.OpenAsync(one.Db, "One");
        await Until(() => hub.Tasks.Count == 2);

        // AH72 · narrow window: the selected task and its actions stay reachable.
        hub.SelectedTask = hub.Tasks[0];
        await Until(() => hub.CanEdit);
        hub.SetWidth(1200);
        bool wide = hub.InspectorVisible && hub.ListVisible;
        hub.SetWidth(700);
        bool closed = !hub.InspectorVisible && hub.ListVisible && hub.SelectedTask == hub.Tasks[0];
        await hub.ToggleInspectorCommand.RunAsync();
        Check(wide && closed && hub.InspectorVisible && !hub.ListVisible && hub.SelectedTask == hub.Tasks[0] && hub.AskBuilderCommand.CanExecute(null) && hub.MoveTaskCommand.CanExecute("in_progress"),
              "at a narrow width the inspector becomes its own pane, and the selected task and its actions are still reachable");
        await hub.ToggleInspectorCommand.RunAsync();
        hub.SetWidth(1200);
        Check(hub.ListVisible && hub.InspectorVisible && hub.SelectedTask == hub.Tasks[0], "widening again restores the list beside the inspector with the same selection");

        // AH73 · each workspace keeps its own layout.
        await hub.WidenProjectsCommand.RunAsync();
        await hub.ShowChannelCommand.RunAsync();
        hub.NoteWindow(new(100, 100, 1000, 700));
        await hub.OpenAsync(two.Db, "Two");
        await hub.NarrowInspectorCommand.RunAsync();
        await hub.NarrowProjectsCommand.RunAsync();
        await hub.ShowInboxCommand.RunAsync();
        hub.NoteWindow(new(3000, 100, 900, 600));
        WindowPlacement? restored = null;
        var back = NewHub(one.W, layouts);
        back.WindowRestoreRequested += w => restored = w;
        await back.OpenAsync(one.Db, "One");
        var inboxHub = NewHub(two.W, layouts);
        WindowPlacement? restored2 = null;
        inboxHub.WindowRestoreRequested += w => restored2 = w;
        await inboxHub.OpenAsync(two.Db, "Two");
        Check(back.ProjectsWidth == 280 && back.ChannelVisible && restored == new WindowPlacement(100, 100, 1000, 700) && inboxHub.ProjectsWidth == 200 && inboxHub.InboxVisible && inboxHub.InspectorWidth == 240
              && restored2 == new WindowPlacement(3000, 100, 900, 600), "two workspaces reopen with their own pane sizes, view and window placement");
        await back.ShowBoardCommand.RunAsync();

        // AH74 · the command menu.
        back.SelectedProject = back.Projects.Single(p => p.Name == "Atlas");
        await Until(() => back.Tasks.Count == 2 && back.CanWrite);
        back.SelectedTask = null;
        await back.OpenMenuCommand.RunAsync();
        back.MenuQuery = "builder";
        var disabled = back.MenuEntries.First(e => e.Title.StartsWith("Ask the builder"));
        await back.RunMenuEntryCommand.RunAsync(disabled);
        bool noted = back.MenuNote == "Select a task first." && back.MenuOpen && !disabled.Enabled && disabled.Label.Contains("unavailable: Select a task first.");
        back.MenuQuery = "beta";
        var task = back.MenuEntries.First(e => e.Kind == "task");
        back.MenuQuery = "new task";
        var shortcut = back.MenuEntries.First(e => e.Title == "New task");
        await back.CloseMenuCommand.RunAsync();
        await back.OpenMenuCommand.RunAsync();
        back.MenuQuery = "beta";
        await back.RunMenuEntryCommand.RunAsync(back.MenuEntries.First(e => e.Kind == "task"));
        Check(noted && task.Title == "Beta" && shortcut.Shortcut == "Ctrl+T" && !back.MenuOpen && back.SelectedTask?.Title == "Beta",
              "the menu finds tasks and actions, shows shortcuts, explains why an action is unavailable, and activating a task selects it and closes the menu");

        // AH75 · saved views and private search per project.
        back.FilterOwner = "ana"; back.FilterSearch = "private words"; back.FilterBlocked = true;
        back.SavedViewName = "Ana blocked";
        await back.SaveViewCommand.RunAsync();
        string line = back.FilterLine;
        back.SelectedProject = back.Projects.Single(p => p.Name == "Second");
        await Until(() => back.Tasks.Any(t => t.Title == "Other work"));
        bool isolated = back.Filter.IsEmpty && back.FilterSearch == "" && back.SavedViewsHere.Count == 0 && back.ShownTasks.Count() == 1;
        back.SelectedProject = back.Projects.Single(p => p.Name == "Atlas");
        await Until(() => back.Tasks.Count == 2);
        bool kept = back.FilterSearch == "private words" && back.FilterOwner == "ana" && back.SavedViewsHere.Count == 1;
        await back.ClearFilterCommand.RunAsync();
        await back.ApplyViewCommand.RunAsync(back.SavedViewsHere[0]);
        var reopened = NewHub(one.W, layouts);
        await reopened.OpenAsync(one.Db, "One");
        await Until(() => reopened.Tasks.Count == 2);
        Check(line.Contains("tasks match") && isolated && kept && back.FilterOwner == "ana" && back.FilterBlocked && back.FilterSearch == "" && reopened.SavedViewsHere.Single().Name == "Ana blocked",
              "a saved view reopens its owner, blocker and review filters; search text and filters stay with the project they were made in");
        await hub.ReleaseWriteLockAsync(); await back.ReleaseWriteLockAsync(); await inboxHub.ReleaseWriteLockAsync(); await reopened.ReleaseWriteLockAsync();
    }

    // The end-of-batch journey, driven through the window's own commands --------------------------------------------------------------------------------
    private static async Task Journey(string folder)
    {
        var (b, planId) = await CheckpointJ.ApprovedPlan(folder, CheckpointJ.PlanModels(500));
        await using var _ = b;
        var plans = await b.W.ReadPlansAsync(b.Db, b.Project);
        var hub = NewHub(b.W, Path.Combine(folder, "layouts"));
        await hub.OpenAsync(b.Db, "Atlas");
        await Until(() => hub.Tasks.Count == 3 && hub.CanWrite);
        await hub.ShowPlanCommand.RunAsync();
        await Until(() => hub.PlanNodes.Count == 3);
        var qaRow = hub.PlanNodes.Single(r => r.Title == "QA");
        string A = hub.Tasks.Single(t => t.Title == "Draft A").Id, B = hub.Tasks.Single(t => t.Title == "Draft B").Id;

        // AH82 · a blocker that has produced nothing is explained.
        hub.FilterSearch = "Draft";
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.OpenNodeBlockerCommand.RunAsync(qaRow);
        await Until(() => hub.Review != null && hub.SelectedTask?.Id is var s && (s == A || s == B));
        Check(hub.HasEvidenceNote && hub.EvidenceNote.Contains("has not produced an output yet") && qaRow.Direction.Contains("Needs: Draft A, Draft B") && qaRow.Direction.StartsWith("Needs:") && qaRow.StateLabel == "Waiting",
              "following a blocker that has produced nothing says so, and the graph row shows direction and status in words");
        await hub.BackCommand.RunAsync();
        Check(hub.SelectedTask?.Title == "QA" && hub.FilterSearch == "Draft", "Back returns to the waiting card with the board's filter unchanged");

        // AH107 · AH29 · estimate, then start.
        await hub.EstimatePlanCommand.RunAsync();
        Check(hub.EstimateLines.Any(l => l.StartsWith("Builder")) && hub.EstimateLines.Any(l => l.StartsWith("Reviewer")) && hub.EstimateLines.Any(l => l.Contains("No spending cap")), "the window shows the estimate before anything starts");
        await hub.StartPlanCommand.RunAsync();
        await Until(() => hub.PlanNodes.Count(r => r.Node.State == "running") == 2);
        Check(hub.PlanNodes.Count(r => r.Node.State == "running") == 2 && hub.PlanNodes.Single(r => r.Title == "QA").Node.State == "waiting", "two independent branches run together while QA waits");
        await Until(() => RunEngine.ForTask(b.Db, A).Count(x => x.Role == "reviewer" && !x.Active) == 1 && RunEngine.ForTask(b.Db, B).Count(x => x.Role == "reviewer" && !x.Active) == 1, 15000);
        await hub.PollAsync();
        await Until(() => hub.PlanNodes.Count(r => r.Node.State == "awaiting_review") == 2);

        // AH82 · from the waiting card to the blocker's revision.
        qaRow = hub.PlanNodes.Single(r => r.Title == "QA");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.OpenNodeBlockerCommand.RunAsync(qaRow);
        await Until(() => hub.Review is { HasOutput: true } && hub.SelectedTask?.Id == qaRow.Node.Blockers[0].TaskId);
        Check(hub.Review!.Revision == 1 && hub.Review.OutputId == qaRow.Node.Blockers[0].OutputId && !hub.HasEvidenceNote && hub.CanDecide, "the waiting card opens its blocker and the exact revision the blocker produced, ready for a decision");
        await hub.BackCommand.RunAsync();
        Check(hub.SelectedTask?.Title == "QA", "Back restores the initiating card");

        // AH83 · pin revision 1, deliver revision 2, decide only on what is displayed.
        string target = qaRow.Node.Blockers[0].TaskId;
        hub.SelectedTask = hub.Tasks.Single(t => t.Id == target);
        await Until(() => hub.Review is { HasOutput: true });
        string pinnedText = hub.OutputText;
        await hub.PinRevisionCommand.RunAsync();
        await b.W.UpdateTaskAsync(b.Db, C(), target, hub.SelectedTask!.Revision, reviewRequired: true);
        await b.W.DecideOutputAsync(b.Db, C(), target, hub.Review!.OutputId!, 1, 1, "changes_requested", "Add detail.");
        var rev2 = await b.W.DispatchAsync(b.Db, C(), target, "builder");
        await b.Settled(rev2.Id);
        await hub.PollAsync();
        await hub.RefreshCommand.RunAsync(); // another window delivered it: reloading must not swap the pinned text
        await Until(() => hub.Review is { IsStale: true });
        bool pinnedHeld = hub.Review!.Revision == 1 && hub.OutputText == pinnedText && !hub.CanDecide && hub.PinNote.Contains("Revision 2 has been delivered");
        await hub.ComparePinnedCommand.RunAsync();
        bool compared = hub.ComparisonLines.Count > 0 && hub.ComparisonLines[0].Contains("1") && hub.Review.Revision == 1;
        await hub.OpenLatestRevisionCommand.RunAsync();
        await Until(() => hub.Review?.Revision == 2);
        Check(pinnedHeld && compared && !hub.Pinned && hub.Review!.Revision == 2, "a pinned revision stays on screen when revision 2 arrives; comparing changes nothing; the owner opens revision 2 only on request, and decisions follow the displayed revision");
        await Until(() => RunEngine.ForTask(b.Db, target).Count(x => x.Role == "reviewer" && !x.Active) == 2);

        // AH80 · AH30 · a live handoff pulses; reconnect and reduced motion keep it idle.
        string other = target == A ? B : A;
        string outTarget = One(b.Db, $"SELECT id FROM outputs WHERE task_id='{target}' AND revision=2");
        await hub.PollAsync();
        await b.W.DecideOutputAsync(b.Db, C(), target, outTarget, 2, 1, "accepted", null);
        await hub.PollAsync();
        await Until(() => hub.PlanNodes.Any(r => r.Pulse));
        bool pulsed = hub.PlanNodes.Single(r => r.Pulse).Node.TaskId != target && hub.PlanNodes.Single(r => r.Pulse).HandoffLabel.Length > 0;
        hub.ReducedMotion = true;
        bool idleNow = hub.PlanNodes.All(r => !r.Pulse);
        hub.ReducedMotion = false;
        await hub.OpenAsync(b.Db, "Atlas");
        await Until(() => hub.PlanNodes.Count == 3);
        await hub.PollAsync();
        Check(pulsed && idleNow && hub.PlanNodes.All(r => !r.Pulse), "a new accepted output pulses its dependants once; switching to reduced motion stops it at once; reconnecting and polling again leave the graph idle");

        // Accept the other branch, restart, and QA is eligible.
        string outOther = One(b.Db, $"SELECT id FROM outputs WHERE task_id='{other}' AND revision=1");
        await b.W.UpdateTaskAsync(b.Db, C(), other, new ProjectReader(b.Db).Tasks(b.Project).Single(t => t.Id == other).Revision, reviewRequired: true);
        await b.W.DecideOutputAsync(b.Db, C(), other, outOther, 1, 1, "accepted", null);
        await b.Fake.RestartAsync();
        var fresh = NewHub(b.W, Path.Combine(folder, "layouts"));
        await fresh.OpenAsync(b.Db, "Atlas");
        await Until(() => fresh.Tasks.Count == 3 && fresh.CanWrite);
        fresh.SelectedTask = fresh.Tasks.Single(t => t.Title == "QA");
        await Until(() => fresh.CanEdit);
        await fresh.MoveTaskCommand.RunAsync("in_progress");
        await Until(() => fresh.Tasks.Single(t => t.Title == "QA").Status == "in progress");
        Check(fresh.Tasks.Single(t => t.Title == "QA").Status == "in progress", "after a restart, with both branches accepted, QA is eligible and moves to In progress");
        await hub.ReleaseWriteLockAsync(); await fresh.ReleaseWriteLockAsync();
    }

    private static string One(string db, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = sql;
        return (string)q.ExecuteScalar()!;
    }
}
