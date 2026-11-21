using LocalMind.AgentHub;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 6 · the four corrections from the Day 5 review.</summary>
internal static partial class CheckpointJ
{
    private static async Task ReviewFixes(string folder)
    {
        await Unresolved(Path.Combine(folder, "unresolved"));
        await Eligible(Path.Combine(folder, "eligible"));
        await Capacity(Path.Combine(folder, "capacity"));
        await Editor(Path.Combine(folder, "editor"));
    }

    // P2 · an unresolved prerequisite is shown, kept and must be corrected before acceptance --------------------------------------
    private static async Task Unresolved(string folder)
    {
        var renamed = Plans.Parse("Spec | | Write the spec |\nBuild | | Build it | after: Specification");
        var fixedUp = Plans.Parse("Specification | | Write the spec |\nBuild | | Build it | after: Specification");
        var dup = Plans.Parse("Spec | | a |\nSpec | | b |\nBuild | | c | after: Spec");
        var self = Plans.Parse("Build | | c | after: Build");
        Check(renamed.Tasks.Count == 2 && renamed.Edges.Count == 0 && Plans.Problems(renamed.Tasks).Single().Contains("needs “Specification”, but no task has that title")
              && Plans.Problems(fixedUp.Tasks).Count == 0 && fixedUp.Edges.Count == 1 && Plans.Problems(dup.Tasks).Any(p => p.Contains("2 tasks are titled")) && Plans.Problems(dup.Tasks).Any(p => p.Contains("matches 2 tasks"))
              && Plans.Problems(self.Tasks).Single().Contains("lists itself"), "an unresolved, renamed, duplicate-title or self reference is reported, never guessed into an edge");

        await using var b = await CheckpointI.NewBench(folder);
        var p = await b.W.ProposePlanAsync(b.Db, C(), b.Project, "Spec then build.", [.. renamed.Tasks], [.. renamed.Edges]);
        var stored = (await b.W.ReadPlansAsync(b.Db, b.Project)).Single();
        var refused = await Refused(() => b.W.ApprovePlanAsync(b.Db, C(), p.Id, 1));
        Check(stored.Tasks.Single(t => t.Title == "Build").After == "Specification" && refused.Code == "unresolved_reference" && refused.Message.Contains("Specification") && One<long>(b.Db, "SELECT count(*) FROM tasks") == 0,
              "the owner's prerequisite text is stored as written, and approval is refused until it resolves, creating no task");
        await b.W.EditPlanAsync(b.Db, C(), p.Id, 1, [.. fixedUp.Tasks], [.. fixedUp.Edges]);
        await b.W.ApprovePlanAsync(b.Db, C(), p.Id, 2);
        Check(One<long>(b.Db, "SELECT count(*) FROM hub_dependencies") == 1, "after the correction the proposal is accepted with its prerequisite");
    }

