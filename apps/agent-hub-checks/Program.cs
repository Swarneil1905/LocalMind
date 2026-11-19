using LocalMind.AgentHub;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

var folder = Directory.CreateTempSubdirectory("localmind-hub-check-");
try
{
    string file = Path.Combine(folder.FullName, "projects.db");
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT);
            CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT,title TEXT,status TEXT,created_at TEXT);
            INSERT INTO projects VALUES ('a','Launch'),('b','Private');
            INSERT INTO tasks VALUES ('a1','a','Review','open','1'),('a2','a','Ship','open','2'),('b1','b','Secret','open','1');
            """;
        cmd.ExecuteNonQuery();
        var vm = new HubViewModel();
        await vm.OpenAsync(file);
        Check(vm.Tasks.Count == 2 && vm.Tasks.All(t => t.Prerequisites == "No prerequisites"), "legacy database");
        cmd.CommandText = """
            CREATE TABLE hub_dependencies(task_id TEXT,requires_id TEXT);
            INSERT INTO hub_dependencies VALUES ('a2','a1'),('a2','b1');
            """;
        cmd.ExecuteNonQuery();
    }
    var hash = SHA256.HashData(File.ReadAllBytes(file));
    var hub = new HubViewModel();
    await hub.OpenAsync(file);
    Check(hub.Projects.Count == 2 && hub.ProjectHeading == "Launch", "project selection");
    Check(hub.Tasks.Single(t => t.Title == "Ship").Prerequisites == "Requires: Review", "scoped prerequisites");
    Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "database remains unchanged");
    var ship = hub.Tasks.Single(t => t.Id == "a2");
    Check(ship.OwnerLabel == "Unassigned" && ship.FirstBlockerId == "a1" &&
          ship.DependencySummary == "Requires Review (open)", "real blocker target, no invented assignee");
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE tasks SET status='done' WHERE id='a1';
            INSERT INTO tasks VALUES ('a3','a','Review','cancelled','3');
            INSERT INTO hub_dependencies VALUES ('a2','a3');
            """;
        cmd.ExecuteNonQuery();
        await hub.OpenAsync(file);
        ship = hub.Tasks.Single(t => t.Id == "a2");
        Check(ship.FirstBlockerId == "a3" && ship.DependencySummary.Contains("cancelled"),
              "duplicate titles resolve by ID; cancellation does not satisfy a prerequisite");
        cmd.CommandText = "UPDATE tasks SET status='done' WHERE id='a3'";
        cmd.ExecuteNonQuery();
    }
    await hub.OpenAsync(file);
    ship = hub.Tasks.Single(t => t.Id == "a2");
    Check(!ship.HasBlocker && ship.DependencySummary == "All prerequisites complete",
          "completed prerequisites clear the jump target");
    string missing = Path.Combine(folder.FullName, "missing.db");
    await hub.OpenAsync(missing);
    Check(!File.Exists(missing) && hub.Tasks.Count == 0 && hub.StatusText.StartsWith("Could not open"), "failed open clears stale tasks without creating a file");
}
finally { folder.Delete(true); }

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}");
}
