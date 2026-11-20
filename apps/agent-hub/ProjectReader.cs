using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub;

public sealed record ProjectItem(string Id, string Name);
public sealed record Prerequisite(string Id, string Title, string Status, bool AwaitingReview = false)
{
    // Only "done" satisfies a prerequisite; cancelled work is still unresolved.
    public bool IsResolved => Status == "done" && !AwaitingReview;
    public string StatusLabel => Status.Replace('_', ' ') + (AwaitingReview ? ", awaiting your review" : "");
}

public sealed record TaskItem(string Id, string Title, string Status, IReadOnlyList<Prerequisite> Dependencies, string? Outcome = null, string? Owner = null, int Revision = 0,
                              bool ReviewRequired = false, bool ReviewAccepted = false)
{
    // Legacy databases have no assignee or outcome; only recorded values are shown.
    public string OwnerLabel => string.IsNullOrWhiteSpace(Owner) ? "Unassigned" : Owner;
    public bool HasOutcome => !string.IsNullOrWhiteSpace(Outcome);
    public string Prerequisites => Dependencies.Count == 0 ? "No prerequisites" :
        "Requires: " + string.Join(", ", Dependencies.Select(d => d.Title));
    internal Prerequisite? FirstOpen => Dependencies.FirstOrDefault(d => !d.IsResolved);
    public string? FirstBlockerId => FirstOpen?.Id;
    public bool HasBlocker => FirstBlockerId != null;
    /// <summary>The same answer the card, the inspector and a future move command read.</summary>
    public MoveResult Start => MoveEligibility.Evaluate(this, "in progress");
    /// <summary>One line for the card: why it cannot start, or the dependency state.</summary>
    public string MoveNote => Start.Allowed ? DependencySummary : Start.Reason;
    public string DependencySummary
    {
        get
        {
            if (FirstOpen is not { } pending)
                return Dependencies.Count == 0 ? "No prerequisites" : "All prerequisites complete";
            var others = Dependencies.Count(d => !d.IsResolved) - 1;
            return $"Requires {pending.Title} ({pending.StatusLabel})"
                + (others > 0 ? $" · {others} more unresolved" : "");
        }
    }
}

public sealed record MoveResult(bool Allowed, Prerequisite? Blocker, string Reason);

public static class MoveEligibility
{
    /// <summary>Open and cancelled need nothing. Any other state needs every prerequisite done, and Done on a review-gated task needs an accepted output.</summary>
    public static MoveResult Evaluate(TaskItem task, string target)
    {
        if (target is "open" or "cancelled") return new(true, null, "No prerequisite blocks this move.");
        if (task.FirstOpen is { } blocker)
        {
            int others = task.Dependencies.Count(d => !d.IsResolved) - 1;
            return new(false, blocker, $"Blocked: can’t move to {target} until {blocker.Title} ({blocker.StatusLabel}) is done" + (others > 0 ? $", plus {others} more." : "."));
        }
        if (target == "done" && task.ReviewRequired && !task.ReviewAccepted)
            return new(false, null, $"Blocked: “{task.Title}” needs an accepted output before it can be Done.");
        return new(true, null, "No prerequisite blocks this move.");
    }
}

/// <summary>What the review panel shows for one task. Every value is read from the workspace; nothing is inferred.</summary>
public sealed record ReviewInfo(string PolicyName, int PolicyRevision, string? OutputId, int Revision, string? Text, string? Author, string? RunId, string? AcceptedBy,
                                string? Recommendation, string? Feedback, string? FeedbackBy, string? Decision, string? DecisionBy,
                                int LatestRevision = 0, string? PreviousOutputId = null)
{
    public bool HasOutput => OutputId != null;
    public bool Decided => AcceptedBy != null || Decision != null;
    /// <summary>A newer revision exists than the one on screen.</summary>
    public bool IsStale => HasOutput && LatestRevision > Revision;
}

