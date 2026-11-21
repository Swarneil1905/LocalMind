using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

public sealed record PlanTask(string Key, string Title, string? Owner, string Criteria, bool Review, string? TaskId = null, string? After = null);
public sealed record PlanEdge(string Key, string Requires);
public sealed record PlanView(string Id, string ProjectId, string Brief, string Status, int Version, string CreatedAt, string? DecidedAt, IReadOnlyList<PlanTask> Tasks, IReadOnlyList<PlanEdge> Edges,
                              string? PlannerModel, int? PlannerInput, int? PlannerOutput);

/// <summary>What stops a task from starting, with the evidence to open: the prerequisite's task and, when it produced one, the output revision.</summary>
public sealed record Blocker(string TaskId, string Title, string State, string Reason, string? OutputId, int? Revision);

public sealed record PlanNode(string Key, string TaskId, string Title, string? Owner, string State, bool Review, IReadOnlyList<Blocker> Blockers, long LastSeq, string? ActiveAttemptId, int Revision,
                              IReadOnlyList<string> Needs, IReadOnlyList<string> Feeds);
public sealed record Eligibility(bool Ok, string? Reason, IReadOnlyList<Blocker> Blockers);

/// <summary>The accepted plan as it stands. <see cref="Seq"/> is the newest stored event, so a view can tell whether it has seen everything.</summary>
public sealed record PlanStatus(string PlanId, string Status, long Seq, IReadOnlyList<PlanNode> Nodes, IReadOnlyList<PlanEdge> Edges, IReadOnlyList<Overlap> Overlaps);
public sealed record Overlap(string A, string B);
public sealed record SkippedTask(string TaskId, string Reason);

public sealed record HandoffEvent(long Seq, string Kind, string TaskId, IReadOnlyList<string> Targets);

public sealed record RoleEstimate(string Role, string? Model, string? Connection, bool IsCloud, string Currency, double? Estimate, string PriceStatus, int Tasks, string? Note);
public sealed record TaskPreviewLite(string TaskId, string Title, bool Ready, string? Binding, double? Estimate, string? Blocker);
public sealed record PlanEstimate(string PlanId, string Currency, IReadOnlyList<RoleEstimate> Roles, double? Total, bool TotalComplete, IReadOnlyList<string> UnknownPricing,
                                  int UnresolvedCharges, int UnknownUsage, double? CapLimit, double? RemainingCap, IReadOnlyList<TaskPreviewLite> Tasks);
public sealed record AttemptActual(string AttemptId, string TaskId, string Role, string Model, double? Estimate, double? Cost, int? InputTokens, int? OutputTokens, string? PriceStatus, string Status);
public sealed record PlanStart(IReadOnlyList<string> Started, IReadOnlyList<SkippedTask> Skipped);

/// <summary>Proposals, the accepted plan, and what the team can do next. Approval turns a proposal into ordinary tasks and edges; nothing runs until the owner starts it.</summary>
public static class Plans
{
    private static string Now => DateTime.UtcNow.ToString("O");
    private static StoreException Bad(string code, string message, string recovery) => new(code, message, recovery);

