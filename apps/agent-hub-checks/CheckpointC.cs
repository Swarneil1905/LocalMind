using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 2 Checkpoint C: durable transitions (AH07), task and dependency edits (AH08), offline roles (AH63).</summary>
internal static class CheckpointC
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static SqliteConnection Db(string file) =>
        new(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());

    private static T One<T>(string file, string sql)
    {
        using var db = Db(file);
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar() is { } v and not DBNull ? (T)Convert.ChangeType(v, typeof(T)) : default!;
    }

    /// <summary>Projects, tasks (status, revision, owner) and edges, as text, read fresh from disk.</summary>
    private static string Graph(string file) => One<string>(file, """
        SELECT group_concat(x, '|') FROM (
          SELECT 't:'||id||'/'||title||'/'||status||'/'||revision||'/'||ifnull(owner,'') AS x FROM tasks
          UNION ALL SELECT 'e:'||task_id||'>'||requires_id FROM hub_dependencies ORDER BY 1)
        """) ?? "";

    private static StoreFault Expect(Action act)
    {
        try { act(); }
        catch (Exception e) when (e.GetType().Name == "StoreException")
        {
            var err = (WorkerError)e.GetType().GetProperty("Error")!.GetValue(e)!;
            return new(err.Code, err.Message, err.Recovery);
        }
        throw new InvalidOperationException("expected a store error");
    }

    private static StoreFault ExpectWorker(Action act)
    {
        try { act(); }
        catch (WorkerException e) { return new(e.Error.Code, e.Error.Message, e.Error.Recovery); }
        throw new InvalidOperationException("expected a worker error");
    }

    private sealed record StoreFault(string Code, string Message, string Recovery);

    private static Func<string> Ids(string prefix)
    {
        int n = 0;
        return () => prefix + ++n;
    }

    private static (string Db, string Project, Func<string> Id) Workspace(string dir, string name)
    {
        var id = Ids(name[..1].ToLower());
        var info = ProjectStore.Create(dir, name, id);
        return (info.DatabasePath, info.ProjectId, id);
    }

    public static async Task Run(string dir, string workerExe)
    {
        Directory.CreateDirectory(dir);
        Transitions(Path.Combine(dir, "ah07"));
        Edits(Path.Combine(dir, "ah08"));
        await Roles(Path.Combine(dir, "ah63"), workerExe);
        await Board(Path.Combine(dir, "ah09"));
        await LostAcknowledgement(Path.Combine(dir, "lost-ack"));
    }

    // A save that commits but whose answer is lost, driven through the same method the dialogs call. ------------
    private static async Task LostAcknowledgement(string dir)
    {
        static WorkerException Lost() => new(new("outcome_unknown", "The worker stopped while handling “createTask”, so it may or may not have been applied.", "Check the workspace, then repeat the action."));
        async Task<(LocalMind.AgentHub.HubViewModel Hub, FakeWorker Fake, string Db)> Start(string name)
        {
            var fake = new FakeWorker();
            var hub = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(new WorkerClient(fake)), PickFile = () => Task.FromResult<string?>(null) };
            string folder = Path.Combine(dir, name);
            await hub.CreateWorkspaceAsync("Atlas", folder);
            return (hub, fake, Path.Combine(folder, "agenthub.db"));
        }

        // Same input again: one task, one event, the original ID.
        var a = await Start("same");
        a.Fake.AfterHandle = req => req.Op == "createTask" ? throw Lost() : Task.CompletedTask;
        string? lost = await a.Hub.CreateTaskAsync("Draft", "A note.");
        a.Fake.AfterHandle = null;
        string originalId = One<string>(a.Db, "SELECT id FROM tasks");
        Check(lost != null && lost.Contains("may or may not") && One<long>(a.Db, "SELECT count(*) FROM tasks") == 1, "the save committed even though its answer was lost");
        Check(await a.Hub.CreateTaskAsync("Draft", "A note.") is null && One<long>(a.Db, "SELECT count(*) FROM tasks") == 1
              && One<long>(a.Db, "SELECT count(*) FROM events WHERE kind='task_created'") == 1 && a.Hub.SelectedTask?.Id == originalId && a.Hub.OpenTasks.Count == 1,
              "resubmitting identical input gives one task, one event and the original task ID");

        // Changed input: the worker is asked what happened first, so the first task is neither lost nor duplicated.
        var b = await Start("changed");
        b.Fake.AfterHandle = req => req.Op == "createTask" ? throw Lost() : Task.CompletedTask;
        await b.Hub.CreateTaskAsync("Draft", "");
        b.Fake.AfterHandle = null;
        Check(await b.Hub.CreateTaskAsync("QA", "") is null && One<long>(b.Db, "SELECT count(*) FROM tasks WHERE title='Draft'") == 1
              && One<long>(b.Db, "SELECT count(*) FROM tasks") == 2 && b.Hub.OpenTasks.Count == 2, "a changed request after a lost answer keeps the first task and adds the new one once");

        // Lost before it committed: a changed request just goes ahead.
        var c = await Start("never");
        c.Fake.BeforeHandle = req => req.Op == "createTask" ? throw Lost() : Task.CompletedTask;
        await c.Hub.CreateTaskAsync("Draft", "");
        c.Fake.BeforeHandle = null;
        Check(await c.Hub.CreateTaskAsync("QA", "") is null && One<long>(c.Db, "SELECT count(*) FROM tasks") == 1 && c.Hub.OpenTasks.Single().Title == "QA",
              "a lost request that never committed leaves nothing behind");

        // The same applies to edits: a retried assignment reuses its command ID and does not conflict with itself.
        var d = await Start("assign");
        await d.Hub.CreateTaskAsync("Draft", "");
        d.Fake.AfterHandle = req => req.Op == "updateTask" ? throw Lost() : Task.CompletedTask;
        await d.Hub.AssignAsync("Reviewer");
        d.Fake.AfterHandle = null;
        Check(await d.Hub.AssignAsync("Reviewer") is null && d.Hub.SelectedTask!.OwnerLabel == "Reviewer" && One<long>(d.Db, "SELECT count(*) FROM events WHERE kind='task_updated'") == 1,
              "a retried assignment applies once");
    }

    // AH09 ------------------------------------------------------------------------------------------------
    private static async Task Board(string dir)
    {
        var client = new WorkerClient(new FakeWorker());
        var hub = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(client), PickFile = () => Task.FromResult<string?>(null) };
        string folder = Path.Combine(dir, "atlas");
        Check(await hub.CreateWorkspaceAsync("Atlas", folder) is null && hub.IsWorkspace && hub.OpenTasks.Count == 0, "a new workspace opens with an empty board");
        Check(await hub.CreateTaskAsync("Draft", "A launch note.") is null && await hub.CreateTaskAsync("QA", "") is null, "tasks can be created from the board");
        Check(hub.OpenTasks.Count == 2 && hub.SelectedTask?.Title == "QA" && hub.OpenHeader == "Open · 2", "new tasks land in Open and the newest is selected");
        var draft = hub.Tasks.Single(t => t.Title == "Draft");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        Check(await hub.AddPrerequisiteAsync(draft.Id) is null && hub.SelectedTask!.HasBlocker && hub.SelectedTask.Dependencies.Single().Id == draft.Id, "QA can be made to depend on Draft");
        Check(await hub.AssignAsync("Reviewer") is null && hub.SelectedTask!.OwnerLabel == "Reviewer", "a role can be assigned");

        string qaId = hub.SelectedTask!.Id;
        await hub.MoveTaskCommand.RunAsync("in_progress");
        Check(hub.Notice is { } n && n.Contains("Draft") && n.Contains("stays in Open") && hub.NoticeActionLabel == "Go to blocker" && hub.OpenTasks.Any(t => t.Id == qaId) && hub.ActiveTasks.Count == 0,
              "a blocked move shows the reason, keeps the card in its stored column and offers Go to blocker");
        await hub.NoticeCommand!.RunAsync();
        Check(hub.SelectedTask?.Id == draft.Id && hub.BackCommand.CanExecute(null), "the notice action follows the blocker");
        await hub.BackCommand.RunAsync();
        Check(hub.SelectedTask?.Id == qaId, "Back returns to the QA card");

        hub.SelectedTask = hub.Tasks.Single(t => t.Id == draft.Id);
        await hub.MoveTaskCommand.RunAsync("in_progress");
        await hub.MoveTaskCommand.RunAsync("done");
        Check(hub.DoneTasks.Single().Id == draft.Id && hub.Notice == null, "resolving Draft moves it to Done");
        hub.SelectedTask = hub.Tasks.Single(t => t.Id == qaId);
        // A second client edits QA first, so this view is stale.
        string db = Path.Combine(folder, "agenthub.db");
        TaskStore.UpdateTask(db, "other-1", qaId, hub.SelectedTask!.Revision, null, "Changed elsewhere", null); // another writer, bypassing this window
        await hub.MoveTaskCommand.RunAsync("in_progress");
        Check(hub.Notice is { } stale && stale.Contains("changed by someone else") && hub.NoticeActionLabel == "Reload board" && hub.OpenTasks.Any(t => t.Id == qaId),
              "a stale move shows the conflict, keeps the stored column and offers a reload");
        await hub.NoticeCommand!.RunAsync();
        await hub.MoveTaskCommand.RunAsync("in_progress");
        Check(hub.ActiveTasks.Single().Id == qaId && hub.Notice == null && hub.ActiveTasks.Single().Outcome == "Changed elsewhere", "after reloading, the move is accepted and keeps the other client's edit");

        // Reopen: a fresh view model finds the same columns, owner and dependency.
        var again = new LocalMind.AgentHub.HubViewModel { Workspaces = new LocalMind.AgentHub.WorkspaceService(new WorkerClient(new FakeWorker())) };
        Check(await again.OpenWorkspaceFolderAsync(folder) is null && again.ActiveTasks.Single() is { OwnerLabel: "Reviewer" } qa && qa.Dependencies.Single().Id == draft.Id && again.DoneTasks.Single().Id == draft.Id,
              "reopening shows the accepted position, owner and dependency");

        // A second workspace stays isolated.
        Check(await hub.CreateWorkspaceAsync("Other", Path.Combine(dir, "other")) is null && hub.Tasks.Count == 0 && hub.ProjectHeading == "Other", "a second workspace shows none of the first one's tasks");
        Check(hub.MoveTaskCommand.CanExecute("open") == false && !hub.AssignCommand.CanExecute(null), "move and assign are unavailable with nothing selected");
    }

    // AH07 ------------------------------------------------------------------------------------------------
    private static void Transitions(string dir)
    {
        var (db, project, id) = Workspace(dir, "Atlas");
        var draft = TaskStore.CreateTask(db, "c-create", project, "Draft", null, id);
        Check(One<long>(db, "SELECT count(*) FROM events") == 1 && draft.Seq == 1, "creating a task commits one event with a sequence number");

        var moved = TaskStore.MoveTask(db, "c-move-1", draft.Id, "in_progress", 0);
        Check(moved is { Duplicate: false, Revision: 1, Seq: 2 } && One<string>(db, "SELECT status FROM tasks") == "in_progress"
              && One<string>(db, "SELECT from_state||'>'||to_state FROM events WHERE seq=2") == "open>in_progress", "an accepted move commits state and event together");

        var again = TaskStore.MoveTask(db, "c-move-1", draft.Id, "in_progress", 0);
        Check(again.Duplicate && again.Seq == 2 && One<long>(db, "SELECT count(*) FROM events") == 2 && One<int>(db, "SELECT revision FROM tasks") == 1,
              "a duplicate command ID returns the first answer and writes nothing");
        Check(Expect(() => TaskStore.UpdateTask(db, "c-move-1", draft.Id, 1, "Renamed", null, null)).Code == "command_reused", "a command ID cannot be reused for a different action");

        string before = Graph(db);
        long events = One<long>(db, "SELECT count(*) FROM events");
        var bad = Expect(() => TaskStore.MoveTask(db, "c-bad", draft.Id, "bogus", 1));
        var invalid = Expect(() => TaskStore.MoveTask(db, "c-invalid", draft.Id, "in_progress", 1));
        Check(bad.Code == "invalid_state" && invalid.Code == "invalid_transition" && invalid.Recovery.Contains("already there") && Graph(db) == before && One<long>(db, "SELECT count(*) FROM events") == events,
              "an invalid move is refused with a next step and writes no event");

        TaskStore.BeforeEventInsert = () => throw new IOException("interrupted between the two writes");
        try { Expect(() => { try { TaskStore.MoveTask(db, "c-cut", draft.Id, "done", 1); } catch (IOException) { throw new StoreFaultException(); } }); }
        catch (StoreFaultException) { }
        finally { TaskStore.BeforeEventInsert = null; }
        Check(Graph(db) == before && One<long>(db, "SELECT count(*) FROM events") == events && One<string>(db, "SELECT status FROM tasks") == "in_progress",
              "an interruption between the state write and the event write rolls both back");
        var retry = TaskStore.MoveTask(db, "c-cut", draft.Id, "done", 1);
        Check(!retry.Duplicate && One<long>(db, "SELECT count(*) FROM events WHERE command_id='c-cut'") == 1 && One<string>(db, "SELECT status FROM tasks") == "done",
              "retrying the interrupted command applies it exactly once");

        var skip = Expect(() => TaskStore.MoveTask(db, "c-skip", draft.Id, "in_progress", 2));
        Check(skip.Code == "invalid_transition" && skip.Recovery.Contains("instead"), "an impossible move (done to in progress) suggests where it can go");

        // Reopen from disk: one event per accepted command, and the last event agrees with the state.
        Check(One<long>(db, "SELECT count(*) FROM events") == 3 && One<long>(db, "SELECT count(DISTINCT command_id) FROM events") == 3
              && One<string>(db, "SELECT to_state FROM events WHERE task_id=(SELECT id FROM tasks) AND kind='task_moved' ORDER BY seq DESC LIMIT 1") == One<string>(db, "SELECT status FROM tasks"),
              "after reopening, events match accepted commands and agree with the task state");
    }

    private sealed class StoreFaultException : Exception { }

    // AH08 ------------------------------------------------------------------------------------------------
    private static void Edits(string dir)
    {
        var (db, project, id) = Workspace(dir, "Atlas");
        var draft = TaskStore.CreateTask(db, "k1", project, "Draft", "A launch note.", id);
        var qa = TaskStore.CreateTask(db, "k2", project, "QA", null, id);
        var dep = TaskStore.AddDependency(db, "k3", qa.Id, draft.Id, 0);
        Check(dep.Revision == 1 && One<long>(db, "SELECT count(*) FROM hub_dependencies") == 1, "QA can depend on Draft");

        string before = Graph(db);
        var blocked = Expect(() => TaskStore.MoveTask(db, "k4", qa.Id, "in_progress", 1));
        Check(blocked.Code == "blocked" && blocked.Message.Contains("“Draft”") && blocked.Recovery.Contains("Go to blocker") && Graph(db) == before,
              "a blocked move names the prerequisite and leaves the stored column alone");

        var assigned = TaskStore.UpdateTask(db, "k5", qa.Id, 1, null, null, "Reviewer");
        Check(assigned.Revision == 2 && One<string>(db, $"SELECT owner FROM tasks WHERE id='{qa.Id}'") == "Reviewer", "assigning a role persists");

        // Two independent clients edit the same revision.
        var a = new WorkerClient(new FakeWorker());
        var b = new WorkerClient(new FakeWorker());
        var won = a.UpdateTaskAsync(db, "a1", draft.Id, 0, title: "Draft the launch note").GetAwaiter().GetResult();
        a.ReleaseWorkspaceAsync(db).GetAwaiter().GetResult(); // one writer at a time: the first client hands the workspace over
        string afterWin = Graph(db);
        var lost = ExpectWorker(() => b.UpdateTaskAsync(db, "b1", draft.Id, 0, title: "Other title").GetAwaiter().GetResult());
        Check(won.Revision == 1 && lost.Code == "conflict" && lost.Message.Contains("now revision 1") && Graph(db) == afterWin
              && One<string>(db, $"SELECT title FROM tasks WHERE id='{draft.Id}'") == "Draft the launch note", "two clients on one revision: one winner, a useful conflict, the first change survives");

        // Graph rules.
        var other = Workspace(Path.Combine(dir, "other"), "Other");
        var foreign = TaskStore.CreateTask(other.Db, "o1", other.Project, "Foreign", null, other.Id);
        string clean = Graph(db);
        // Cross-project edges need two projects in one store.
        string twoProjects = Path.Combine(dir, "two");
        var t = Workspace(twoProjects, "One");
        var onlyHere = TaskStore.CreateTask(t.Db, "x1", t.Project, "Here", null, t.Id);
        using (var w = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = t.Db, Pooling = false }.ToString()))
        {
            w.Open();
            using var c = w.CreateCommand();
            c.CommandText = "INSERT INTO projects VALUES('second','Second'); INSERT INTO tasks(id,project_id,title,status,created_at,revision) VALUES('there','second','There','open','9',0)";
            c.ExecuteNonQuery();
        }
        string beforeCross = Graph(t.Db);
        Check(Expect(() => TaskStore.AddDependency(t.Db, "x2", onlyHere.Id, "there", 0)).Code == "cross_project" && Graph(t.Db) == beforeCross, "a cross-project edge is refused and the graph survives");
        Check(Expect(() => TaskStore.AddDependency(db, "k6", draft.Id, qa.Id, 1)).Code == "cycle" && Expect(() => TaskStore.AddDependency(db, "k7", draft.Id, draft.Id, 1)).Code == "cycle"
              && Graph(db).Contains("e:" + qa.Id + ">" + draft.Id) && One<long>(db, "SELECT count(*) FROM hub_dependencies") == 1, "cycles and self-edges are refused and the valid graph survives");

        // Removing a prerequisite invalidates what a client computed from it.
        var qaNow = One<int>(db, $"SELECT revision FROM tasks WHERE id='{qa.Id}'");
        TaskStore.RemoveDependency(db, "k8", qa.Id, draft.Id, qaNow);
        var stale = Expect(() => TaskStore.MoveTask(db, "k9", qa.Id, "in_progress", qaNow));
        Check(stale.Code == "conflict" && One<int>(db, $"SELECT revision FROM tasks WHERE id='{qa.Id}'") == qaNow + 1, "removing a prerequisite makes a move based on the old readiness stale");
        var fresh = TaskStore.MoveTask(db, "k10", qa.Id, "in_progress", qaNow + 1);
        Check(fresh.Revision == qaNow + 2 && One<string>(db, $"SELECT status FROM tasks WHERE id='{qa.Id}'") == "in_progress", "readiness is recomputed once the task is reread");

        // Resolving the prerequisite unblocks the move; a cancelled prerequisite does not.
        var (db2, p2, id2) = Workspace(Path.Combine(dir, "resolve"), "Atlas");
        var d = TaskStore.CreateTask(db2, "r1", p2, "Draft", null, id2);
        var q = TaskStore.CreateTask(db2, "r2", p2, "QA", null, id2);
        TaskStore.AddDependency(db2, "r3", q.Id, d.Id, 0);
        TaskStore.MoveTask(db2, "r4", d.Id, "cancelled", 0);
        Check(Expect(() => TaskStore.MoveTask(db2, "r5", q.Id, "in_progress", 1)).Code == "blocked", "a cancelled prerequisite still blocks");
        TaskStore.MoveTask(db2, "r6", d.Id, "open", 1);
        TaskStore.MoveTask(db2, "r7", d.Id, "in_progress", 2);
        TaskStore.MoveTask(db2, "r8", d.Id, "done", 3);
        Check(TaskStore.MoveTask(db2, "r9", q.Id, "in_progress", 1).Revision == 2, "a done prerequisite lets the blocked task move");
    }

    // AH63 ------------------------------------------------------------------------------------------------
    private static async Task Roles(string dir, string workerExe)
    {
        async Task<(string[] Texts, string[] Recs)> Script(string folder, IWorker worker)
        {
            var client = new WorkerClient(worker);
            var ws = await client.CreateWorkspaceAsync(folder, "Atlas");
            var task = await client.CreateTaskAsync(ws.DatabasePath, "t", ws.ProjectId, "Draft the launch note", "A launch note.");
            var first = await client.RunRoleAsync(ws.DatabasePath, "b1", task.Id, "builder");
            var rev1 = await client.RunRoleAsync(ws.DatabasePath, "r1", task.Id, "reviewer");
            var second = await client.RunRoleAsync(ws.DatabasePath, "b2", task.Id, "builder");
            var rev2 = await client.RunRoleAsync(ws.DatabasePath, "r2", task.Id, "reviewer");
            Check(first.Revision == 1 && !first.Detail!.Contains("Summary:") && rev1.Detail!.StartsWith("request_changes") && second.Revision == 2
                  && second.Detail!.Contains("Summary:") && second.Detail.Contains("Correction applied") && rev2.Detail!.StartsWith("approve_recommended") && rev2.Detail.Contains("Revision 2"),
                  "builder proposes, reviewer requests a correction, builder revises, reviewer recognizes revision 2");
            Check(One<long>(ws.DatabasePath, "SELECT count(*) FROM outputs WHERE accepted_by IS NOT NULL") == 0 && One<long>(ws.DatabasePath, "SELECT count(*) FROM reviews") == 2,
                  "a reviewer's recommendation is not a human acceptance");
            return ([first.Detail!, second.Detail!], [rev1.Detail!, rev2.Detail!]);
        }

        var fake = await Script(Path.Combine(dir, "fake-a"), new FakeWorker());
        await using var real = new ProcessWorker(workerExe, "profile-roles");
        await real.StartAsync();
        var proc = await Script(Path.Combine(dir, "process"), real);
        var again = await Script(Path.Combine(dir, "fake-b"), new FakeWorker());
        Check(fake.Texts.SequenceEqual(again.Texts) && fake.Recs.SequenceEqual(again.Recs) && fake.Texts.SequenceEqual(proc.Texts), "two runs with the same inputs give identical outputs, in process or not");

        var c = new WorkerClient(new FakeWorker());
        var w = await c.CreateWorkspaceAsync(Path.Combine(dir, "order"), "Atlas");
        var t = await c.CreateTaskAsync(w.DatabasePath, "t", w.ProjectId, "Draft", null);
        var noOut = await c.CallAsync("runRole", new RoleArgs(w.DatabasePath, "rv", t.Id, "reviewer", null));
        var builderFails = await c.CallAsync("runRole", new RoleArgs(w.DatabasePath, "bf", t.Id, "builder", "builder"));
        var reviewerFails = await c.CallAsync("runRole", new RoleArgs(w.DatabasePath, "rf", t.Id, "reviewer", "reviewer"));
        Check(noOut.Error?.Code == "no_output" && builderFails.Error?.Code == "fixture_failure" && reviewerFails.Error?.Code == "fixture_failure"
              && One<long>(w.DatabasePath, "SELECT count(*) FROM outputs") == 0 && One<long>(w.DatabasePath, "SELECT count(*) FROM reviews") == 0,
              "wrong ordering and injected failures return typed errors and write nothing");
    }
}
