using LocalMind.AgentHub;
using LocalMind.AgentHub.Core;

/// <summary>Day 5 · plan, schedule, graph. Scripted local models; nothing needs a paid credential.</summary>
internal static partial class CheckpointJ
{
    internal const string PlanText = "Draft A | writer-a | Write A in one line | \nDraft B | writer-b | Write B in one line\nQA | tester | Check both drafts | after: Draft A; Draft B";

    internal static Dictionary<string, string> Bindings(PlanEstimate e) => e.Tasks.Where(t => t.Ready && t.Binding != null).ToDictionary(t => t.TaskId, t => t.Binding!);

    internal static Func<ScriptedRequest, ScriptedReply> PlanModels(int builderDelay = 600) => r => r.Model switch
    {
        "spare:1" => CheckpointI.Text(r, PlanText),
        "builder-a:1" => CheckpointI.Text(r, "Deliverable text for the task", builderDelay),
        _ => CheckpointI.Text(r, "APPROVE\nFine."),
    };

    private static async Task PlanChecks(string folder)
    {
        await Proposal(Path.Combine(folder, "proposal"));
        await Schedule(Path.Combine(folder, "schedule"));
        await Failure(Path.Combine(folder, "failure"));
        await Cap(Path.Combine(folder, "cap"));
    }