    // P1 · eligibility comes from the board now, before preview and between preview and Send -----------------------------------------------
    private static async Task Eligible(string folder)
    {
        var (b, planId) = await ApprovedPlan(folder, PlanModels(100));
        await using var _ = b;
        var plan = (await b.W.ReadPlansAsync(b.Db, b.Project)).Single();
        string A = plan.Tasks.Single(t => t.Title == "Draft A").TaskId!, B = plan.Tasks.Single(t => t.Title == "Draft B").TaskId!;
        var early = await b.W.PlanEstimateAsync(b.Db, planId);          // previewed while both were ready
        int chats = b.Local.Seen.Count(x => x.Path == "/api/chat");
        int Rev(string id) => new ProjectReader(b.Db).Tasks(b.Project).Single(t => t.Id == id).Revision;
        await b.W.MoveTaskAsync(b.Db, C(), A, "cancelled", Rev(A));
        string extra = b.Task("Unfinished prerequisite", "Not done yet");
        await b.W.AddDependencyAsync(b.Db, C(), B, extra, Rev(B));

        var between = await b.W.StartPlanAsync(b.Db, C(), planId, Bindings(early));              // edits after the preview, before Send
        var fresh = await b.W.PlanEstimateAsync(b.Db, planId);                                    // edits before the preview
        var before = await b.W.StartPlanAsync(b.Db, C(), planId, fresh.Tasks.Where(t => t.Binding != null).ToDictionary(t => t.TaskId, t => t.Binding!));
        var graph = await b.W.PlanStatusAsync(b.Db, planId);
        var nodeA = graph.Nodes.Single(n => n.TaskId == A);
        var nodeB = graph.Nodes.Single(n => n.TaskId == B);
        var direct = await Refused(() => b.W.DispatchAsync(b.Db, C(), B, "builder", binding: early.Tasks.Single(t => t.TaskId == B).Binding));
        Check(between.Started.Count == 0 && before.Started.Count == 0 && fresh.Tasks.Count(t => t.Ready) == 0 && b.Local.Seen.Count(x => x.Path == "/api/chat") == chats && direct.Code == "preview_changed"
              && nodeA.State == "cancelled" && nodeB.Needs.Contains("Unfinished prerequisite") && nodeB.Blockers.Any(x => x.Title == "Unfinished prerequisite") && One<long>(b.Db, "SELECT count(*) FROM attempts") == 0,
              "a cancelled task and a new unfinished prerequisite block the plan's start before and after the preview: the graph, estimate and dispatch agree, and no provider request is made");
        // Reopen and finish the work: the same rule lets it start.
        await b.W.MoveTaskAsync(b.Db, C(), A, "open", Rev(A));
        await b.W.MoveTaskAsync(b.Db, C(), extra, "cancelled", Rev(extra));
        var cancelledPrereq = (await b.W.PlanStatusAsync(b.Db, planId)).Nodes.Single(n => n.TaskId == B).Blockers.Single();
        Check(cancelledPrereq.State == "cancelled" && cancelledPrereq.Reason.Contains("was cancelled"), "a cancelled prerequisite is named as cancelled, not treated as done");
    }

