using LocalMind.AgentHub;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

// Development-only: write the demo database for the Windows smoke checklist and exit.
if (args is ["--fixture", var target]) { MakeFixture(Path.GetFullPath(target)); Console.WriteLine($"Wrote {Path.GetFullPath(target)}"); return; }

if (args is ["--atlas-fixture", var atlas]) { Console.WriteLine($"Wrote {CheckpointB.MakeAtlas(Path.GetFullPath(atlas))}"); return; }

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
    await Workbench(folder.FullName);
    await Regressions(folder.FullName);
    await CheckpointA.Run(folder.FullName);
    await CheckpointB.Run(Path.Combine(folder.FullName, "b"));
    await CheckpointC.Run(Path.Combine(folder.FullName, "c"), LocalMind.AgentHub.Core.ProcessWorker.Locate()!);
}
finally { folder.Delete(true); }

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}");
}

static async Task Until(Func<bool> done)
{
    for (int i = 0; i < 100 && !done(); i++) await Task.Delay(30);
}

static void MakeFixture(string file) =>
    Sql(file, """
    CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT);
    CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT,title TEXT,status TEXT,created_at TEXT);
    CREATE TABLE hub_dependencies(task_id TEXT,requires_id TEXT);
    INSERT INTO projects VALUES ('a','Launch'),('b','Other'),('c','Quiet');
    INSERT INTO tasks VALUES ('r1','a','Review','open','1'),('r2','a','Review','cancelled','2'),
        ('s1','a','Approve revision','open','3'),('b1','b','Secret','open','1');
    INSERT INTO hub_dependencies VALUES ('s1','r1'),('s1','r2');
    """);

static void Sql(string file, string text)
{
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = text;
    cmd.ExecuteNonQuery();
}

