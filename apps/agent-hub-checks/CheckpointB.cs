using LocalMind.AgentHub;
using LocalMind.AgentHub.Controls;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 2 Checkpoint B: card content, blocked-move explanation, control view models, gallery isolation.</summary>
internal static class CheckpointB
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static void Sql(string file, string text)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = text;
        c.ExecuteNonQuery();
    }

    public const string LongQa = "QA the end-to-end onboarding flow across every supported provider, including the offline reviewer fixtures and the cancelled-prerequisite case";

    /// <summary>A real Agent Hub workspace with a blocked, long-titled QA task. Development fixture; also used by the native walkthrough.</summary>
    public static string MakeAtlas(string folder)
    {
        int n = 0;
        var info = ProjectStore.Create(folder, "Atlas", () => "atlas" + ++n);
        Sql(info.DatabasePath, $"""
            INSERT INTO tasks(id,project_id,title,status,created_at,outcome,owner) VALUES
              ('t-draft','{info.ProjectId}','Draft the launch note','open','1','A launch note under 300 words that the reviewer can approve.','Maya Chen'),
              ('t-draft2','{info.ProjectId}','Draft the launch note','cancelled','2',NULL,NULL),
              ('t-qa','{info.ProjectId}','{LongQa}','open','3','Confirm the whole flow works on a fresh profile.',NULL),
              ('t-ship','{info.ProjectId}','Ship the beta','done','4',NULL,'Sam Ortiz');
            INSERT INTO hub_dependencies VALUES ('t-qa','t-draft'),('t-qa','t-draft2');
            """);
        return info.DatabasePath;
    }

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        string db = MakeAtlas(Path.Combine(dir, "atlas"));
        var hub = new HubViewModel();
        await hub.OpenAsync(db, "Atlas");
        var qa = hub.Tasks.Single(t => t.Id == "t-qa");
        var draft = hub.Tasks.Single(t => t.Id == "t-draft");

        Check(qa.Title == LongQa && qa.HasOutcome && qa.OwnerLabel == "Unassigned" && qa.Status == "open" && qa.FirstBlockerId == "t-draft",
              "card reads outcome, owner (Unassigned), recorded state and first blocker");
        Check(draft.OwnerLabel == "Maya Chen" && draft.HasOutcome && !draft.HasBlocker, "a recorded owner and outcome are shown as stored");

        var move = qa.Start;
        Check(!move.Allowed && move.Blocker?.Id == "t-draft" && move.Reason.Contains("Draft the launch note") && move.Reason.Contains("plus 1 more"),
              "blocked move names the prerequisite and the others");
        Check(qa.MoveNote == move.Reason && hub.Tasks.Single(t => t.Id == "t-draft").MoveNote == "No prerequisites",
              "card and inspector read the same move result");
        Check(MoveEligibility.Evaluate(qa, "cancelled").Allowed && MoveEligibility.Evaluate(qa, "open").Allowed, "moves that need no prerequisite stay allowed");

        var cancelledOnly = new TaskItem("x", "QA", "open", [new("d", "Draft", "cancelled")]);
        var doneOnly = new TaskItem("y", "QA", "open", [new("d", "Draft", "done")]);
        Check(!cancelledOnly.Start.Allowed && doneOnly.Start.Allowed && doneOnly.MoveNote == "All prerequisites complete",
              "a completed dependency clears the reason; a cancelled one stays unresolved");

        // Keyboard path: select QA, follow the blocker, Back restores the same card.
        hub.SelectedTask = qa;
        await hub.GoToBlockerCommand.RunAsync();
        Check(hub.SelectedTask?.Id == "t-draft", "following the blocker selects the prerequisite by ID");
        await hub.BackCommand.RunAsync();
        Check(ReferenceEquals(hub.SelectedTask, qa), "Back restores the same QA card");

        // Same titles in two projects never cross.
        string twin = Path.Combine(dir, "twin.db");
        Sql(twin, """
            CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT);
            CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT, title TEXT, status TEXT, created_at TEXT);
            CREATE TABLE hub_dependencies(task_id TEXT, requires_id TEXT);
            INSERT INTO projects VALUES ('p1','One'),('p2','Two');
            INSERT INTO tasks VALUES ('p1d','p1','Draft','open','1'),('p1q','p1','QA','open','2'),('p2d','p2','Draft','done','1'),('p2q','p2','QA','open','2');
            INSERT INTO hub_dependencies VALUES ('p1q','p1d'),('p2q','p2d');
            """);
        var twins = new HubViewModel();
        await twins.OpenAsync(twin);
        var q1 = twins.Tasks.Single(t => t.Id == "p1q");
        twins.SelectedProject = twins.Projects.Single(p => p.Id == "p2");
        await Wait(() => twins.Tasks.Any(t => t.Id == "p2q"));
        var q2 = twins.Tasks.Single(t => t.Id == "p2q");
        Check(q1.FirstBlockerId == "p1d" && q2.Start.Allowed && twins.SelectedTask == null, "identical titles in different projects stay separate");

        // Legacy files carry no owner or outcome.
        string legacy = Path.Combine(dir, "legacy.db");
        Sql(legacy, "CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT); CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT, title TEXT, status TEXT, created_at TEXT); INSERT INTO projects VALUES ('a','A'); INSERT INTO tasks VALUES ('a1','a','T','open','1')");
        var old = new HubViewModel();
        await old.OpenAsync(legacy);
        Check(old.Tasks.Single() is { OwnerLabel: "Unassigned", HasOutcome: false }, "a legacy file shows Unassigned and no outcome");

        // Agent identity: distinct without color or logos.
        AgentIdentity[] agents =
        [
            new("Builder North", "Builder", "Anthropic · claude-sonnet", true), new("Reviewer South", "Reviewer", "Anthropic · claude-sonnet", true),
            new("Workshop box", "Builder", "Local · http://localhost:11434", true), new("Release helper", "Coordinator", null, true),
        ];
        Check(agents.Select(a => a.AccessibleName).Distinct().Count() == 4 && agents.All(a => a.AccessibleName.Contains("fixture identity")),
              "agent identities have distinct accessible names and say they are fixtures");
        Check(agents[3].AccessibleName.Contains("no provider recorded") && !agents[3].HasProvider && agents[3].Initials == "RH",
              "a custom agent without a provider or icon is not given a guessed one");
        Check(new AgentIdentity("Ada", null, null).AccessibleName == "Ada, no provider recorded", "missing role and provider are omitted, not invented");

        // Progress never invents a percentage.
        var unknown = new ProgressRowViewModel("Review", null, "Running");
        Check(unknown.Indeterminate && !unknown.AccessibleName.Contains("percent") && !new ProgressRowViewModel("QA", null, "Not started").Indeterminate
              && new ProgressRowViewModel("Draft", 0.4, "40% complete").AccessibleName.Contains("40 percent"), "progress row shows a percentage only when one is reported");

        // Decision control states.
        var decision = new DecisionViewModel { Question = "Accept?", Subject = "revision 2" };
        Check(decision.AcceptCommand.CanExecute(null), "decision is available when enabled");
        decision.Busy = true;
        Check(!decision.AcceptCommand.CanExecute(null) && !decision.RequestChangesCommand.CanExecute(null), "decision is disabled while busy");
        decision.Busy = false;
        decision.Enabled = false;
        await decision.AcceptCommand.RunAsync();
        Check(!decision.HasOutcome, "a disabled decision records nothing");
        decision.Enabled = true;
        decision.Error = "Not recorded. Try again.";
        Check(decision.HasError && decision.AcceptCommand.CanExecute(null), "a decision error leaves a working next action");

        // Gallery data stays out of the production view model and window.
        bool leaks = new[] { typeof(HubViewModel), typeof(MainWindow) }.SelectMany(t => t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            .Select(m => m switch { System.Reflection.FieldInfo f => f.FieldType, System.Reflection.PropertyInfo p => p.PropertyType, System.Reflection.MethodInfo mi => mi.ReturnType, _ => typeof(object) })
            .Any(t => t.Namespace?.EndsWith(".Gallery") == true);
        var empty = new HubViewModel();
        Check(!leaks && empty.Projects.Count == 0 && empty.Tasks.Count == 0 && empty.ModeLabel == "", "gallery types and data are not reachable from the ordinary app on an empty profile");
    }

    private static async Task Wait(Func<bool> done)
    {
        for (int i = 0; i < 100 && !done(); i++) await Task.Delay(30);
    }
}
