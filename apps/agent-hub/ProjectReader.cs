using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub;

public sealed record ProjectItem(string Id, string Name);
public sealed record TaskItem(string Title, string Status, string Prerequisites);

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
            SELECT t.title,t.status,p.title,t.id FROM tasks t
            LEFT JOIN hub_dependencies e ON e.task_id=t.id
            LEFT JOIN tasks p ON p.id=e.requires_id AND p.project_id=t.project_id
            WHERE t.project_id=$id ORDER BY t.created_at,t.id,p.title,p.id
            """ : "SELECT title,status,NULL,id FROM tasks WHERE project_id=$id ORDER BY created_at,id";
        q.Parameters.AddWithValue("$id", project);
        using var r = q.ExecuteReader();
        List<TaskItem> result = [];
        List<string> needs = [];
        string? last = null;
        string title = "", status = "";
        void Finish() => result.Add(new(title, status, needs.Count == 0 ? "No prerequisites" : "Requires: " + string.Join(", ", needs)));
        while (r.Read())
        {
            var id = r.GetString(3);
            if (id != last)
            {
                if (last != null) Finish();
                needs.Clear();
                (last, title, status) = (id, r.GetString(0), r.GetString(1).Replace('_', ' '));
            }
            if (!r.IsDBNull(2)) needs.Add(r.GetString(2));
        }
        if (last != null) Finish();
        return result;
    }
}
