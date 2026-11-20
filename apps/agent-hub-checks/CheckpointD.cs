using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 3 Checkpoint A: runs, review policy, exact-revision decisions, single writer.</summary>
internal static class CheckpointD
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static SqliteConnection Db(string file, bool ro = true) =>
        new(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, Mode = ro ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite }.ToString());

    private static T One<T>(string file, string sql)
    {
        using var db = Db(file);
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar() is { } v and not DBNull ? (T)Convert.ChangeType(v, typeof(T)) : default!;
    }

    private static void Sql(string file, string sql)
    {
        using var db = Db(file, false);
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static WorkerError Fault(Action act)
    {
        try { act(); }
        catch (Exception e) when (e.GetType().Name == "StoreException") { return (WorkerError)e.GetType().GetProperty("Error")!.GetValue(e)!; }
        throw new InvalidOperationException("expected a store error");
    }

    private static Func<string> Ids(string p) { int n = 0; return () => p + ++n; }

    /// <summary>Atlas with a review-gated Draft, an ordinary Note, and QA waiting on Draft.</summary>
    private static (string Db, string Project, string Draft, string Qa, string Note, Func<string> Id) Atlas(string dir)
    {
        var id = Ids("x");
        var info = ProjectStore.Create(dir, "Atlas", id);
        string d = TaskStore.CreateTask(info.DatabasePath, "t1", info.ProjectId, "Draft", "A launch note.", id).Id;
        string q = TaskStore.CreateTask(info.DatabasePath, "t2", info.ProjectId, "QA", null, id).Id;
        string n = TaskStore.CreateTask(info.DatabasePath, "t3", info.ProjectId, "Note", null, id).Id;
        TaskStore.AddDependency(info.DatabasePath, "t4", q, d, 0);
        TaskStore.UpdateTask(info.DatabasePath, "t5", d, 0, null, null, null, reviewRequired: true);
        return (info.DatabasePath, info.ProjectId, d, q, n, id);
    }

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Migration(Path.Combine(dir, "migrate"));
        Review(Path.Combine(dir, "review"));
        await LostAck(Path.Combine(dir, "lost"));
        Writer(Path.Combine(dir, "writer"));
        await Journey(Path.Combine(dir, "journey"));
    }

    // Stored v4 layout, migrated to v5 -----------------------------------------------------------------------
    private static void Migration(string dir)
    {
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "agenthub.db");
        Sql2(db, """
            CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, title TEXT NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0, outcome TEXT, owner TEXT);
            CREATE TABLE hub_dependencies(task_id TEXT NOT NULL, requires_id TEXT NOT NULL, PRIMARY KEY(task_id, requires_id));
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE workspace(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE events(seq INTEGER PRIMARY KEY AUTOINCREMENT, command_id TEXT NOT NULL UNIQUE, task_id TEXT NOT NULL, kind TEXT NOT NULL,
                from_state TEXT, to_state TEXT, detail TEXT, created_at TEXT NOT NULL);
            CREATE TABLE outputs(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, revision INTEGER NOT NULL, text TEXT NOT NULL, author_role TEXT NOT NULL,
                accepted_by TEXT, created_at TEXT NOT NULL, UNIQUE(task_id, revision));
            CREATE TABLE reviews(id TEXT PRIMARY KEY, output_id TEXT NOT NULL, recommendation TEXT NOT NULL, comment TEXT NOT NULL, reviewer_role TEXT NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO meta VALUES('schema_version','4');
            INSERT INTO workspace VALUES('w','Atlas','1');
            INSERT INTO projects VALUES('p','Atlas');
            INSERT INTO tasks VALUES('t','p','Draft','in_progress','1',2,NULL,'Maya');
            INSERT INTO events(command_id,task_id,kind,created_at,detail) VALUES('c1','t','task_created','1','{}');
            INSERT INTO outputs VALUES('o1','t',1,'Draft 1','builder (fixture)',NULL,'2');
            INSERT INTO reviews VALUES('r1','o1','request_changes','Add a summary.','reviewer (fixture)','3');
            """);
        string before = One<string>(db, "SELECT group_concat(id||':'||revision, '|') FROM outputs");
        var info = ProjectStore.Load(db);
        Check(info.Migrated && info.SchemaVersion == ProjectStore.CurrentSchema && One<string>(db, "SELECT group_concat(id||':'||revision, '|') FROM outputs") == before
              && One<long>(db, "SELECT count(*) FROM events") == 1 && One<string>(db, "SELECT owner FROM tasks") == "Maya", "a schema-v4 store migrates to v5 and keeps its IDs, events and owner");
        Check(One<string>(db, "SELECT run_id FROM outputs") == "run-o1" && One<string>(db, "SELECT run_id FROM reviews") == "run-r1" && One<long>(db, "SELECT count(*) FROM runs") == 2,
              "migrated outputs and reviews gain a run each");
        ProjectStore.Load(db);
        Check(One<string>(db, "SELECT group_concat(id, ',') FROM runs") == "run-o1,run-r1" || One<string>(db, "SELECT group_concat(id, ',') FROM runs") == "run-r1,run-o1", "run IDs are the same after reopening");
        Check(One<string>(db, "SELECT name||revision FROM policy") == "human1", "the policy defaults to human acceptance, revision 1");
    }

    private static void Sql2(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    // Policy, decisions, gating -------------------------------------------------------------------------------
    private static void Review(string dir)
    {
        var (db, project, draft, qa, note, id) = Atlas(dir);
        Check(TaskStore.MoveTask(db, "n1", note, "in_progress", 0).Revision == 1 && TaskStore.MoveTask(db, "n2", note, "done", 1).Revision == 2,
              "an ordinary task still completes without any output");

        // Runs have identities that survive reopening.
        var b1 = TaskStore.RunRole(db, "b1", draft, "builder", null, id);
        string runB1 = One<string>(db, "SELECT run_id FROM outputs WHERE revision=1");
        TaskStore.RunRole(db, "r1", draft, "reviewer", null, id);
        string runR1 = One<string>(db, "SELECT run_id FROM reviews");
        var b2 = TaskStore.RunRole(db, "b2", draft, "builder", null, id);
        var r2 = TaskStore.RunRole(db, "r2", draft, "reviewer", null, id);
        Check(runB1 != null && runR1 != null && runB1 != runR1 && One<long>(db, "SELECT count(*) FROM runs") == 4 && One<string>(db, "SELECT run_id FROM outputs WHERE revision=1") == runB1,
              "each builder and reviewer run keeps its own identity after reopening");

        // Policy authority.
        string draftOut2 = b2.Id, draftOut1 = b1.Id;
        Check(One<string>(db, "SELECT name||revision FROM policy") == "human1", "a new workspace starts under human acceptance");
        var denied = Fault(() => TaskStore.SetPolicy(db, "p0", "reviewer", 1, "builder (fixture)"));
        Check(denied.Code == "not_authorized" && One<string>(db, "SELECT name||revision FROM policy") == "human1", "a fixture role cannot change the policy");
        var open = Fault(() => TaskStore.SetPolicy(db, "p1", "reviewer", 1, "owner"));
        Check(open.Code == "decision_open" && One<string>(db, "SELECT name||revision FROM policy") == "human1", "the policy cannot change while a decision is open");

        // Gating through every move path.
        TaskStore.MoveTask(db, "g0", draft, "in_progress", One<int>(db, $"SELECT revision FROM tasks WHERE id='{draft}'"));
        var early = Fault(() => TaskStore.MoveTask(db, "g1", draft, "done", One<int>(db, $"SELECT revision FROM tasks WHERE id='{draft}'")));
        var qaMove = Fault(() => TaskStore.MoveTask(db, "g2", qa, "in_progress", One<int>(db, $"SELECT revision FROM tasks WHERE id='{qa}'")));
        Check(early.Code == "review_required" && qaMove.Code == "blocked" && One<string>(db, $"SELECT status FROM tasks WHERE id='{draft}'") == "in_progress",
              "a review-gated task cannot be moved to Done before acceptance, and QA stays blocked");

        // Authority and exactness of the decision.
        string Draft(string name) => One<string>(db, $"SELECT status FROM tasks WHERE id='{draft}'");
        Check(Fault(() => TaskStore.DecideOutput(db, "d1", draft, draftOut2, 2, 1, "accepted", null, "builder (fixture)", id)).Message.Contains("author"), "self-review is refused");
        Check(Fault(() => TaskStore.DecideOutput(db, "d2", draft, draftOut2, 2, 1, "accepted", null, "reviewer (fixture)", id)).Code == "not_authorized", "under human acceptance an agent reviewer cannot decide");
        var stale = Fault(() => TaskStore.DecideOutput(db, "d3", draft, draftOut1, 1, 1, "accepted", null, "owner", id));
        Check(stale.Code == "stale_revision" && stale.Message.Contains("revision 2") && Draft("x") == "in_progress" && One<long>(db, "SELECT count(*) FROM decisions") == 0,
              "accepting stale revision 1 after revision 2 arrived is refused and changes nothing");
        Check(Fault(() => TaskStore.DecideOutput(db, "d4", draft, draftOut2, 2, 9, "accepted", null, "owner", id)).Code == "policy_changed", "a decision made under an old policy revision is refused");

        // The transaction: decision, event and effect together.
        string beforeAccept = One<string>(db, "SELECT group_concat(status||accepted_by, '|') FROM tasks t LEFT JOIN outputs o ON o.task_id=t.id");
        TaskStore.BeforeEventInsert = () => throw new IOException("cut");
        try { TaskStore.DecideOutput(db, "d5", draft, draftOut2, 2, 1, "accepted", null, "owner", id); } catch (IOException) { } finally { TaskStore.BeforeEventInsert = null; }
        Check(One<long>(db, "SELECT count(*) FROM decisions") == 0 && One<string>(db, "SELECT group_concat(status||accepted_by, '|') FROM tasks t LEFT JOIN outputs o ON o.task_id=t.id") == beforeAccept,
              "an interruption before the event rolls back the decision and its effect");
        var accepted = TaskStore.DecideOutput(db, "d5", draft, draftOut2, 2, 1, "accepted", null, "owner", id);
        Check(!accepted.Duplicate && One<string>(db, $"SELECT status FROM tasks WHERE id='{draft}'") == "done" && One<string>(db, "SELECT accepted_by FROM outputs WHERE revision=2") == "owner"
              && One<string>(db, "SELECT kind||':'||from_state||'>'||to_state FROM events WHERE command_id='d5'") == "output_accepted:in_progress>done"
              && One<string>(db, "SELECT output_id||'/'||content_revision||'/'||policy_revision||'/'||actor||'/'||run_id||'/'||submission_id FROM decisions") == $"{draftOut2}/2/1/owner/{One<string>(db, "SELECT run_id FROM outputs WHERE revision=2")}/d5",
              "accepting revision 2 records the decision, its event and Done in one transaction");
        Check(TaskStore.DecideOutput(db, "d5", draft, draftOut2, 2, 1, "accepted", null, "owner", id).Duplicate && One<long>(db, "SELECT count(*) FROM decisions") == 1, "the same submission is not decided twice");
        Check(Fault(() => TaskStore.DecideOutput(db, "d6", draft, draftOut2, 2, 1, "accepted", null, "owner", id)).Code == "already_decided", "a second submission for an accepted revision is refused");
        Check(TaskStore.MoveTask(db, "g3", qa, "in_progress", One<int>(db, $"SELECT revision FROM tasks WHERE id='{qa}'")).Revision > 0, "QA can start once the output is accepted");

        // Request changes, then the builder revises with that comment.
        var (db2, _, draft2, _, _, id2) = Atlas(Path.Combine(dir, "rework"));
        var o1 = TaskStore.RunRole(db2, "b1", draft2, "builder", null, id2);
        TaskStore.DecideOutput(db2, "rc", draft2, o1.Id, 1, 1, "changes_requested", "Shorter, please.", "owner", id2);
        var o2 = TaskStore.RunRole(db2, "b2", draft2, "builder", null, id2);
        Check(o2.Revision == 2 && o2.Detail!.Contains("Correction applied: Shorter, please.") && One<string>(db2, "SELECT status FROM tasks WHERE id='" + draft2 + "'") == "open",
              "request changes leaves the task open and the next revision answers the comment");

        // Reviewer-approval policy: an agent reviewer may accept only after recommending approval.
        var (db3, _, draft3, _, _, id3) = Atlas(Path.Combine(dir, "reviewer-policy"));
        Check(TaskStore.SetPolicy(db3, "sp", "reviewer", 1, "owner").Revision == 2, "the owner can choose reviewer approval");
        var v1 = TaskStore.RunRole(db3, "b1", draft3, "builder", null, id3);
        TaskStore.RunRole(db3, "r1", draft3, "reviewer", null, id3);
        Check(Fault(() => TaskStore.DecideOutput(db3, "x1", draft3, v1.Id, 1, 2, "accepted", null, "reviewer (fixture)", id3)).Code == "not_authorized", "a reviewer cannot accept a draft it asked to change");
        var v2 = TaskStore.RunRole(db3, "b2", draft3, "builder", null, id3);
        TaskStore.RunRole(db3, "r2", draft3, "reviewer", null, id3);
        Check(TaskStore.DecideOutput(db3, "x2", draft3, v2.Id, 2, 2, "accepted", null, "reviewer (fixture)", id3).Revision > 0
              && One<string>(db3, "SELECT accepted_by FROM outputs WHERE revision=2") == "reviewer (fixture)", "under reviewer approval the reviewer's approval counts");
    }

    // A lost acknowledgement at the decision boundary ---------------------------------------------------------
    private static async Task LostAck(string dir)
    {
        var (db, _, draft, _, _, id) = Atlas(dir);
        TaskStore.RunRole(db, "b1", draft, "builder", null, id);
        TaskStore.RunRole(db, "r1", draft, "reviewer", null, id);
        var o2 = TaskStore.RunRole(db, "b2", draft, "builder", null, id);
        var fake = new FakeWorker();
        var client = new WorkerClient(fake);
        fake.AfterHandle = req => req.Op == "decideOutput" ? throw new WorkerException(new("outcome_unknown", "The worker stopped while handling “decideOutput”.", "Repeat the action.")) : Task.CompletedTask;
        try { await client.DecideOutputAsync(db, "sub-1", draft, o2.Id, 2, 1, "accepted", null); Check(false, "the answer should be lost"); }
        catch (WorkerException e) { Check(e.Error.Code == "outcome_unknown", "the decision committed but its acknowledgement was lost"); }
        fake.AfterHandle = null;
        var status = await client.CommandStatusAsync(db, "sub-1");
        Check(status is { Revision: > 0 }, "the client can ask what became of the submission");
        var again = await client.DecideOutputAsync(db, "sub-1", draft, o2.Id, 2, 1, "accepted", null);
        Check(again.Duplicate && One<long>(db, "SELECT count(*) FROM decisions") == 1 && One<long>(db, "SELECT count(*) FROM events WHERE kind='output_accepted'") == 1,
              "retrying the same submission gives one decision and one event");
    }

    // One writer per workspace --------------------------------------------------------------------------------
    private static void Writer(string dir)
    {
        Directory.CreateDirectory(dir);
        var first = WorkspaceLock.TryAcquire(Path.Combine(dir, "a"));
        var second = WorkspaceLock.TryAcquire(Path.Combine(dir, "a"));
        var other = WorkspaceLock.TryAcquire(Path.Combine(dir, "b"));
        Check(first.Held && !second.Held && second.OwnerProcessId == Environment.ProcessId && other.Held, "a second open of the same workspace is refused and names the owner; another workspace is unaffected");
        first.Dispose();
        var third = WorkspaceLock.TryAcquire(Path.Combine(dir, "a"));
        Check(third.Held, "the writer role transfers once the owner lets go");
        third.Dispose();
        other.Dispose();
    }

    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 150 && !done(); i++) await Task.Delay(30);
    }

    // The offline review journey through the view model, the same commands the buttons, menu and shortcuts run ----------
    private static async Task Journey(string dir)
    {
        var fake = new FakeWorker();
        var hub = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(new WorkerClient(fake)), PickFile = () => Task.FromResult<string?>(null) };
        string folder = Path.Combine(dir, "atlas");
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        await hub.CreateTaskAsync("Draft", "A launch note.");
        await hub.CreateTaskAsync("QA", "");
        var draft = hub.Tasks.Single(t => t.Title == "Draft");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.AddPrerequisiteAsync(draft.Id);
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Draft");
        await hub.ToggleReviewCommand.RunAsync();
        await Until(() => hub.Review != null && hub.PolicyLine.Contains("human acceptance"));
        Check(hub.SelectedTask!.ReviewRequired && hub.PolicyLine.Contains("only you decide") && hub.StateLine.StartsWith("No draft yet"), "Draft is review-gated under human acceptance and says so");

        async Task Step(LocalMind.AgentHub.WorkspaceCommand c, Func<bool> after) { await c.RunAsync(); await Until(after); }
        await Step(hub.AskBuilderCommand, () => hub.HasOutput);
        await Step(hub.AskReviewerCommand, () => hub.HasFeedback);
        Check(hub.OutputHeading.StartsWith("Revision 1 · builder (fixture)") && hub.FeedbackText.Contains("recommends request changes") && hub.FeedbackText.Contains("not a decision") && hub.StateLine == "Awaiting your review · revision 1",
              "the owner can read the draft, the author label and the reviewer's reason");
        await Step(hub.AskBuilderCommand, () => hub.Review?.Revision == 2);
        await Step(hub.AskReviewerCommand, () => hub.FeedbackText.Contains("approve recommended"));
        Check(hub.Review!.Revision == 2 && hub.OutputText.Contains("Summary:") && hub.StateLine == "Awaiting your review · revision 2" && hub.CanDecide, "revision 2 is waiting for the owner");

        // QA is blocked, and Draft cannot be completed by a shortcut, before the decision.
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.MoveTaskCommand.RunAsync("in_progress");
        Check(hub.Notice is { } n1 && n1.Contains("awaiting your review") && hub.Tasks.Single(t => t.Title == "QA").Status == "open", "QA is blocked and the reason says Draft awaits review");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Draft");
        await Until(() => hub.CanDecide);
        await hub.MoveTaskCommand.RunAsync("done");
        Check(hub.Notice is { } n2 && n2.Contains("accepted output") && hub.Tasks.Single(t => t.Title == "Draft").Status != "done", "Done on the gated task is refused through the move command");

        // A newer revision arrives from another client: the stale approval is refused.
        TaskStore.RunRole(db, "other-b3", hub.SelectedTask!.Id, "builder", null, () => Guid.NewGuid().ToString("N")); // another writer, bypassing this window
        await hub.AcceptOutputCommand.RunAsync();
        Check(hub.Notice is { } n3 && n3.Contains("no longer the current draft") && One<long>(db, "SELECT count(*) FROM decisions") == 0 && hub.Tasks.Single(t => t.Title == "Draft").Status != "done",
              "accepting revision 2 after revision 3 arrived is refused and nothing changes");
        await hub.NoticeCommand!.RunAsync();
        await Until(() => hub.Review?.LatestRevision == 3);
        Check(hub.Review is { Revision: 2, IsStale: true } && !hub.CanDecide, "after reloading, revision 2 stays open, marked stale, until the user opens revision 3");
        await hub.OpenLatestRevisionCommand.RunAsync();
        await Until(() => hub.Review?.Revision == 3);

        // A lost acknowledgement at the decision, repeated from the same screen.
        fake.AfterHandle = req => req.Op == "decideOutput" ? throw new WorkerException(new("outcome_unknown", "The worker stopped while handling “decideOutput”, so it may or may not have been applied.", "Repeat the action.")) : Task.CompletedTask;
        await hub.AcceptOutputCommand.RunAsync();
        fake.AfterHandle = null;
        Check(hub.Notice is { } n4 && n4.Contains("may or may not") && One<long>(db, "SELECT count(*) FROM decisions") == 1, "the decision committed although its answer was lost");
        await hub.NoticeCommand!.RunAsync();
        await Until(() => hub.Tasks.Single(t => t.Title == "Draft").Status == "done" && hub.StateLine.StartsWith("Accepted"));
        Check(One<long>(db, "SELECT count(*) FROM decisions") == 1 && One<long>(db, "SELECT count(*) FROM events WHERE kind='output_accepted'") == 1 && hub.StateLine.StartsWith("Accepted by owner · revision 3")
              && hub.Notice == null, "repeating it gives one decision and shows the accepted revision");

        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.MoveTaskCommand.RunAsync("in_progress");
        Check(hub.Notice == null && hub.Tasks.Single(t => t.Title == "QA").Status == "in progress", "QA can start after the decision");

        // Restart: a fresh view model on the same folder shows the same state.
        await hub.ReleaseWriteLockAsync();
        var again = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(new WorkerClient(new FakeWorker())) };
        await again.OpenWorkspaceFolderAsync(folder);
        again.SelectedTask = again.Tasks.Single(t => t.Title == "Draft");
        await Until(() => again.HasOutput);
        Check(again.StateLine.StartsWith("Accepted by owner · revision 3") && again.Tasks.Single(t => t.Title == "QA").Status == "in progress" && again.OutputHeading.Contains("run "),
              "after a restart the accepted revision, its run and QA's state are the same");

        // Second window: read-only until the first lets go; another workspace stays writable.
        var second = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(new WorkerClient(new FakeWorker())), PickFile = () => Task.FromResult<string?>(null) };
        await second.OpenWorkspaceFolderAsync(folder);
        Check(second.ReadOnlyReason is { } why && why.Contains($"process {Environment.ProcessId}") && !second.CanWrite && !second.NewTaskCommand.CanExecute(null) && second.ModeLabel.Contains("read-only"),
              "a second window on the same workspace is read-only and names the owner");
        var third = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(new WorkerClient(new FakeWorker())), PickFile = () => Task.FromResult<string?>(null) };
        Check(await third.CreateWorkspaceAsync("Other", Path.Combine(dir, "other")) is null && third.CanWrite, "a second independent workspace is still writable");
        await again.ReleaseWriteLockAsync();
        await second.RefreshCommand.RunAsync();
        Check(second.CanWrite && second.ReadOnlyReason == null, "the second window can write once the owner lets go");
        await second.ReleaseWriteLockAsync();
        await third.ReleaseWriteLockAsync();
    }
}
