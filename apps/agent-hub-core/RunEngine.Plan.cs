using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

public sealed partial class RunEngine
{
    private const string PlannerPrompt = "You are the planner. Turn the brief into tasks. Reply with one task per line and nothing else, in the form: "
        + "Title | owner | acceptance criteria | after: Other title; Another title. Leave out the after part when nothing must come first.";

    /// <summary>
    /// Asks the planner model for a proposal, or stores the tasks the owner wrote. Nothing is created on the board and no task runs until the owner approves.
    /// The planner runs only on a model on this computer for now: a cloud planner would need the preview and consent a task dispatch has.
    /// </summary>
    public CommandResult ProposePlan(string workspace, string commandId, string projectId, string brief, PlanTask[]? tasks, PlanEdge[]? edges)
    {
        if (string.IsNullOrWhiteSpace(brief) || brief.Length > 4000) throw Bad("invalid_text", "A brief needs 1 to 4000 characters.", "Shorten it.");
        if (TaskStore.CommandStatus(workspace, commandId) is { } done) return done;
        if (tasks != null) return Plans.Propose(workspace, commandId, projectId, brief.Trim(), tasks, edges ?? [], null, null, null, newId);
        string model; (string Provider, string Endpoint, bool IsCloud, string? CredentialRef, string Label) c; ProjectSettings settings;
        using (var db = Reader.Open(workspace))
        {
            var a = Reader.Rows(db, "SELECT connection_id, model_id FROM assignments WHERE project_id=$p AND role='planner'", ("$p", projectId)).FirstOrDefault()
                ?? throw Bad("no_planner", "No model is assigned to the planner for this project.", "Choose one in Team, or write the plan yourself.");
            c = Team.Connection(db, Reader.S(a[0]));
            model = Reader.S(a[1]);
            settings = Team.Settings(db, projectId);
        }
        if (c.IsCloud) throw Bad("planner_cloud", "The planner can only run on a model on this computer for now.", "Assign a local model to the planner, or write the plan yourself.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.MaxSeconds));
        ChatResult res;
        try { res = Client(c.Provider, c.Endpoint, c.CredentialRef).ChatAsync(new(model, [new("system", PlannerPrompt), new("user", brief.Trim())], Math.Max(settings.MaxOutputTokens, 600), false, null), _ => { }, cts.Token).GetAwaiter().GetResult(); }
        catch (ModelException e) { throw Bad(e.Code, e.Message, "Check the planner's model in Connections, or write the plan yourself."); }
        catch (OperationCanceledException) { throw Bad("planner_timeout", $"The planner did not answer within {settings.MaxSeconds} seconds.", "Try again, or write the plan yourself."); }
        var (parsed, links) = Plans.Parse(res.Text);
        if (parsed.Count == 0) throw Bad("unreadable_plan", "The planner's answer had no tasks in the expected “Title | owner | criteria | after: …” form.", "Edit the brief and ask again, or write the plan yourself.");
        return Plans.Propose(workspace, commandId, projectId, brief.Trim(), parsed, links, model, res.InputTokens, res.OutputTokens, newId);
    }

    private static bool IsLaunched(SqliteConnection db, string taskId) =>
        TaskStore.Scalar<long>(db, "SELECT count(*) FROM attempts WHERE task_id=$t AND role='builder' AND status IN ('leased','sending','streaming','completed','needs_reconciliation')", ("$t", taskId)) > 0;

    private static bool Ready(SqliteConnection db, PlanView p, PlanTask t) =>
        t.TaskId != null && !Plans.Satisfied(db, t.TaskId) && !IsLaunched(db, t.TaskId) && !IsQueued(db, t.TaskId) && Plans.EligibilityOf(db, t.TaskId).Ok;

    private static bool IsQueued(SqliteConnection db, string taskId) =>
        TaskStore.Scalar<long>(db, "SELECT count(*) FROM start_queue WHERE task_id=$t AND status='waiting'", ("$t", taskId)) > 0;

    /// <summary>Before starting: what each role would cost, what is already unresolved and how much of the cap is left. A role whose price is unknown is named, never counted as free.</summary>
    public PlanEstimate Estimate(string workspace, string planId)
    {
        using var db = Reader.Open(workspace);
        var p = Plans.Load(db, planId);
        var settings = Team.Settings(db, p.ProjectId);
        var budget = Team.Budget(db, p.ProjectId);
        var unknown = new List<string>();
        var previews = new List<TaskPreviewLite>();
        double builder = 0, reviewer = 0;
        bool builderKnown = true, reviewerKnown = true;
        int builderTasks = 0, reviewerTasks = 0;
        var rev = Reader.Rows(db, "SELECT connection_id, model_id FROM assignments WHERE project_id=$p AND role='reviewer'", ("$p", p.ProjectId)).FirstOrDefault();
        (Price? Price, string Status) revPrice = rev == null ? (null, "none") : Team.PriceFor(db, Reader.S(rev[0]), Reader.S(rev[1]), Team.Connection(db, Reader.S(rev[0])).IsCloud);
        foreach (var t in p.Tasks.Where(t => t.TaskId != null && !Plans.Satisfied(db, t.TaskId)))
        {
            var ck = Check(db, t.TaskId!, "builder", null, new());
            previews.Add(new(t.TaskId!, t.Title, Ready(db, p, t), ck.Preview.Binding, ck.Preview.Estimate, ck.Preview.Blocker));
            if (IsLaunched(db, t.TaskId!)) continue;
            builderTasks++;
            if (ck.Preview.Estimate is { } e) builder += e; else { builderKnown = false; unknown.Add(ck.Preview.Model == null ? "builder: no model assigned" : $"builder: no price for “{t.Title}”"); }
            if (t.Review)
            {
                reviewerTasks++;
                if (revPrice.Price is { } rp) reviewer += (ck.Preview.PacketTokens + settings.MaxOutputTokens) / 1e6 * rp.InputPerMTok + settings.MaxOutputTokens / 1e6 * rp.OutputPerMTok;
                else { reviewerKnown = false; if (!unknown.Any(u => u.StartsWith("reviewer"))) unknown.Add(rev == null ? "reviewer: no model assigned" : $"reviewer: price is {revPrice.Status}"); }
            }
        }
        string cur = settings.Currency;
        var roles = new List<RoleEstimate>
        {
            new("planner", p.PlannerModel, null, false, cur, 0, p.PlannerModel == null ? "none" : "ran", 1, p.PlannerModel == null ? "Written by the owner; no model ran." : $"Already ran: {p.PlannerInput?.ToString() ?? "unknown"} in, {p.PlannerOutput?.ToString() ?? "unknown"} out."),
            new("builder", AssignedModel(db, p.ProjectId, "builder"), null, false, cur, builderKnown ? builder : null, builderKnown ? "known" : "unknown", builderTasks, null),
            new("reviewer", rev == null ? null : Reader.S(rev[1]), null, false, cur, reviewerKnown ? reviewer : null, reviewerKnown ? revPrice.Status : "unknown", reviewerTasks, null),
        };
        return new(planId, cur, roles, roles.Where(r => r.Estimate != null).Sum(r => r.Estimate!.Value), builderKnown && reviewerKnown, unknown, budget.Unresolved, budget.UnknownUsage, budget.Limit, budget.Remaining, previews);
    }

    private static string? AssignedModel(SqliteConnection db, string project, string role) =>
        Reader.Rows(db, "SELECT model_id FROM assignments WHERE project_id=$p AND role=$r", ("$p", project), ("$r", role)).FirstOrDefault() is { } r ? Reader.S(r[0]) : null;

    /// <summary>
    /// Starts every task that is ready, in plan order, up to the project's concurrency limit. Each start is bound to the preview the owner saw
    /// (<paramref name="bindings"/> by task ID), so a changed model, packet or price skips that task. Repeating the call starts nothing twice.
    /// </summary>
    public PlanStart StartPlan(string workspace, string commandId, string planId, IReadOnlyDictionary<string, string> bindings)
    {
        PlanView p;
        using (var db = Reader.Open(workspace)) p = Plans.Load(db, planId);
        if (p.Status != "accepted") throw Bad("plan_not_approved", "This plan has not been approved.", "Approve the plan first.");
        List<string> started = [];
        List<SkippedTask> skipped = [];
        foreach (var t in p.Tasks.Where(t => t.TaskId != null))
        {
            using var db = Reader.Open(workspace);
            if (Plans.Satisfied(db, t.TaskId!)) { skipped.Add(new(t.TaskId!, "Already accepted.")); continue; }
            if (IsLaunched(db, t.TaskId!)) { skipped.Add(new(t.TaskId!, "Already started.")); continue; }
            if (IsQueued(db, t.TaskId!)) { skipped.Add(new(t.TaskId!, "Already waiting for a free project slot.")); continue; }
            // The same rule the graph and the estimate use, from the board as it is now; the dispatch transaction checks it again.
            if (Plans.EligibilityOf(db, t.TaskId!) is { Ok: false } no) { skipped.Add(new(t.TaskId!, no.Reason!)); continue; }
            if (!bindings.TryGetValue(t.TaskId!, out var binding)) { skipped.Add(new(t.TaskId!, "It was not in the estimate you reviewed.")); continue; }
            try
            {
                var res = Dispatch(workspace, commandId + ":" + t.TaskId, t.TaskId!, "builder", new(Binding: binding, RequireEligible: true, QueueIfFull: true));
                if (res.Detail?.StartsWith("queued:") == true) skipped.Add(new(t.TaskId!, "Waiting for a free project slot. It starts on its own when one opens."));
                else started.Add(t.TaskId!);
            }
            catch (StoreException e) { skipped.Add(new(t.TaskId!, e.Message)); }
        }
        return new(started, skipped);
    }
}