// Commands, history, layout and scope. Fixture: Launch (blocked task, two "Review" prerequisites, one cancelled),
// Other (private task) and Quiet (no tasks).
static async Task Workbench(string dir)
{
    string file = Path.Combine(dir, "workbench.db");
    MakeFixture(file);

    var cold = new HubViewModel();
    Check(!cold.OpenCommand.CanExecute(null) && !cold.RefreshCommand.CanExecute(null) &&
          !cold.GoToBlockerCommand.CanExecute(null) && !cold.BackCommand.CanExecute(null) &&
          cold.ToggleProjectsCommand.CanExecute(null), "cold start: only layout commands available before a file is open");

    int picks = 0;
    string? choice = null;
    var hub = new HubViewModel { PickFile = () => { picks++; return Task.FromResult(choice); } };
    Check(hub.OpenCommand.CanExecute(null) && !hub.RefreshCommand.CanExecute(null), "open enabled, refresh disabled before a file");
    await hub.OpenCommand.RunAsync();
    Check(picks == 1 && hub.Projects.Count == 0, "cancelled picker opens nothing and reads once");
    choice = file;
    await hub.OpenCommand.RunAsync();
    Check(picks == 2 && hub.Projects.Count == 3 && hub.ProjectHeading == "Launch" && hub.RefreshCommand.CanExecute(null),
          "menu/shortcut command and picker open through one path");

    Check(!hub.GoToBlockerCommand.CanExecute(null), "blocker unavailable without a selected task");
    await hub.GoToBlockerCommand.RunAsync();
    Check(hub.SelectedTask == null && !hub.BackCommand.CanExecute(null), "disabled command changes nothing");

    var ship = hub.Tasks.Single(t => t.Id == "s1");
    hub.SelectedTask = ship;
    Check(hub.Tasks.Count(t => t.Title == "Review") == 2 && ship.Dependencies.Count == 2 &&
          ship.Dependencies.Select(d => d.Id).Distinct().Count() == 2, "duplicate titles stay distinct by ID");
    Check(ship.Status == "open" && ship.Dependencies.Single(d => d.Id == "r2").IsResolved == false,
          "recorded status shown as stored; cancelled is unresolved");
    Check(hub.GoToBlockerCommand.CanExecute(null), "blocker available for a selected blocked task");
    await hub.GoToBlockerCommand.RunAsync();
    Check(hub.SelectedTask?.Id == "r1" && hub.BackCommand.CanExecute(null), "blocker selects the first unresolved prerequisite");
    await hub.BackCommand.RunAsync();
    Check(hub.SelectedTask?.Id == "s1" && !hub.BackCommand.CanExecute(null), "back returns to the exact source task");

    var cancelled = ship.Dependencies.Single(d => d.Id == "r2");
    await hub.GoToBlockerCommand.RunAsync(cancelled);
    Check(hub.SelectedTask?.Id == "r2", "following a cancelled duplicate-title prerequisite lands on its own ID");
    await hub.BackCommand.RunAsync();
    Check(hub.SelectedTask?.Id == "s1", "back after the second duplicate");

    // Button path: the list passes the task itself, which need not be the selected one.
    hub.SelectedTask = hub.Tasks.Single(t => t.Id == "r1");
    await hub.GoToBlockerCommand.RunAsync(ship);
    Check(hub.SelectedTask?.Id == "r1" && hub.BackCommand.CanExecute(null), "list button navigates through the same command");
    await hub.BackCommand.RunAsync();
    Check(hub.SelectedTask?.Id == "s1", "button history records its source task");

    // Refresh keeps what still exists and drops what vanished.
    await hub.GoToBlockerCommand.RunAsync();
    Sql(file, "DELETE FROM hub_dependencies WHERE requires_id='r1'; DELETE FROM tasks WHERE id='r1'");
    await hub.RefreshCommand.RunAsync();
    Check(hub.SelectedTask == null && hub.BackCommand.CanExecute(null), "refresh clears a deleted target but keeps a surviving source");
    Sql(file, "DELETE FROM hub_dependencies WHERE task_id='s1'; DELETE FROM tasks WHERE id='s1'");
    await hub.RefreshCommand.RunAsync();
    Check(!hub.BackCommand.CanExecute(null) && !hub.GoToBlockerCommand.CanExecute(null), "refresh discards history IDs that disappeared");
    Sql(file, "INSERT INTO tasks VALUES ('s1','a','Approve revision','open','3'); INSERT INTO hub_dependencies VALUES ('s1','r2')");
    var hash = SHA256.HashData(File.ReadAllBytes(file));

    // Project isolation while a load is in flight.
    await hub.RefreshCommand.RunAsync();
    hub.SelectedTask = hub.Tasks.Single(t => t.Id == "s1");
    await hub.GoToBlockerCommand.RunAsync();
    Check(hub.BackCommand.CanExecute(null), "history exists in project A");
    hub.SelectedProject = hub.Projects.Single(p => p.Id == "b");
    Check(hub.SelectedTask == null && !hub.BackCommand.CanExecute(null) && hub.Tasks.Count == 0, "switching project clears task and history at once");
    await Until(() => hub.Tasks.Count > 0);
    Check(hub.Tasks.Single().Id == "b1" && hub.SelectedTask == null, "project B never shows A's task");
    hub.SelectedProject = hub.Projects.Single(p => p.Id == "c");
    await Until(() => hub.StatusText.StartsWith("No tasks"));
    Check(hub.StatusText == "No tasks in this project.", "empty project is distinct from a load failure");

    // Layout
    hub.SetWidth(1280);
    Check(!hub.Narrow && hub.InspectorVisible && hub.ListVisible && hub.InspectorColumn == 2, "wide: inspector beside the list");
    await hub.ToggleInspectorCommand.RunAsync();
    Check(!hub.InspectorVisible && hub.ListVisible, "hiding the inspector gives the list the space");
    await hub.ToggleInspectorCommand.RunAsync();
    hub.SetWidth(800);
    Check(hub.Narrow && !hub.InspectorVisible && hub.ListVisible, "narrow: inspector starts closed, list stays");
    await hub.ToggleInspectorCommand.RunAsync();
    Check(hub.InspectorVisible && !hub.ListVisible && hub.InspectorColumn == 1, "narrow: inspector replaces the list on request");
    await hub.ToggleInspectorCommand.RunAsync();
    await hub.ToggleProjectsCommand.RunAsync();
    Check(!hub.ProjectsVisible && hub.SelectedProject?.Id == "c", "collapsing projects keeps the selection");
    hub.SetWidth(1280);
    Check(hub.InspectorVisible && hub.ListVisible, "widening restores the inspector");

    // Failure
    string bad = Path.Combine(dir, "bad.db");
    File.WriteAllText(bad, "not a database");
    await hub.OpenAsync(bad);
    Check(hub.Tasks.Count == 0 && hub.SelectedTask == null && !hub.BackCommand.CanExecute(null) &&
          hub.StatusText.StartsWith("Could not open"), "unreadable file clears details and says so");
    Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "open, refresh, navigation and failure leave the database unchanged");
    string missing = Path.Combine(dir, "never.db");
    await hub.OpenAsync(missing);
    Check(!File.Exists(missing), "opening a missing file does not create it");
}