    // --- Proposals ----------------------------------------------------------------------------------------------
    /// <summary>One task per line: Title | owner | criteria | after: Title; Title. Lines that do not parse are ignored; a plan needs at least one.</summary>
    public static (List<PlanTask> Tasks, List<PlanEdge> Edges) Parse(string text)
    {
        List<PlanTask> tasks = [];
        List<(string Key, string After)> pending = [];
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim().TrimStart('-', '*', ' ').Trim();
            while (line.Length > 1 && char.IsDigit(line[0])) line = line[1..];
            line = line.TrimStart('.', ')', ' ');
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[0].Length > 200) continue;
            string key = "t" + (tasks.Count + 1);
            string? after = parts.FirstOrDefault(p => p.StartsWith("after:", StringComparison.OrdinalIgnoreCase));
            string? owner = parts.Length > 1 && !parts[1].StartsWith("after:", StringComparison.OrdinalIgnoreCase) && parts[1].Length > 0 ? parts[1] : null;
            string criteria = parts.Length > 2 && !parts[2].StartsWith("after:", StringComparison.OrdinalIgnoreCase) ? parts[2] : "";
            tasks.Add(new(key, parts[0], owner, criteria, true, null, after == null ? null : after[6..].Trim()));
            if (after != null) pending.Add((key, after[6..]));
        }
        List<PlanEdge> edges = [];
        foreach (var (key, after) in pending)
            foreach (var name in after.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var hit = tasks.Where(t => Norm(t.Title) == Norm(name)).ToList();
                if (hit.Count == 1 && hit[0].Key != key) edges.Add(new(key, hit[0].Key)); // anything else is reported by Problems, never guessed
            }
        return (tasks, edges);
    }

    private static string Norm(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>What stops a proposal from being accepted as written: a prerequisite that names no task, two tasks with one title, or a task that waits for itself.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<PlanTask> tasks)
    {
        List<string> problems = [];
        foreach (var g in tasks.GroupBy(t => Norm(t.Title)).Where(g => g.Count() > 1))
            problems.Add($"{g.Count()} tasks are titled “{g.First().Title}”. Give each task its own title.");
        foreach (var t in tasks.Where(t => !string.IsNullOrWhiteSpace(t.After)))
            foreach (var name in t.After!.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int hits = tasks.Count(x => Norm(x.Title) == Norm(name));
                if (Norm(name) == Norm(t.Title)) problems.Add($"“{t.Title}” lists itself as a prerequisite.");
                else if (hits == 0) problems.Add($"“{t.Title}” needs “{name}”, but no task has that title.");
                else if (hits > 1) problems.Add($"“{t.Title}” needs “{name}”, which matches {hits} tasks.");
            }
        return problems;
    }

    internal static void Validate(IReadOnlyList<PlanTask> tasks, IReadOnlyList<PlanEdge> edges)
    {
        if (tasks.Count is 0 or > 40) throw Bad("invalid_plan", "A plan needs 1 to 40 tasks.", "Add a task, or remove some.");
        if (tasks.Select(t => t.Key).Distinct().Count() != tasks.Count) throw Bad("invalid_plan", "Two tasks share an ID.", "Reload the proposal.");
        foreach (var t in tasks)
            if (t.Title.Trim().Length is 0 or > 200 || t.Title.Any(char.IsControl)) throw Bad("invalid_title", "A task title needs 1 to 200 characters.", "Enter a shorter title without control characters.");
        var keys = tasks.Select(t => t.Key).ToHashSet();
        foreach (var e in edges)
            if (!keys.Contains(e.Key) || !keys.Contains(e.Requires) || e.Key == e.Requires) throw Bad("invalid_plan", "A prerequisite points at a task that is not in the plan.", "Remove or fix it.");
        // Cycle check by repeatedly removing tasks with no remaining prerequisites.
        var left = edges.Select(e => (e.Key, e.Requires)).Distinct().ToList();
        var open = keys.ToHashSet();
        while (open.Count > 0)
        {
            var free = open.Where(k => !left.Any(e => e.Key == k && open.Contains(e.Requires))).ToList();
            if (free.Count == 0) throw Bad("cycle", "These prerequisites loop back on themselves.", "Remove one of them.");
            foreach (var k in free) open.Remove(k);
        }
    }

    public static CommandResult Propose(string workspace, string commandId, string projectId, string brief, IReadOnlyList<PlanTask> tasks, IReadOnlyList<PlanEdge> edges,
                                        string? model, int? inTok, int? outTok, Func<string> newId) =>
        TaskStore.Run(workspace, commandId, "plan_proposed", db =>
        {
            Validate(tasks, edges);
            if (TaskStore.Scalar<long>(db, "SELECT count(*) FROM projects WHERE id=$p", ("$p", projectId)) == 0) throw Bad("not_found", "That project no longer exists.", "Reload the workspace.");
            string id = newId();
            TaskStore.Exec(db, "INSERT INTO plans(id,project_id,brief,status,version,created_at,planner_model,planner_input,planner_output) VALUES($i,$p,$b,'proposed',1,$at,$m,$in,$out)",
                ("$i", id), ("$p", projectId), ("$b", brief), ("$at", Now), ("$m", model), ("$in", inTok), ("$out", outTok));
            Store(db, id, tasks, edges);
            return new(id, 1, FirstTaskOrPlaceholder(db, projectId, id), null, null, $"{tasks.Count} proposed tasks");
        });

    // Events hang off a task; a plan with no tasks yet is filed under the plan's own ID.
    private static string FirstTaskOrPlaceholder(SqliteConnection db, string projectId, string planId) => planId;

    private static void Store(SqliteConnection db, string planId, IReadOnlyList<PlanTask> tasks, IReadOnlyList<PlanEdge> edges)
    {
        TaskStore.Exec(db, "DELETE FROM plan_tasks WHERE plan_id=$p", ("$p", planId));
        TaskStore.Exec(db, "DELETE FROM plan_edges WHERE plan_id=$p", ("$p", planId));
        int pos = 0;
        foreach (var t in tasks)
            TaskStore.Exec(db, "INSERT INTO plan_tasks(plan_id,key,title,owner,criteria,review,task_id,position,after_text) VALUES($p,$k,$t,$o,$c,$r,NULL,$n,$a)",
                ("$p", planId), ("$k", t.Key), ("$t", t.Title.Trim()), ("$o", string.IsNullOrWhiteSpace(t.Owner) ? null : t.Owner.Trim()), ("$c", t.Criteria ?? ""), ("$r", t.Review ? 1 : 0), ("$n", pos++), ("$a", string.IsNullOrWhiteSpace(t.After) ? null : t.After.Trim()));
        foreach (var e in edges.DistinctBy(e => (e.Key, e.Requires)))
            TaskStore.Exec(db, "INSERT INTO plan_edges VALUES($p,$k,$r)", ("$p", planId), ("$k", e.Key), ("$r", e.Requires));
    }

    internal static PlanView Load(SqliteConnection db, string planId)
    {
        var r = Reader.Rows(db, "SELECT id,project_id,brief,status,version,created_at,decided_at,planner_model,planner_input,planner_output FROM plans WHERE id=$i", ("$i", planId)).FirstOrDefault()
            ?? throw Bad("not_found", "That plan no longer exists.", "Reload the plan list.");
        var tasks = Reader.Rows(db, "SELECT key,title,owner,criteria,review,task_id,after_text FROM plan_tasks WHERE plan_id=$p ORDER BY position", ("$p", planId))
            .Select(t => new PlanTask(Reader.S(t[0]), Reader.S(t[1]), t[2] as string, Reader.S(t[3]), Reader.I(t[4]) != 0, t[5] as string, t[6] as string)).ToList();
        var edges = Reader.Rows(db, "SELECT key,requires_key FROM plan_edges WHERE plan_id=$p ORDER BY key,requires_key", ("$p", planId)).Select(e => new PlanEdge(Reader.S(e[0]), Reader.S(e[1]))).ToList();
        return new(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.I(r[4]), Reader.S(r[5]), r[6] as string, tasks, edges, r[7] as string,
            r[8] is null ? null : Reader.I(r[8]), r[9] is null ? null : Reader.I(r[9]));
    }

    public static PlanView Read(string workspace, string planId)
    {
        using var db = Reader.Open(workspace);
        return Load(db, planId);
    }

    /// <summary>The project's plans, newest first.</summary>
    public static IReadOnlyList<PlanView> ForProject(string workspace, string projectId)
    {
        using var db = Reader.Open(workspace);
        return [.. Reader.Rows(db, "SELECT id FROM plans WHERE project_id=$p ORDER BY created_at DESC, rowid DESC", ("$p", projectId)).Select(r => Load(db, Reader.S(r[0])))];
    }

    public static CommandResult Edit(string workspace, string commandId, string planId, int expectedVersion, IReadOnlyList<PlanTask> tasks, IReadOnlyList<PlanEdge> edges) =>
        TaskStore.Run(workspace, commandId, "plan_edited", db =>
        {
            var p = Load(db, planId);
            if (p.Status != "proposed") throw Bad("plan_decided", $"This plan was already {p.Status}.", "Ask the planner for a new proposal.");
            if (p.Version != expectedVersion) throw Bad("conflict", $"The proposal changed (now version {p.Version}, you had {expectedVersion}).", "Reload the proposal, then edit again.");
            Validate(tasks, edges);
            Store(db, planId, tasks, edges);
            TaskStore.Exec(db, "UPDATE plans SET version=version+1 WHERE id=$i", ("$i", planId));
            return new(planId, p.Version + 1, planId, null, null, $"{tasks.Count} tasks");
        });

    public static CommandResult Dismiss(string workspace, string commandId, string planId, int expectedVersion) =>
        TaskStore.Run(workspace, commandId, "plan_dismissed", db =>
        {
            var p = Load(db, planId);
            if (p.Status == "accepted") throw Bad("plan_decided", "This plan was already approved.", "Cancel its tasks on the board instead.");
            if (p.Status == "proposed" && p.Version != expectedVersion) throw Bad("conflict", "The proposal changed.", "Reload the proposal, then decide again.");
            TaskStore.Exec(db, "UPDATE plans SET status='dismissed', decided_at=$at WHERE id=$i AND status='proposed'", ("$at", Now), ("$i", planId));
            return new(planId, p.Version, planId, null, null, "dismissed; no work started");
        });

    /// <summary>Creates the tasks and edges of exactly the version the owner saw. A second approval, even from another window, creates nothing.</summary>
    public static CommandResult Approve(string workspace, string commandId, string planId, int expectedVersion, Func<string> newId) =>
        TaskStore.Run(workspace, commandId, "plan_approved", db =>
        {
            var p = Load(db, planId);
            if (p.Status == "accepted") return new(planId, p.Version, planId, null, null, "already approved");
            if (p.Status == "dismissed") throw Bad("plan_decided", "This plan was dismissed.", "Ask the planner for a new proposal.");
            if (p.Version != expectedVersion) throw Bad("conflict", $"The proposal changed (now version {p.Version}, you had {expectedVersion}).", "Review the latest version, then approve it.");
            Validate(p.Tasks, p.Edges);
            if (Problems(p.Tasks) is { Count: > 0 } open)
                throw Bad("unresolved_reference", open[0] + (open.Count > 1 ? $" ({open.Count - 1} more.)" : ""), "Correct the proposal and save it, then approve.");
            Dictionary<string, string> ids = [];
            foreach (var t in p.Tasks)
            {
                string id = newId();
                ids[t.Key] = id;
                TaskStore.Exec(db, "INSERT INTO tasks(id,project_id,title,status,created_at,revision,outcome,owner,review_required) VALUES($i,$p,$t,'open',$at,0,$o,$w,$r)",
                    ("$i", id), ("$p", p.ProjectId), ("$t", t.Title.Trim()), ("$at", Now), ("$o", string.IsNullOrWhiteSpace(t.Criteria) ? null : t.Criteria.Trim()), ("$w", t.Owner), ("$r", t.Review ? 1 : 0));
                TaskStore.Exec(db, "UPDATE plan_tasks SET task_id=$t WHERE plan_id=$p AND key=$k", ("$t", id), ("$p", planId), ("$k", t.Key));
            }
            foreach (var e in p.Edges) TaskStore.Exec(db, "INSERT INTO hub_dependencies VALUES($t,$r)", ("$t", ids[e.Key]), ("$r", ids[e.Requires]));
            TaskStore.Exec(db, "UPDATE plans SET status='accepted', decided_at=$at WHERE id=$i", ("$at", Now), ("$i", planId));
            string first = ids[p.Tasks[0].Key];
            // One event per created task, so the channel and the board see the same sequence the graph does.
            foreach (var t in p.Tasks)
                TaskStore.Exec(db, "INSERT INTO events(command_id,task_id,kind,from_state,to_state,detail,created_at,event_schema) VALUES($c,$t,'task_created',NULL,'open',$d,$at,2)",
                    ("$c", commandId + ":" + t.Key), ("$t", ids[t.Key]), ("$d", JsonSerializer.Serialize(new { id = ids[t.Key], detail = "title=" + t.Title.Trim() })), ("$at", Now));
            return new(planId, p.Version, first, null, null, $"approved version {p.Version}: {p.Tasks.Count} tasks");
        });

    // --- State, graph, handoffs -------------------------------------------------------------------------------------
    internal static bool Satisfied(SqliteConnection db, string taskId) =>
        TaskStore.Scalar<long>(db, """
            SELECT count(*) FROM tasks p WHERE p.id=$t AND p.status='done'
            AND NOT (p.review_required=1 AND COALESCE((SELECT accepted_by FROM outputs WHERE task_id=p.id ORDER BY revision DESC LIMIT 1),'')='')
            """, ("$t", taskId)) > 0;

    private static readonly string[] Launched = ["leased", "sending", "streaming", "completed", "needs_reconciliation"];

    private static string NodeState(SqliteConnection db, string taskId, bool prereqsMet)
    {
        if (Satisfied(db, taskId)) return "accepted";
        var attempts = Reader.Rows(db, "SELECT status FROM attempts WHERE task_id=$t AND role='builder' ORDER BY created_at DESC, rowid DESC", ("$t", taskId)).Select(r => Reader.S(r[0])).ToList();
        if (attempts.Any(s => s is "leased" or "sending" or "streaming")) return "running";
        var latest = Reader.Rows(db, "SELECT id, revision FROM outputs WHERE task_id=$t ORDER BY revision DESC LIMIT 1", ("$t", taskId)).FirstOrDefault();
        if (latest != null)
        {
            bool changes = TaskStore.Scalar<long>(db, "SELECT count(*) FROM decisions WHERE output_id=$o AND decision='changes_requested'", ("$o", Reader.S(latest[0]))) > 0;
            return changes ? "changes_requested" : "awaiting_review";
        }
        if (attempts.Count > 0 && attempts[0] is "failed" or "interrupted" or "stopped" or "cancelled" or "paused" or "abandoned" or "needs_reconciliation") return "failed";
        return prereqsMet ? "ready" : "waiting";
    }

    private static bool AllPrerequisitesSatisfied(SqliteConnection db, string taskId) =>
        Reader.Rows(db, "SELECT requires_id FROM hub_dependencies WHERE task_id=$t", ("$t", taskId)).All(r => Satisfied(db, Reader.S(r[0])));

    private static bool Queued(SqliteConnection db, string taskId) =>
        TaskStore.Scalar<long>(db, "SELECT count(*) FROM start_queue WHERE task_id=$t AND status='waiting'", ("$t", taskId)) > 0;

    /// <summary>
    /// The one eligibility rule, read from the board as it is now: a task that is cancelled or done is not a candidate, and every prerequisite on the board
    /// (not the proposal's copy of the edges) must be satisfied. The graph, the estimate and the dispatch transaction all call this.
    /// </summary>
    internal static Eligibility EligibilityOf(SqliteConnection db, string taskId)
    {
        var t = TaskStore.Task(db, taskId);
        if (t.Status == "cancelled") return new(false, $"“{t.Title}” was cancelled. Reopen it before it can run.", []);
        if (t.Status == "done") return new(false, $"“{t.Title}” is already done.", []);
        List<Blocker> blockers = [];
        foreach (var r in Reader.Rows(db, "SELECT p.id, p.title, p.status FROM hub_dependencies e JOIN tasks p ON p.id=e.requires_id WHERE e.task_id=$t ORDER BY p.title, p.id", ("$t", taskId)))
        {
            string id = Reader.S(r[0]);
            if (Satisfied(db, id)) continue;
            string state = Reader.S(r[2]) == "cancelled" ? "cancelled" : NodeState(db, id, AllPrerequisitesSatisfied(db, id));
            blockers.Add(Why(db, id, Reader.S(r[1]), state));
        }
        return blockers.Count == 0 ? new(true, null, []) : new(false, "Waiting for " + string.Join(", ", blockers.Select(b => "“" + b.Title + "”")) + ".", blockers);
    }

    public static PlanStatus Status(string workspace, string planId)
    {
        using var db = Reader.Open(workspace);
        var p = Load(db, planId);
        if (p.Status != "accepted") return new(planId, p.Status, Seq(db), [], p.Edges, []);
        var keyOf = p.Tasks.Where(t => t.TaskId != null).ToDictionary(t => t.TaskId!, t => t.Key);
        List<PlanNode> nodes = [];
        List<PlanEdge> live = [];
        foreach (var t in p.Tasks)
        {
            var el = EligibilityOf(db, t.TaskId!);
            var row = TaskStore.Task(db, t.TaskId!);
            string state = row.Status == "cancelled" ? "cancelled" : Queued(db, t.TaskId!) ? "queued" : NodeState(db, t.TaskId!, AllPrerequisitesSatisfied(db, t.TaskId!));
            var needs = Reader.Rows(db, "SELECT p.id, p.title FROM hub_dependencies e JOIN tasks p ON p.id=e.requires_id WHERE e.task_id=$t ORDER BY p.title, p.id", ("$t", t.TaskId));
            var feeds = Reader.Rows(db, "SELECT x.title FROM hub_dependencies e JOIN tasks x ON x.id=e.task_id WHERE e.requires_id=$t ORDER BY x.title, x.id", ("$t", t.TaskId));
            foreach (var n in needs) if (keyOf.TryGetValue(Reader.S(n[0]), out var rk)) live.Add(new(t.Key, rk));
            string? active = TaskStore.Scalar<string>(db, "SELECT id FROM attempts WHERE task_id=$t AND status IN ('leased','sending','streaming') ORDER BY created_at LIMIT 1", ("$t", t.TaskId));
            long last = TaskStore.Scalar<long?>(db, "SELECT max(seq) FROM events WHERE task_id=$t", ("$t", t.TaskId)) ?? 0;
            nodes.Add(new(t.Key, t.TaskId!, row.Title, t.Owner, state, t.Review, el.Blockers, last, active, row.Revision, [.. needs.Select(n => Reader.S(n[1]))], [.. feeds.Select(f => Reader.S(f[0]))]));
        }
        return new(planId, p.Status, Seq(db), nodes, live, Overlaps(db, p));
    }

    private static long Seq(SqliteConnection db) => TaskStore.Scalar<long?>(db, "SELECT max(seq) FROM events") ?? 0;

    private static Blocker Why(SqliteConnection db, string taskId, string title, string state)
    {
        var o = Reader.Rows(db, "SELECT id, revision FROM outputs WHERE task_id=$t ORDER BY revision DESC LIMIT 1", ("$t", taskId)).FirstOrDefault();
        string? outId = o == null ? null : Reader.S(o[0]);
        int? rev = o == null ? null : Reader.I(o[1]);
        string reason = state switch
        {
            "running" => $"“{title}” is still running.",
            "queued" => $"“{title}” is waiting for a free project slot.",
            "cancelled" => $"“{title}” was cancelled, so it will not be accepted unless you reopen it.",
            "failed" => $"“{title}” failed: " + (TaskStore.Scalar<string>(db, "SELECT stop_reason FROM attempts WHERE task_id=$t AND role='builder' ORDER BY created_at DESC, rowid DESC LIMIT 1", ("$t", taskId)) ?? "its attempt did not finish."),
            "awaiting_review" => $"“{title}” has revision {rev} waiting for your decision.",
            "changes_requested" => $"You asked for changes to revision {rev} of “{title}”; it needs a new revision.",
            "ready" => $"“{title}” has not started yet.",
            _ => $"“{title}” is waiting for its own prerequisites.",
        };
        return new(taskId, title, state, reason, outId, rev);
    }

    private static IReadOnlyList<Overlap> Overlaps(SqliteConnection db, PlanView p)
    {
        var ids = p.Tasks.Where(t => t.TaskId != null).Select(t => $"'{t.TaskId}'");
        var rows = Reader.Rows(db, $"SELECT id, sent_at, COALESCE(ended_at,'9999') FROM attempts WHERE task_id IN ({string.Join(',', ids)}) AND sent_at IS NOT NULL ORDER BY sent_at, id");
        List<Overlap> pairs = [];
        for (int i = 0; i < rows.Count; i++)
            for (int j = i + 1; j < rows.Count; j++)
                if (string.CompareOrdinal(Reader.S(rows[j][1]), Reader.S(rows[i][2])) < 0 && string.CompareOrdinal(Reader.S(rows[i][1]), Reader.S(rows[j][2])) < 0)
                    pairs.Add(new(Reader.S(rows[i][0]), Reader.S(rows[j][0])));
        return pairs;
    }

    /// <summary>Stored events that hand work from one task to another since <paramref name="afterSeq"/>: a draft delivered, a review recorded, an output accepted.</summary>
    public static IReadOnlyList<HandoffEvent> Handoffs(string workspace, string projectId, long afterSeq)
    {
        using var db = Reader.Open(workspace);
        return [.. Reader.Rows(db, """
            SELECT e.seq, e.kind, e.task_id FROM events e JOIN tasks t ON t.id=e.task_id
            WHERE t.project_id=$p AND e.seq>$a AND e.kind IN ('output_proposed','review_recorded','output_accepted') ORDER BY e.seq
            """, ("$p", projectId), ("$a", afterSeq)).Select(r =>
        {
            string task = Reader.S(r[2]), kind = Reader.S(r[1]);
            var targets = kind == "output_accepted" ? Reader.Rows(db, "SELECT task_id FROM hub_dependencies WHERE requires_id=$t ORDER BY task_id", ("$t", task)).Select(x => Reader.S(x[0])).ToList() : [task];
            return new HandoffEvent(Convert.ToInt64(r[0]), kind, task, targets);
        })];
    }

    /// <summary>Prerequisites first, in dependency order, then the node itself: the order a keyboard walk follows.</summary>
    public static IReadOnlyList<string> Traversal(IReadOnlyList<PlanEdge> edges, string key)
    {
        List<string> order = [];
        void Visit(string k)
        {
            if (order.Contains(k)) return;
            foreach (var e in edges.Where(e => e.Key == k).OrderBy(e => e.Requires, StringComparer.Ordinal)) Visit(e.Requires);
            order.Add(k);
        }
        Visit(key);
        return order;
    }

    // --- After the run -------------------------------------------------------------------------------------------------
    /// <summary>Every attempt of the plan's tasks with what was estimated beside what was used.</summary>
    public static IReadOnlyList<AttemptActual> Actuals(string workspace, string planId)
    {
        using var db = Reader.Open(workspace);
        return [.. Reader.Rows(db, """
            SELECT a.id, a.task_id, a.role, a.requested_model, a.estimate, a.cost, a.input_tokens, a.output_tokens, a.price_status, a.status
            FROM attempts a JOIN plan_tasks t ON t.task_id=a.task_id WHERE t.plan_id=$p ORDER BY a.created_at, a.rowid
            """, ("$p", planId)).Select(r => new AttemptActual(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), r[4] is null ? null : Convert.ToDouble(r[4]), r[5] is null ? null : Convert.ToDouble(r[5]),
                r[6] is null ? null : Reader.I(r[6]), r[7] is null ? null : Reader.I(r[7]), r[8] as string, Reader.S(r[9])))];
    }
}