public sealed class ProjectReader(string path)
{
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3
        }.ToString());
        db.Open();
        return db;
    }

    public List<ProjectItem> Projects()
    {
        using var db = Open();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT id,name FROM projects ORDER BY name COLLATE NOCASE,id";
        using var r = q.ExecuteReader();
        List<ProjectItem> items = [];
        while (r.Read()) items.Add(new(r.GetString(0), r.GetString(1)));
        return items;
    }

    private const string Accepted = "COALESCE((SELECT accepted_by FROM outputs WHERE task_id={0}.id ORDER BY revision DESC LIMIT 1),'')";

    public List<TaskItem> Tasks(string project)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='hub_dependencies'";
        bool hasEdges = Convert.ToInt64(q.ExecuteScalar()) != 0;
        q.CommandText = "SELECT count(*) FROM pragma_table_info('tasks') WHERE name IN ('outcome','owner','revision','review_required')";
        bool hasDetail = Convert.ToInt64(q.ExecuteScalar()) == 4;
        string detail = hasDetail ? $"t.outcome,t.owner,t.revision,t.review_required,{string.Format(Accepted, "t")}<>''" : "NULL,NULL,0,0,0";
        string awaiting = hasDetail ? $"(p.review_required=1 AND {string.Format(Accepted, "p")}='')" : "0";
        q.CommandText = hasEdges ? $"""
            SELECT t.title,t.status,p.title,t.id,p.id,p.status,{detail},{awaiting} FROM tasks t
            LEFT JOIN hub_dependencies e ON e.task_id=t.id
            LEFT JOIN tasks p ON p.id=e.requires_id AND p.project_id=t.project_id
            WHERE t.project_id=$id ORDER BY t.created_at,t.id,p.title,p.id
            """ : "SELECT title,status,NULL,id,NULL,NULL,NULL,NULL,0,0,0,0 FROM tasks WHERE project_id=$id ORDER BY created_at,id";
        q.Parameters.AddWithValue("$id", project);
        using var r = q.ExecuteReader();
        List<TaskItem> result = [];
        List<Prerequisite> needs = [];
        string? last = null;
        string title = "", status = "";
        string? outcome = null, owner = null;
        int revision = 0;
        bool gated = false, accepted = false;
        void Finish() => result.Add(new(last!, title, status, needs.ToArray(), outcome, owner, revision, gated, accepted));
        while (r.Read())
        {
            var id = r.GetString(3);
            if (id != last)
            {
                if (last != null) Finish();
                needs.Clear();
                (last, title, status) = (id, r.GetString(0), r.GetString(1).Replace('_', ' '));
                (outcome, owner, revision) = (r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? 0 : r.GetInt32(8));
                (gated, accepted) = (!r.IsDBNull(9) && r.GetInt32(9) != 0, !r.IsDBNull(10) && r.GetInt32(10) != 0);
            }
            if (!r.IsDBNull(4)) needs.Add(new(r.GetString(4), r.GetString(2), r.GetString(5), !r.IsDBNull(11) && r.GetInt32(11) != 0));
        }
        if (last != null) Finish();
        return result;
    }

    /// <summary>Null for legacy files and stores older than the review tables.</summary>
    public (string Name, int Revision)? Policy()
    {
        using var db = Open();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='policy'";
        if (Convert.ToInt64(q.ExecuteScalar()) == 0) return null;
        q.CommandText = "SELECT name, revision FROM policy WHERE id=1";
        using var r = q.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt32(1)) : null;
    }

    /// <summary>The review of one task. <paramref name="revision"/> keeps a revision the user already has open; null shows the latest.</summary>
    public ReviewInfo? Review(string taskId, int? revision = null)
    {
        if (Policy() is not var (policy, policyRevision)) return null;
        using var db = Open();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT id, revision, text, author_role, run_id, accepted_by FROM outputs WHERE task_id=$t AND ($v IS NULL OR revision=$v) ORDER BY revision DESC LIMIT 1";
        q.Parameters.AddWithValue("$t", taskId);
        q.Parameters.AddWithValue("$v", (object?)revision ?? DBNull.Value);
        string? outId = null, text = null, author = null, run = null, accepted = null;
        int shown = 0;
        using (var r = q.ExecuteReader())
            if (r.Read())
                (outId, shown, text, author, run, accepted) = (r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
        if (outId == null) return new(policy, policyRevision, null, 0, null, null, null, null, null, null, null, null, null);
        q.CommandText = "SELECT max(revision) FROM outputs WHERE task_id=$t";
        q.Parameters.Clear();
        q.Parameters.AddWithValue("$t", taskId);
        int latest = Convert.ToInt32(q.ExecuteScalar());
        q.CommandText = "SELECT id FROM outputs WHERE task_id=$t AND revision<$v ORDER BY revision DESC LIMIT 1";
        q.Parameters.AddWithValue("$v", shown);
        string? previous = q.ExecuteScalar() as string;
        q.Parameters.Clear();
        string? rec = null, feedback = null, by = null, decision = null, decider = null;
        q.CommandText = "SELECT recommendation, comment, reviewer_role FROM reviews WHERE output_id=$o ORDER BY rowid DESC LIMIT 1";
        q.Parameters.Clear();
        q.Parameters.AddWithValue("$o", outId);
        using (var r = q.ExecuteReader())
            if (r.Read()) (rec, feedback, by) = (r.GetString(0), r.GetString(1), r.GetString(2));
        q.CommandText = "SELECT decision, actor FROM decisions WHERE output_id=$o LIMIT 1";
        using (var r = q.ExecuteReader())
            if (r.Read()) (decision, decider) = (r.GetString(0), r.GetString(1));
        return new(policy, policyRevision, outId, shown, text, author, run, accepted, rec, feedback, by, decision, decider, latest, previous);
    }

    /// <summary>Check results recorded for one output, oldest first. Empty when none were ever run.</summary>
    public List<(string Name, string Status, string? Detail)> Checks(string outputId)
    {
        using var db = Open();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='output_checks'";
        if (Convert.ToInt64(q.ExecuteScalar()) == 0) return [];
        q.CommandText = "SELECT name, status, detail FROM output_checks WHERE output_id=$o ORDER BY id";
        q.Parameters.AddWithValue("$o", outputId);
        using var r = q.ExecuteReader();
        List<(string, string, string?)> rows = [];
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return rows;
    }

    /// <summary>The workspace's own ID, which unsent drafts are filed under. Falls back to the file path for stores without one.</summary>
    public string WorkspaceId()
    {
        using var db = Open();
        using var q = db.CreateCommand();
        q.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='workspace'";
        if (Convert.ToInt64(q.ExecuteScalar()) == 0) return path;
        q.CommandText = "SELECT id FROM workspace LIMIT 1";
        return q.ExecuteScalar() as string ?? path;
    }
}