    internal static async Task<(CheckpointI.Bench B, string Plan)> ApprovedPlan(string folder, Func<ScriptedRequest, ScriptedReply> answer)
    {
        var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: answer);
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "planner", b.LocalConn!, "spare:1");
        var proposed = await b.W.ProposePlanAsync(b.Db, C(), b.Project, "Ship two drafts, then QA them.");
        await b.W.ApprovePlanAsync(b.Db, C(), proposed.Id, 1);
        return (b, proposed.Id);
    }

    /// <summary>
    /// Development-only (the Day 5 native journey): a disposable Atlas whose planner, builder and reviewer are three real local models. The owner writes the brief in the window.
    /// </summary>
    public static async Task<string> MakeDay5(string folder, string endpoint, string builder, string reviewer, string planner)
    {
        var fake = new FakeWorker("day5-fixture", new MemoryVault());
        var w = new WorkerClient(fake);
        var info = await w.CreateWorkspaceAsync(folder, "Atlas");
        string conn = (await w.AddConnectionAsync(info.DatabasePath, C(), "ollama", endpoint, "Ollama on this computer")).Id;
        await w.ProbeConnectionAsync(info.DatabasePath, conn);
        await w.AssignRoleAsync(info.DatabasePath, C(), info.ProjectId, "builder", conn, builder);
        await w.AssignRoleAsync(info.DatabasePath, C(), info.ProjectId, "reviewer", conn, reviewer);
        await w.AssignRoleAsync(info.DatabasePath, C(), info.ProjectId, "planner", conn, planner);
        var settings = Team.Read(info.DatabasePath, info.ProjectId).Settings;
        await w.SetProjectSettingsAsync(info.DatabasePath, C(), settings with { MaxSeconds = 300, MaxOutputTokens = 300 });
        await w.ReleaseWorkspaceAsync(info.DatabasePath);
        await fake.DisposeAsync();
        return info.DatabasePath;
    }

    // AH28 · propose, edit, approve once, dismiss ---------------------------------------------------------------------------------
    private static async Task Proposal(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.LocalTeam(b, answer: PlanModels());
        await b.W.AssignRoleAsync(b.Db, C(), b.Project, "planner", b.LocalConn!, "spare:1");
        var proposed = await b.W.ProposePlanAsync(b.Db, C(), b.Project, "Ship two drafts, then QA them.");
        var plan = (await b.W.ReadPlansAsync(b.Db, b.Project)).Single();
        Check(plan is { Status: "proposed", Version: 1, PlannerModel: "spare:1" } && plan.Tasks.Count == 3 && plan.Edges.Count == 2 && b.Local.Seen.Last(x => x.Path == "/api/chat").Packet.Contains("Ship two drafts")
              && One<long>(b.Db, "SELECT count(*) FROM tasks") == 0, "the planner's brief becomes a proposal of three tasks and two prerequisites; the board has nothing yet");

        var edited = plan.Tasks.Select(t => t.Title == "Draft A" ? t with { Title = "Write A", Owner = "ana", Criteria = "One short line about A." } : t.Title == "QA" ? t with { Review = false, After = "Write A; Draft B" } : t).ToArray();
        await b.W.EditPlanAsync(b.Db, C(), plan.Id, 1, edited, [.. plan.Edges]);
        var stale = await Refused(() => b.W.ApprovePlanAsync(b.Db, C(), plan.Id, 1));
        string approve = C();
        await b.W.ApprovePlanAsync(b.Db, approve, plan.Id, 2);
        await b.W.ApprovePlanAsync(b.Db, approve, plan.Id, 2);       // the same click again
        var again = await b.W.ApprovePlanAsync(b.Db, C(), plan.Id, 2); // a second window
        Check(stale.Code == "conflict" && One<long>(b.Db, "SELECT count(*) FROM tasks") == 3 && One<long>(b.Db, "SELECT count(*) FROM hub_dependencies") == 2 && !again.Duplicate
              && One<string>(b.Db, "SELECT owner || '/' || outcome FROM tasks WHERE title='Write A'") == "ana/One short line about A." && One<long>(b.Db, "SELECT review_required FROM tasks WHERE title='QA'") == 0
              && One<long>(b.Db, "SELECT count(*) FROM attempts") == 0, "the edited, accepted version is stored once however many times it is approved, and approval starts no attempt");

        var second = await b.W.ProposePlanAsync(b.Db, C(), b.Project, "Another idea.", [new("t1", "Idea", null, "", true)], []);
        await b.W.DismissPlanAsync(b.Db, C(), second.Id, 1);
        var dismissed = await Refused(() => b.W.ApprovePlanAsync(b.Db, C(), second.Id, 1));
        Check(dismissed.Code == "plan_decided" && One<long>(b.Db, "SELECT count(*) FROM tasks") == 3 && One<long>(b.Db, "SELECT count(*) FROM attempts") == 0 && b.Local.Seen.Count(x => x.Path == "/api/chat") == 1,
              "a dismissed proposal creates no task and starts no attempt");
        string open = (await b.W.ProposePlanAsync(b.Db, C(), b.Project, "A loop.", [new("a", "A", null, "", true), new("b", "B", null, "", true)], [])).Id;
        var loop = await Refused(() => b.W.EditPlanAsync(b.Db, C(), open, 1, [new("a", "A", null, "", true), new("b", "B", null, "", true)], [new("a", "b"), new("b", "a")]));
        Check(loop.Code == "cycle", "a plan whose prerequisites loop is refused");
    }

    // AH107 · AH29 · AH30 · the end-of-batch journey --------------------------------------------------------------------------------
    private static async Task Schedule(string folder)
    {
        var (b, planId) = await ApprovedPlan(folder, PlanModels());
        await using var _ = b;
        var plan = (await b.W.ReadPlansAsync(b.Db, b.Project)).Single();
        string A = plan.Tasks.Single(t => t.Title == "Draft A").TaskId!, B = plan.Tasks.Single(t => t.Title == "Draft B").TaskId!, QA = plan.Tasks.Single(t => t.Title == "QA").TaskId!;
        var est = await b.W.PlanEstimateAsync(b.Db, planId);
        Check(est.Roles.Select(r => r.Role).SequenceEqual(["planner", "builder", "reviewer"]) && est.Roles[1].Tasks == 3 && est.Currency == "USD" && est.Tasks.Count(t => t.Ready) == 2 && est.UnknownPricing.Count == 0
              && est.CapLimit == null && est.UnresolvedCharges == 0 && est.Roles[0].Note!.Contains("Already ran"), "the estimate names planner, builder and reviewer, the currency, unresolved charges and the cap, and marks two tasks ready");

        var start = await b.W.StartPlanAsync(b.Db, C(), planId, Bindings(est));
        var repeat = await b.W.StartPlanAsync(b.Db, C(), planId, Bindings(est));
        var qaSkip = start.Skipped.Single(s => s.TaskId == QA);
        Check(start.Started.Count == 2 && qaSkip.Reason.Contains("Draft A") && qaSkip.Reason.Contains("Draft B") && repeat.Started.Count == 0, "two independent branches start together; QA waits and names both prerequisites; starting again starts nothing");
        await Until(() => RunEngine.ForTask(b.Db, A).Any(x => x.SentAt != null && x.Active) && RunEngine.ForTask(b.Db, B).Any(x => x.SentAt != null && x.Active));
        var mid = await b.W.PlanStatusAsync(b.Db, planId);
        Check(mid.Overlaps.Count >= 1 && mid.Nodes.Single(n => n.Key == plan.Tasks.Single(t => t.TaskId == QA).Key).State == "waiting", "the two builders' attempts overlap in time and QA is waiting");

        await b.Fake.RestartAsync(); // the worker restarts with both attempts in flight
        var attemptA = RunEngine.ForTask(b.Db, A).Single(x => x.Role == "builder");
        var attemptB = RunEngine.ForTask(b.Db, B).Single(x => x.Role == "builder");
        await b.Settled(attemptA.Id); await b.Settled(attemptB.Id);
        await Until(() => RunEngine.ForTask(b.Db, A).Count(x => x.Role == "reviewer" && !x.Active) == 1 && RunEngine.ForTask(b.Db, B).Count(x => x.Role == "reviewer" && !x.Active) == 1);
        var waiting = await b.W.PlanStatusAsync(b.Db, planId);
        var qaNode = waiting.Nodes.Single(n => n.TaskId == QA);
        Check(waiting.Nodes.Single(n => n.TaskId == A).State == "awaiting_review" && qaNode.Blockers.Count == 2 && qaNode.Blockers.All(x => x.OutputId != null && x.Revision == 1 && x.Reason.Contains("waiting for your decision")),
              "after a restart both completed drafts survive; QA names each blocker with its output and revision");

        string outA = One<string>(b.Db, $"SELECT id FROM outputs WHERE task_id='{A}'"), outB = One<string>(b.Db, $"SELECT id FROM outputs WHERE task_id='{B}'");
        await b.W.DecideOutputAsync(b.Db, C(), A, outA, 1, 1, "accepted", null);
        var half = (await b.W.PlanStatusAsync(b.Db, planId)).Nodes.Single(n => n.TaskId == QA);
        Check(half.Blockers.Count == 1 && half.Blockers[0].TaskId == B, "QA keeps waiting while either branch lacks acceptance, and shows the one that remains");
        long before = One<long>(b.Db, "SELECT max(seq) FROM events");
        await b.W.DecideOutputAsync(b.Db, C(), B, outB, 1, 1, "accepted", null);
        long seq = One<long>(b.Db, $"SELECT max(seq) FROM events WHERE task_id='{B}' AND kind='output_accepted'");

        await b.Fake.RestartAsync();
        var done = await b.W.PlanStatusAsync(b.Db, planId);
        var channel = Channel.Read(b.Db, b.Project, before, 50).Messages.Single(m => m.Kind == "output_accepted");
        var board = new ProjectReader(b.Db).Tasks(b.Project).Single(t => t.Id == B);
        var hand = (await b.W.PlanHandoffsAsync(b.Db, b.Project, before)).Single(h => h.Kind == "output_accepted");
        Check(done.Nodes.Single(n => n.TaskId == B).LastSeq == seq && channel.Seq == seq && board.LastSeq == seq && hand.Seq == seq && hand.Targets.SequenceEqual([QA]),
              "the board card, the graph node, the channel row and the handoff all carry the same stored event");
        var qaTask = new ProjectReader(b.Db).Tasks(b.Project).Single(t => t.Id == QA);
        var moved = await b.W.MoveTaskAsync(b.Db, C(), QA, "in_progress", qaTask.Revision);
        Check(done.Nodes.Single(n => n.TaskId == QA).State is "ready" && moved.Revision > qaTask.Revision && done.Overlaps.Count >= 1, "after the owner accepts both and the worker restarts, QA is eligible and moves");

        var actuals = await b.W.PlanActualsAsync(b.Db, planId);
        Check(actuals.Count(a => a.Role == "builder") == 2 && actuals.All(a => a.Status == "completed" && a.InputTokens == 40 && a.OutputTokens == 25 && a.Estimate == 0 && a.Cost == 0),
              "each attempt's actual usage sits beside what was estimated");
        var order = Plans.Traversal(plan.Edges, plan.Tasks.Single(t => t.TaskId == QA).Key);
        Check(order.Count == 3 && order[^1] == plan.Tasks.Single(t => t.TaskId == QA).Key, "keyboard traversal visits prerequisites in dependency order and ends on the node");
    }

    // AH29 · a failed prerequisite is named --------------------------------------------------------------------------------------------
    private static async Task Failure(string folder)
    {
        var (b, planId) = await ApprovedPlan(folder, r => r.Model == "builder-a:1" && r.Packet.Contains("Task: Draft B") ? new(500, "{\"error\":\"boom\"}") : PlanModels(100)(r));
        await using var _ = b;
        var plan = (await b.W.ReadPlansAsync(b.Db, b.Project)).Single();
        string B = plan.Tasks.Single(t => t.Title == "Draft B").TaskId!, QA = plan.Tasks.Single(t => t.Title == "QA").TaskId!;
        var est = await b.W.PlanEstimateAsync(b.Db, planId);
        await b.W.StartPlanAsync(b.Db, C(), planId, Bindings(est));
        await b.Settled(RunEngine.ForTask(b.Db, B).Single(x => x.Role == "builder").Id);
        var qa = (await b.W.PlanStatusAsync(b.Db, planId)).Nodes.Single(n => n.TaskId == QA);
        Check(qa.Blockers.Any(x => x.TaskId == B && x.State == "failed" && x.Reason.Contains("failed")) && qa.State == "waiting", "QA shows which prerequisite failed and why");
    }

    // AH107 · AH108 · two parallel reservations at the cap --------------------------------------------------------------------------------
    private static async Task Cap(string folder)
    {
        await using var b = await CheckpointI.NewBench(folder);
        await CheckpointI.CloudTeam(b, answer: r => new(200, ContentType: "text/event-stream", Chunks: ScriptedServer.Sse(r.Model, "Vendor", "Cloud draft", 40, 25), DelayMs: 300));
        var proposed = await b.W.ProposePlanAsync(b.Db, C(), b.Project, "Two independent drafts.", [new("t1", "One", null, "Write one.", true), new("t2", "Two", null, "Write two.", true)], []);
        await b.W.ApprovePlanAsync(b.Db, C(), proposed.Id, 1);
        var est = await b.W.PlanEstimateAsync(b.Db, proposed.Id);
        double each = est.Tasks[0].Estimate!.Value;
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { BudgetLimit = each * 1.5 });
        est = await b.W.PlanEstimateAsync(b.Db, proposed.Id);
        Check(est.Roles[1].Estimate > 0 && est.CapLimit is { } cap && Math.Abs(cap - each * 1.5) < 1e-12 && est.UnknownPricing.Any(u => u.StartsWith("reviewer")) && !est.TotalComplete,
              "the estimate shows the cloud builder's cost and the cap, and names the reviewer whose price is unknown instead of counting it as free");
        var start = await b.W.StartPlanAsync(b.Db, C(), proposed.Id, Bindings(est));
        var held = Team.Read(b.Db, b.Project).Budget;
        Check(start.Started.Count == 1 && start.Skipped.Single(s => s.Reason.Contains("budget")) is { } && Math.Abs(held.Reserved - each) < 1e-12, "two parallel starts at the cap: one reserves its estimate, the other is refused for the budget");
        await b.Fake.RestartAsync();
        Check(Math.Abs(Team.Read(b.Db, b.Project).Budget.Reserved - each) < 1e-12 || Team.Read(b.Db, b.Project).Budget.Spent > 0, "the reservation survives a restart until the attempt settles");
    }
}