// Review fixes: late picker, overlapping refresh, platform shortcuts.
static async Task Regressions(string dir)
{
    var late = new HubViewModel();
    int changed = 0;
    late.OpenCommand.CanExecuteChanged += (_, _) => changed++;
    Check(!late.OpenCommand.CanExecute(null), "no picker yet: open disabled");
    late.PickFile = () => Task.FromResult<string?>(null);
    Check(changed == 1 && late.OpenCommand.CanExecute(null), "assigning the picker after binding re-enables open");

    string file = Path.Combine(dir, "regress.db");
    MakeFixture(file);
    Sql(file, "INSERT INTO tasks VALUES ('b2','b','Gate','open','2'); INSERT INTO hub_dependencies VALUES ('b2','b1')");
    var hub = new HubViewModel { PickFile = () => Task.FromResult<string?>(null) };
    await hub.OpenAsync(file);
    hub.SelectedProject = hub.Projects.Single(p => p.Id == "b");
    await Until(() => hub.Tasks.Count == 2);
    hub.SelectedTask = hub.Tasks.Single(t => t.Id == "b2");
    await hub.GoToBlockerCommand.RunAsync();
    await hub.OpenCommand.RunAsync();
    Check(hub.SelectedProject?.Id == "b" && hub.SelectedTask?.Id == "b1" && hub.BackCommand.CanExecute(null),
          "cancelled picker keeps project, task and history");

    var first = hub.RefreshCommand.RunAsync();
    var second = hub.RefreshCommand.RunAsync();
    await Task.WhenAll(first, second);
    Check(hub.SelectedProject?.Id == "b" && hub.SelectedTask?.Id == "b1", "overlapping refresh keeps the non-first project and task");
    await hub.BackCommand.RunAsync();
    Check(hub.SelectedTask?.Id == "b2", "overlapping refresh keeps Back history");

    var launch = hub.Projects.Single(p => p.Id == "a");
    var pending = Task.WhenAll(hub.RefreshCommand.RunAsync(), hub.RefreshCommand.RunAsync());
    hub.SelectedProject = launch;
    await pending;
    await Until(() => hub.Tasks.Count == 3);
    Check(hub.SelectedProject?.Id == "a" && hub.SelectedTask == null && !hub.BackCommand.CanExecute(null),
          "an intentional project switch wins over an overlapping refresh");
    Check(hub.Projects.Select(p => p.Id).Order().SequenceEqual(["a", "b", "c"]), "all projects stay listed after a switch during refresh");
    hub.SelectedProject = hub.Projects.Single(p => p.Id == "b");
    await Until(() => hub.Tasks.Any(t => t.Id == "b2"));
    Check(hub.SelectedProject?.Id == "b" && hub.Tasks.Count == 2, "can switch back to Other without refreshing again");

    string bad = Path.Combine(dir, "regress-bad.db");
    File.WriteAllText(bad, "not a database");
    await hub.OpenAsync(bad);
    Check(hub.Projects.Count == 0 && hub.SelectedProject == null && hub.StatusText.StartsWith("Could not open"), "unreadable database still clears the project list");
    await hub.OpenAsync(file);
    Check(hub.Projects.Count == 3, "opening a different database replaces the list");

    var mac = Shortcuts.For(true);
    var win = Shortcuts.For(false);
    Check(win["Open"].ToString() == "Ctrl+O" && win["Refresh"].ToString() == "F5", "Windows gestures: Ctrl+O and F5");
    Check(mac.Values.All(g => g.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta) && !g.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) &&
          mac["Open"].Key == Avalonia.Input.Key.O, "macOS gestures use Command, never Ctrl (definition only; not run on macOS)");
    Check(mac["Refresh"].Key == Avalonia.Input.Key.R && mac["Refresh"].KeyModifiers == Avalonia.Input.KeyModifiers.Meta, "macOS Refresh is Command+R");
}