    // P1 · the project limit holds for plan starts, manual sends, retries and automatic review ---------------------------------------------
    private static async Task Capacity(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: r => r.Model == "builder-a:1" ? CheckpointI.Text(r, "Draft body for the task", 400) : CheckpointI.Text(r, "APPROVE\nFine.", 200));
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { MaxConcurrency = 1 });
        var proposed = await b.W.ProposePlanAsync(b.Db, C(), b.Project, "Two independent tasks.", [new("t1", "One", null, "Write one.", true), new("t2", "Two", null, "Write two.", true)], []);
        await b.W.ApprovePlanAsync(b.Db, C(), proposed.Id, 1);
        string manual = b.Task("Manual", "Write by hand");
        var est = await b.W.PlanEstimateAsync(b.Db, proposed.Id);
        var start = await b.W.StartPlanAsync(b.Db, C(), proposed.Id, Bindings(est));
        var full = await Refused(() => b.W.DispatchAsync(b.Db, C(), manual, "builder"));
        var status = await b.W.PlanStatusAsync(b.Db, proposed.Id);
        string t1 = start.Started.Single(), t2 = status.Nodes.Single(n => n.TaskId != t1 && n.Title is "One" or "Two" && n.State == "queued").TaskId;
        var channelText = string.Join(" ", Channel.Read(b.Db, b.Project, 0, 100).Messages.Select(m => m.Text));
        Check(start.Started.Count == 1 && start.Skipped.Any(s => s.Reason.Contains("free project slot")) && full.Code == "at_capacity" && status.Nodes.Count(n => n.State == "queued") == 1 && channelText.Contains("waiting for a free project slot"),
              "with a limit of one, the second plan task waits visibly in the queue and a manual send is refused with a reason");
        await Until(() => RunEngine.ForTask(b.Db, t1).Count(a => a.Role == "builder" && a.Status == "completed") == 1 && RunEngine.ForTask(b.Db, t2).Count(a => a.Role == "builder" && a.Status == "completed") == 1, 20000);
        await Until(() => RunEngine.ForTask(b.Db, t1).Any(a => a.Role == "reviewer" && !a.Active) && RunEngine.ForTask(b.Db, t2).Any(a => a.Role == "reviewer" && !a.Active), 20000);
        var all = RunEngine.ForTask(b.Db, t1).Concat(RunEngine.ForTask(b.Db, t2)).ToList();
        Check(b.Local.Peak == 1 && all.Count == 4 && all.All(a => a.Status == "completed") && One<long>(b.Db, "SELECT count(*) FROM start_queue WHERE status='started'") >= 1,
              $"queued work starts by itself as slots free, including the automatic review: four attempts completed and the provider never had more than {b.Local.Peak} request open");

        // A retry competes for the same slot.
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { MaxConcurrency = 1, AutoReview = false });
        var running = await b.W.DispatchAsync(b.Db, C(), manual, "builder");
        var retry = await Refused(() => b.W.ResumeAttemptAsync(b.Db, C(), all.First().Id, acceptRepeatCost: true));
        await b.Settled(running.Id);
        Check(retry.Code == "at_capacity" && b.Local.Peak == 1, "a retry also needs a free slot");
    }

    // P1 · the proposal editor stays attached to its project and version --------------------------------------------------------------
    private static async Task Editor(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = b.Db, Pooling = false }.ToString()))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO projects VALUES('beta','Beta')";
            q.ExecuteNonQuery();
        }
        var atlas = await b.W.ProposePlanAsync(b.Db, C(), b.Project, "Atlas brief.", [new("t1", "Atlas only", null, "", true)], []);
        var beta = await b.W.ProposePlanAsync(b.Db, C(), "beta", "Beta brief.", [new("t1", "Beta only", null, "", true)], []);
        var hub = CheckpointK.NewHub(b.W, Path.Combine(folder, "layouts"));
        await hub.OpenAsync(b.Db, "Atlas");
        await Until(() => hub.CanWrite && hub.Projects.Count == 2);
        await hub.ShowPlanCommand.RunAsync();
        hub.SelectedProject = hub.Projects.Single(p => p.Name == "Atlas");
        await Until(() => hub.HasProposal && hub.PlanEditText.StartsWith("Atlas only"));
        hub.PlanEditText = "Atlas typed, not saved | | |";
        bool atlasDirty = hub.PlanDirty && !hub.ApprovePlanCommand.CanExecute(null);

        // Switch with a delayed read: the earlier read must not land on the later project.
        var hold = new TaskCompletionSource();
        b.Fake.BeforeHandle = async r => { if (r.Op == "readPlans") await hold.Task; };
        hub.SelectedProject = hub.Projects.Single(p => p.Name == "Beta");
        bool blank = hub.PlanEditText == "" && !hub.SavePlanCommand.CanExecute(null) && !hub.ApprovePlanCommand.CanExecute(null);
        hub.SelectedProject = hub.Projects.Single(p => p.Name == "Atlas");
        hub.SelectedProject = hub.Projects.Single(p => p.Name == "Beta");
        hold.SetResult();
        b.Fake.BeforeHandle = null;
        await Until(() => hub.HasProposal && hub.PlanEditText.StartsWith("Beta only"));
        Check(atlasDirty && blank && !hub.PlanEditText.Contains("Atlas") && !hub.PlanDirty, "switching projects (even with a delayed read) never shows one project's proposal, or its typed text, in another");

        hub.PlanEditText = "Beta edited | | |";
        await hub.SavePlanCommand.RunAsync();
        await Until(() => hub.HasProposal && !hub.PlanDirty && hub.PlanEditText.StartsWith("Beta edited"));
        var plans = await b.W.ReadPlansAsync(b.Db, "beta");
        var atlasStored = (await b.W.ReadPlansAsync(b.Db, b.Project)).Single();
        Check(plans.Single().Tasks.Single().Title == "Beta edited" && plans.Single().Version == 2 && atlasStored.Tasks.Single().Title == "Atlas only" && atlasStored.Version == 1,
              "Save changes the proposal on screen and no other: Beta's task changed, Atlas's did not");

        hub.SelectedProject = hub.Projects.Single(p => p.Name == "Atlas");
        await Until(() => hub.HasProposal && hub.PlanEditText.StartsWith("Atlas"));
        Check(hub.PlanEditText.StartsWith("Atlas typed, not saved") && hub.PlanDirty && !hub.ApprovePlanCommand.CanExecute(null),
              "unsaved text is protected: it is still there when its project returns, and Approve waits for Save");
        await hub.ReleaseWriteLockAsync();
    }
}
