using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub;

public sealed record ProjectItem(string Id, string Name);
public sealed record Prerequisite(string Id, string Title, string Status)
{
    // Only "done" satisfies a prerequisite; cancelled work is still unresolved.
    public bool IsResolved => Status == "done";
    public string StatusLabel => Status.Replace('_', ' ');
}

public sealed record TaskItem(string Id, string Title, string Status, IReadOnlyList<Prerequisite> Dependencies)
{
    // The current LocalMind schema has no task assignee.
    public string OwnerLabel => "Unassigned";
    public string Prerequisites => Dependencies.Count == 0 ? "No prerequisites" :
        "Requires: " + string.Join(", ", Dependencies.Select(d => d.Title));
    private Prerequisite? FirstOpen => Dependencies.FirstOrDefault(d => !d.IsResolved);
    public string? FirstBlockerId => FirstOpen?.Id;
    public bool HasBlocker => FirstBlockerId != null;
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
        q.CommandText = hasEdges ? """
            SELECT t.title,t.status,p.title,t.id,p.id,p.status FROM tasks t
            LEFT JOIN hub_dependencies e ON e.task_id=t.id
            LEFT JOIN tasks p ON p.id=e.requires_id AND p.project_id=t.project_id
            WHERE t.project_id=$id ORDER BY t.created_at,t.id,p.title,p.id
            """ : "SELECT title,status,NULL,id,NULL,NULL FROM tasks WHERE project_id=$id ORDER BY created_at,id";
        q.Parameters.AddWithValue("$id", project);
        using var r = q.ExecuteReader();
        List<TaskItem> result = [];
        List<Prerequisite> needs = [];
        string? last = null;
        string title = "", status = "";
        void Finish() => result.Add(new(last!, title, status, needs.ToArray()));
        while (r.Read())
        {
            var id = r.GetString(3);
            if (id != last)
            {
                if (last != null) Finish();
                needs.Clear();
                (last, title, status) = (id, r.GetString(0), r.GetString(1).Replace('_', ' '));
            }
            if (!r.IsDBNull(4)) needs.Add(new(r.GetString(4), r.GetString(2), r.GetString(5)));
        }
        if (last != null) Finish();
        return result;
    }
}
