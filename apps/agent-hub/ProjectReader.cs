using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub;

public sealed record ProjectItem(string Id, string Name);
public sealed record Prerequisite(string Id, string Title, string Status)
{
    // Only "done" satisfies a prerequisite; cancelled work is still unresolved.
    public bool IsResolved => Status == "done";
    public string StatusLabel => Status.Replace('_', ' ');
}

public sealed record TaskItem(string Id, string Title, string Status, IReadOnlyList<Prerequisite> Dependencies, string? Outcome = null, string? Owner = null, int Revision = 0)
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
            return $"Requires {pending.Title} ({pending.Status.Replace('_', ' ')})"
                + (others > 0 ? $" · {others} more unresolved" : "");
        }
    }
}

public sealed record MoveResult(bool Allowed, Prerequisite? Blocker, string Reason);

public static class MoveEligibility
{
    /// <summary>Open and cancelled need nothing; any other state needs every prerequisite done. Cancelled prerequisites stay unresolved.</summary>
    public static MoveResult Evaluate(TaskItem task, string target)
    {
        if (target is "open" or "cancelled" || task.FirstOpen is not { } blocker) return new(true, null, "No prerequisite blocks this move.");
        int others = task.Dependencies.Count(d => !d.IsResolved) - 1;
        return new(false, blocker, $"Blocked: can’t move to {target} until {blocker.Title} ({blocker.StatusLabel}) is done" + (others > 0 ? $", plus {others} more." : "."));
    }
}

internal sealed class ProjectReader(string path)
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

    public List<TaskItem> Tasks(string project)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='hub_dependencies'";
        bool hasEdges = Convert.ToInt64(q.ExecuteScalar()) != 0;
        q.CommandText = "SELECT count(*) FROM pragma_table_info('tasks') WHERE name IN ('outcome','owner','revision')";
        bool hasDetail = Convert.ToInt64(q.ExecuteScalar()) == 3;
        string detail = hasDetail ? "t.outcome,t.owner,t.revision" : "NULL,NULL,0";
        q.CommandText = hasEdges ? $"""
            SELECT t.title,t.status,p.title,t.id,p.id,p.status,{detail} FROM tasks t
            LEFT JOIN hub_dependencies e ON e.task_id=t.id
            LEFT JOIN tasks p ON p.id=e.requires_id AND p.project_id=t.project_id
            WHERE t.project_id=$id ORDER BY t.created_at,t.id,p.title,p.id
            """ : $"SELECT title,status,NULL,id,NULL,NULL,{(hasDetail ? "outcome,owner,revision" : "NULL,NULL,0")} FROM tasks WHERE project_id=$id ORDER BY created_at,id";
        q.Parameters.AddWithValue("$id", project);
        using var r = q.ExecuteReader();
        List<TaskItem> result = [];
        List<Prerequisite> needs = [];
        string? last = null;
        string title = "", status = "";
        string? outcome = null, owner = null;
        int revision = 0;
        void Finish() => result.Add(new(last!, title, status, needs.ToArray(), outcome, owner, revision));
        while (r.Read())
        {
            var id = r.GetString(3);
            if (id != last)
            {
                if (last != null) Finish();
                needs.Clear();
                (last, title, status) = (id, r.GetString(0), r.GetString(1).Replace('_', ' '));
            (outcome, owner, revision) = (r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? 0 : r.GetInt32(8));
            }
            if (!r.IsDBNull(4)) needs.Add(new(r.GetString(4), r.GetString(2), r.GetString(5)));
        }
        if (last != null) Finish();
        return result;
    }
}
