using LocalMind.AgentHub;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 3 checkpoint A review: frozen retries, one completion rule, caller identity, ownership at the write boundary.</summary>
internal static class CheckpointE
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static T One<T>(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar() is { } v and not DBNull ? (T)Convert.ChangeType(v, typeof(T)) : default!;
    }

    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 150 && !done(); i++) await Task.Delay(30);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static WorkerError Fault(Action act)
    {
        try { act(); }
        catch (Exception e) when (e.GetType().Name == "StoreException") { return (WorkerError)e.GetType().GetProperty("Error")!.GetValue(e)!; }
        throw new InvalidOperationException("expected a store error");
    }

    private static async Task<WorkerError> Refused(Func<Task> act)
    {
        try { await act(); }
        catch (WorkerException e) { return e.Error; }
        throw new InvalidOperationException("expected a worker error");
    }

    private static WorkerException Lost() => new(new("outcome_unknown", "The worker stopped while handling the request, so it may or may not have been applied.", "Repeat the action."));

    public static async Task Run(string dir, string workerExe)
    {
        Directory.CreateDirectory(dir);
        await Retry(Path.Combine(dir, "retry"));
        Completion(Path.Combine(dir, "completion"));
        await Identity(Path.Combine(dir, "identity"), workerExe);
        await Ownership(Path.Combine(dir, "ownership"), workerExe);
    }

    /// <summary>A workspace opened in a view model with two review-gated tasks, each with a revision waiting.</summary>
    private static async Task<(HubViewModel Hub, FakeWorker Fake, string Db, string A, string B)> TwoWaiting(string folder)
    {
        var fake = new FakeWorker();
        var hub = new HubViewModel { Workspaces = new WorkspaceService(new WorkerClient(fake)), PickFile = () => Task.FromResult<string?>(null) };
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        await hub.CreateTaskAsync("Task A", "");
        await hub.CreateTaskAsync("Task B", "");
        foreach (var title in new[] { "Task A", "Task B" })
        {
            hub.SelectedTask = hub.Tasks.Single(t => t.Title == title);
            await hub.ToggleReviewCommand.RunAsync();
            await Until(() => hub.SelectedTask?.ReviewRequired == true);
            await hub.AskBuilderCommand.RunAsync();
            await Until(() => hub.HasOutput);
        }
        return (hub, fake, db, hub.Tasks.Single(t => t.Title == "Task A").Id, hub.Tasks.Single(t => t.Title == "Task B").Id);
    }

    // 1. A retry stays with the request it was made for -----------------------------------------------------------------
    private static async Task Retry(string dir)
    {
        // Changed selection.
        var (hub, fake, db, a, b) = await TwoWaiting(Path.Combine(dir, "changed"));
        hub.SelectedTask = hub.Tasks.Single(t => t.Id == a);
        await Until(() => hub.CanDecide && hub.Review?.OutputId != null);
        string subject = hub.DecisionSubject;
        Check(subject.Contains("“Task A”") && subject.Contains("revision 1"), "the decision surface names the task and revision it will decide on");
        fake.AfterHandle = req => req.Op == "decideOutput" ? throw Lost() : Task.CompletedTask;
        await hub.AcceptOutputCommand.RunAsync();
        fake.AfterHandle = null;
        Check(hub.NoticeActionLabel == "Try again" && One<long>(db, "SELECT count(*) FROM decisions") == 1, "the accept for task A committed and its answer was lost");
        hub.SelectedTask = hub.Tasks.Single(t => t.Id == b);
        Check(hub.Review == null && !hub.CanDecide && hub.DecisionSubject == "", "the moment another task is selected, the previous task's review and decision buttons are gone");
        await Until(() => hub.Review?.OutputId != null && hub.CanDecide);
        await hub.NoticeCommand!.RunAsync();
        Check(One<long>(db, "SELECT count(*) FROM decisions") == 1 && One<long>(db, $"SELECT count(*) FROM decisions WHERE task_id='{a}'") == 1
              && One<long>(db, $"SELECT count(*) FROM decisions WHERE task_id='{b}'") == 0 && One<string>(db, $"SELECT status FROM tasks WHERE id='{b}'") != "done",
              "retrying A after selecting B decides nothing for B");

        // Empty selection.
        var two = await TwoWaiting(Path.Combine(dir, "empty"));
        two.Hub.SelectedTask = two.Hub.Tasks.Single(t => t.Id == two.A);
        await Until(() => two.Hub.CanDecide && two.Hub.Review?.OutputId != null);
        two.Fake.AfterHandle = req => req.Op == "decideOutput" ? throw Lost() : Task.CompletedTask;
        await two.Hub.AcceptOutputCommand.RunAsync();
        two.Fake.AfterHandle = null;
        two.Hub.SelectedTask = null;
        await two.Hub.NoticeCommand!.RunAsync();
        Check(One<long>(two.Db, "SELECT count(*) FROM decisions") == 1 && One<long>(two.Db, $"SELECT count(*) FROM decisions WHERE task_id='{two.A}'") == 1,
              "retrying with nothing selected repeats the original request, once");

        // A newer output arrives in the original workspace before the first request committed.
        var late = await TwoWaiting(Path.Combine(dir, "newer"));
        late.Hub.SelectedTask = late.Hub.Tasks.Single(t => t.Id == late.A);
        await Until(() => late.Hub.CanDecide && late.Hub.Review?.OutputId != null);
        late.Fake.BeforeHandle = req => req.Op == "decideOutput" ? throw Lost() : Task.CompletedTask;
        await late.Hub.AcceptOutputCommand.RunAsync();
        late.Fake.BeforeHandle = null;
        TaskStore.RunRole(late.Db, "newer-b", late.A, "builder", null, NewId);
        await late.Hub.NoticeCommand!.RunAsync();
        Check(late.Hub.Notice is { } stale && stale.Contains("no longer the current draft") && One<long>(late.Db, "SELECT count(*) FROM decisions") == 0
              && One<string>(late.Db, $"SELECT status FROM tasks WHERE id='{late.A}'") != "done", "a newer output makes the frozen retry stale; it never decides on revision 2");

        // A different workspace opens before the retry.
        var moved = await TwoWaiting(Path.Combine(dir, "moved"));
        moved.Hub.SelectedTask = moved.Hub.Tasks.Single(t => t.Id == moved.A);
        await Until(() => moved.Hub.CanDecide && moved.Hub.Review?.OutputId != null);
        moved.Fake.AfterHandle = req => req.Op == "decideOutput" ? throw Lost() : Task.CompletedTask;
        await moved.Hub.AcceptOutputCommand.RunAsync();
        moved.Fake.AfterHandle = null;
        string otherFolder = Path.Combine(dir, "moved-other");
        await moved.Hub.CreateWorkspaceAsync("Elsewhere", otherFolder);
        // Another window takes the workspace the user left, before the retry.
        var other = new WorkerClient(new FakeWorker("other-window"));
        string projectA = One<string>(moved.Db, "SELECT id FROM projects");
        Check((await other.AcquireWorkspaceAsync(moved.Db)).Held, "once the user has left it, another window can take the first workspace");
        await moved.Hub.NoticeCommand!.RunAsync();
        Check(One<long>(moved.Db, "SELECT count(*) FROM decisions") == 1 && moved.Hub.ProjectHeading == "Elsewhere" && moved.Hub.Tasks.Count == 0
              && One<long>(Path.Combine(otherFolder, "agenthub.db"), "SELECT count(*) FROM decisions") == 0 && moved.Hub.Notice == null,
              "retrying after opening another workspace settles the original by its status, writes nothing and leaves the new one alone");
        Check((await other.CreateTaskAsync(moved.Db, "o1", projectA, "The other window still writes", null)).Revision == 0,
              "the retry did not touch the other window's ownership of the first workspace");
        await other.ReleaseWorkspaceAsync(moved.Db);
        var third = new WorkerClient(new FakeWorker("third-window"));
        Check((await third.AcquireWorkspaceAsync(moved.Db)).Held, "a second process can take the first workspace right after the background retry");
        await third.ReleaseWorkspaceAsync(moved.Db);

        // A write that never committed is not replayed into a workspace the window no longer shows.
        var away = await TwoWaiting(Path.Combine(dir, "away"));
        away.Hub.SelectedTask = away.Hub.Tasks.Single(t => t.Id == away.A);
        await Until(() => away.Hub.CanDecide && away.Hub.Review?.OutputId != null);
        away.Fake.BeforeHandle = req => req.Op == "decideOutput" ? throw Lost() : Task.CompletedTask;
        await away.Hub.AcceptOutputCommand.RunAsync();
        away.Fake.BeforeHandle = null;
        await away.Hub.CreateWorkspaceAsync("Elsewhere", Path.Combine(dir, "away-other"));
        await away.Hub.NoticeCommand!.RunAsync();
        Check(One<long>(away.Db, "SELECT count(*) FROM decisions") == 0 && away.Hub.Notice is { } nothing && nothing.Contains("nothing was sent")
              && (await new WorkerClient(new FakeWorker("fourth")).AcquireWorkspaceAsync(away.Db)).Held,
              "an uncommitted decision is not replayed into a workspace the window left, and the workspace stays free");

        // A task move behaves the same way: saved (reply lost) and never sent (request lost).
        foreach (bool saved in new[] { true, false })
        {
            var mv = await TwoWaiting(Path.Combine(dir, saved ? "move-saved" : "move-lost"));
            mv.Hub.SelectedTask = mv.Hub.Tasks.Single(t => t.Id == mv.A);
            if (saved) mv.Fake.AfterHandle = req => req.Op == "moveTask" ? throw Lost() : Task.CompletedTask;
            else mv.Fake.BeforeHandle = req => req.Op == "moveTask" ? throw Lost() : Task.CompletedTask;
            await mv.Hub.MoveTaskCommand.RunAsync("in_progress");
            mv.Fake.AfterHandle = null; mv.Fake.BeforeHandle = null;
            Check(mv.Hub.NoticeActionLabel == "Try again", saved ? "the move was saved and its reply lost" : "the move request was lost before it reached the store");
            await mv.Hub.CreateWorkspaceAsync("Elsewhere", Path.Combine(dir, saved ? "move-saved-b" : "move-lost-b"));
            var mvOther = new WorkerClient(new FakeWorker("move-other-" + saved));
            string mvProject = One<string>(mv.Db, "SELECT id FROM projects");
            Check((await mvOther.AcquireWorkspaceAsync(mv.Db)).Held, "another window owns the workspace the user left");
            long eventsBefore = One<long>(mv.Db, "SELECT count(*) FROM events");
            await mv.Hub.NoticeCommand!.RunAsync();
            string status = One<string>(mv.Db, $"SELECT status FROM tasks WHERE id='{mv.A}'");
            Check(One<long>(mv.Db, "SELECT count(*) FROM events") == eventsBefore && mv.Hub.ProjectHeading == "Elsewhere"
                  && (saved ? status == "in_progress" && mv.Hub.Notice == null : status == "open" && mv.Hub.Notice is { } n && n.Contains("nothing was sent")),
                  saved ? "retrying a saved move from another workspace settles it by status and writes nothing" : "retrying a move that never went through asks the user to reopen, and writes nothing");
            Check((await mvOther.CreateTaskAsync(mv.Db, "mo1", mvProject, "Still editable", null)).Revision == 0,
                  "the other window keeps ownership: the move retry did not take or release it");
            await mvOther.ReleaseWorkspaceAsync(mv.Db);
            Check((await new WorkerClient(new FakeWorker("move-third-" + saved)).AcquireWorkspaceAsync(mv.Db)).Held && mv.Hub.CanWrite,
                  "a second process can take the first workspace right after the retry while the visible workspace keeps its own ownership");
        }

        // A retry in the same workspace still reuses the original command ID.
        var same = await TwoWaiting(Path.Combine(dir, "move-same"));
        same.Hub.SelectedTask = same.Hub.Tasks.Single(t => t.Id == same.A);
        same.Fake.AfterHandle = req => req.Op == "moveTask" ? throw Lost() : Task.CompletedTask;
        await same.Hub.MoveTaskCommand.RunAsync("in_progress");
        same.Fake.AfterHandle = null;
        await same.Hub.NoticeCommand!.RunAsync();
        Check(One<string>(same.Db, $"SELECT status FROM tasks WHERE id='{same.A}'") == "in_progress" && One<long>(same.Db, "SELECT count(*) FROM events WHERE kind='task_moved'") == 1,
              "a same-workspace retry settles the original move once");

        // The shortcut only brings the decision into view.
        var focus = await TwoWaiting(Path.Combine(dir, "focus"));
        focus.Hub.SelectedTask = focus.Hub.Tasks.Single(t => t.Id == focus.B);
        await Until(() => focus.Hub.CanDecide && focus.Hub.Review?.OutputId != null);
        int raised = 0;
        focus.Hub.DecisionFocusRequested += () => raised++;
        await focus.Hub.ReviewDecisionCommand.RunAsync();
        Check(raised == 1 && One<long>(focus.Db, "SELECT count(*) FROM decisions") == 0, "the accept shortcut focuses the decision surface and commits nothing");
    }

    // 2 and 3. One completion rule ----------------------------------------------------------------------------------
    private static void Completion(string dir)
    {
        var info = ProjectStore.Create(dir, "Atlas", () => NewId());
        string db = info.DatabasePath;
        string Task_(string id, string title) => TaskStore.CreateTask(db, "c-" + id, info.ProjectId, title, null, NewId).Id;
        string spec = Task_("spec", "Spec"), draft = Task_("draft", "Draft"), qa = Task_("qa", "QA");
        TaskStore.AddDependency(db, "d1", draft, spec, 0);
        TaskStore.AddDependency(db, "d2", qa, draft, 0);
        TaskStore.UpdateTask(db, "d3", draft, 1, null, null, null, reviewRequired: true);
        int Rev(string id) => One<int>(db, $"SELECT revision FROM tasks WHERE id='{id}'");
        string Status(string id) => One<string>(db, $"SELECT status FROM tasks WHERE id='{id}'");

        var out1 = TaskStore.RunRole(db, "b1", draft, "builder", null, NewId);
        var accepted = TaskStore.DecideOutput(db, "acc", draft, out1.Id, 1, 1, "accepted", null, "owner", NewId);
        Check(Status(draft) != "done" && One<string>(db, "SELECT accepted_by FROM outputs") == "owner" && accepted.Detail!.Contains("waiting on Spec")
              && One<string>(db, "SELECT kind||':'||ifnull(to_state,'none') FROM events WHERE command_id='acc'") == "output_accepted:none",
              "accepting Draft's output while its prerequisite is open records the decision and does not complete Draft");
        Check(Fault(() => TaskStore.MoveTask(db, "m1", draft, "in_progress", Rev(draft))).Code == "blocked" && Fault(() => TaskStore.MoveTask(db, "m2", qa, "in_progress", Rev(qa))).Code == "blocked",
              "a direct move of Draft follows the same rule, and QA still waits");

        // The same state read fresh from disk, as after a restart.
        var items = new ProjectReader(db).Tasks(info.ProjectId);
        Check(items.Single(t => t.Id == draft) is { ReviewAccepted: true } d && d.Status != "done" && items.Single(t => t.Id == qa).Start.Allowed == false,
              "after a restart the board shows an accepted, unfinished Draft and a blocked QA");

        TaskStore.MoveTask(db, "s1", spec, "in_progress", Rev(spec));
        TaskStore.MoveTask(db, "s2", spec, "done", Rev(spec));
        TaskStore.MoveTask(db, "g1", draft, "in_progress", Rev(draft));
        TaskStore.MoveTask(db, "g2", draft, "done", Rev(draft));
        Check(Status(draft) == "done" && TaskStore.MoveTask(db, "g3", qa, "in_progress", Rev(qa)).Revision > 0, "once the prerequisite is done, Draft completes and QA may start");

        // A newer revision arrives after Draft was accepted and completed.
        var items2 = new ProjectReader(db).Tasks(info.ProjectId);
        TaskStore.MoveTask(db, "g4", qa, "open", Rev(qa));
        TaskStore.RunRole(db, "b2", draft, "builder", null, NewId);
        var after = new ProjectReader(db).Tasks(info.ProjectId);
        var blocked = Fault(() => TaskStore.MoveTask(db, "g5", qa, "in_progress", Rev(qa)));
        Check(Status(draft) == "done" && blocked.Code == "blocked" && blocked.Message.Contains("done, awaiting your review")
              && after.Single(t => t.Id == qa).MoveNote.Contains("done, awaiting your review") && !after.Single(t => t.Id == qa).Dependencies.Single().IsResolved,
              "a new draft after acceptance keeps Draft marked Done but pending review, and QA waits; the board and the worker agree");
        var out2 = One<string>(db, "SELECT id FROM outputs WHERE revision=2");
        TaskStore.DecideOutput(db, "acc2", draft, out2, 2, 1, "accepted", null, "owner", NewId);
        Check(TaskStore.MoveTask(db, "g6", qa, "in_progress", Rev(qa)).Revision > 0, "accepting the new revision lets QA start");

        // Review turned on for a task that is already Done.
        var done = ProjectStore.Create(Path.Combine(dir, "done"), "Atlas", () => NewId());
        string d2 = TaskStore.CreateTask(done.DatabasePath, "x1", done.ProjectId, "Draft", null, NewId).Id;
        string q2 = TaskStore.CreateTask(done.DatabasePath, "x2", done.ProjectId, "QA", null, NewId).Id;
        TaskStore.AddDependency(done.DatabasePath, "x3", q2, d2, 0);
        TaskStore.MoveTask(done.DatabasePath, "x4", d2, "in_progress", 0);
        TaskStore.MoveTask(done.DatabasePath, "x5", d2, "done", 1);
        TaskStore.UpdateTask(done.DatabasePath, "x6", d2, 2, null, null, null, reviewRequired: true);
        Check(Fault(() => TaskStore.MoveTask(done.DatabasePath, "x7", q2, "in_progress", One<int>(done.DatabasePath, $"SELECT revision FROM tasks WHERE id='{q2}'"))).Code == "blocked",
              "turning on review for a Done task with no accepted output blocks its dependants");

        // Acceptance never completes a cancelled task.
        var cx = ProjectStore.Create(Path.Combine(dir, "cancelled"), "Atlas", () => NewId());
        string cdb = cx.DatabasePath;
        string cd = TaskStore.CreateTask(cdb, "y1", cx.ProjectId, "Draft", null, NewId).Id;
        string cq = TaskStore.CreateTask(cdb, "y2", cx.ProjectId, "QA", null, NewId).Id;
        TaskStore.AddDependency(cdb, "y3", cq, cd, 0);
        TaskStore.UpdateTask(cdb, "y4", cd, 0, null, null, null, reviewRequired: true);
        var cout = TaskStore.RunRole(cdb, "y5", cd, "builder", null, NewId);
        TaskStore.MoveTask(cdb, "y6", cd, "cancelled", One<int>(cdb, $"SELECT revision FROM tasks WHERE id='{cd}'"));
        TaskStore.DecideOutput(cdb, "y7", cd, cout.Id, 1, 1, "accepted", null, "owner", NewId);
        string CStatus() => One<string>(cdb, $"SELECT status FROM tasks WHERE id='{cd}'");
        Check(CStatus() == "cancelled" && One<string>(cdb, "SELECT accepted_by FROM outputs") == "owner"
              && Fault(() => TaskStore.MoveTask(cdb, "y8", cq, "in_progress", One<int>(cdb, $"SELECT revision FROM tasks WHERE id='{cq}'"))).Code == "blocked",
              "accepting a cancelled task's output records the acceptance, keeps the task Cancelled and keeps its dependants blocked");
        Check(new ProjectReader(cdb).Tasks(cx.ProjectId).Single(t => t.Id == cd).Status == "cancelled", "after a restart the task is still Cancelled");
        TaskStore.MoveTask(cdb, "y9", cd, "open", One<int>(cdb, $"SELECT revision FROM tasks WHERE id='{cd}'"));
        Check(CStatus() == "open" && One<string>(cdb, "SELECT accepted_by FROM outputs") == "owner", "an explicit reopen brings the task back, with its acceptance intact");
    }

    // 4. Authority comes from the credential --------------------------------------------------------------------------
    private static async Task Identity(string dir, string exe)
    {
        await using var worker = new ProcessWorker(exe, "profile-identity");
        await worker.StartAsync();
        var owner = new WorkerClient(worker);
        var info = await owner.CreateWorkspaceAsync(Path.Combine(dir, "atlas"), "Atlas");
        string db = info.DatabasePath;
        var task = await owner.CreateTaskAsync(db, "t1", info.ProjectId, "Draft", null);
        await owner.UpdateTaskAsync(db, "t2", task.Id, 0, reviewRequired: true);
        var builder = new WorkerClient(worker) { TokenOverride = await owner.IssueRoleTokenAsync("builder") };
        var reviewer = new WorkerClient(worker) { TokenOverride = await owner.IssueRoleTokenAsync("reviewer") };

        var out1 = await builder.RunRoleAsync(db, "b1", task.Id, "builder");
        Check((await Refused(() => builder.RunRoleAsync(db, "b2", task.Id, "reviewer"))).Code == "not_authorized", "a builder credential cannot run the reviewer role");
        Check((await Refused(() => builder.DecideOutputAsync(db, "b3", task.Id, out1.Id, 1, 1, "accepted", null))).Message.Contains("author"), "the builder cannot accept its own draft");
        Check((await Refused(() => builder.SetPolicyAsync(db, "b4", "reviewer", 1))).Code == "not_authorized", "a fixture credential cannot change the policy");
        Check((await Refused(() => builder.CreateTaskAsync(db, "b5", info.ProjectId, "Sneaky", null))).Code == "not_authorized" && One<long>(db, "SELECT count(*) FROM tasks") == 1,
              "a fixture credential cannot use owner operations");
        await reviewer.RunRoleAsync(db, "r1", task.Id, "reviewer");
        Check((await Refused(() => reviewer.DecideOutputAsync(db, "r2", task.Id, out1.Id, 1, 1, "accepted", null))).Code == "not_authorized" && One<long>(db, "SELECT count(*) FROM decisions") == 0,
              "under human acceptance a reviewer credential cannot decide");
        // Under reviewer approval the reviewer's own identity is what counts.
        await owner.DecideOutputAsync(db, "o1", task.Id, out1.Id, 1, 1, "changes_requested", "Add a summary.");
        Check((await Refused(() => owner.SetPolicyAsync(db, "p0", "reviewer", 9))).Code == "conflict", "the owner's policy change still checks the policy revision");
        await owner.SetPolicyAsync(db, "p1", "reviewer", 1);
        var out2 = await builder.RunRoleAsync(db, "b6", task.Id, "builder");
        Check((await Refused(() => reviewer.DecideOutputAsync(db, "r3", task.Id, out2.Id, 2, 2, "accepted", null))).Code == "not_authorized", "a reviewer cannot accept before it recommends approval");
        await reviewer.RunRoleAsync(db, "r4", task.Id, "reviewer");
        await reviewer.DecideOutputAsync(db, "r5", task.Id, out2.Id, 2, 2, "accepted", null);
        Check(One<string>(db, "SELECT accepted_by FROM outputs WHERE revision=2") == "reviewer (fixture)" && One<string>(db, "SELECT actor FROM decisions WHERE output_id='" + out2.Id + "'") == "reviewer (fixture)",
              "the decision records the reviewer's own identity, not a name in the request");
        await owner.RotateAsync();
        Check((await Refused(() => builder.RunRoleAsync(db, "b7", task.Id, "builder"))).Code == "unauthorized", "rotating the owner credential retires fixture credentials");
    }

    // 5. Ownership is enforced where the write happens ------------------------------------------------------------------
    private static async Task Ownership(string dir, string exe)
    {
        await using var first = new ProcessWorker(exe, "profile-own");
        await using var second = new ProcessWorker(exe, "profile-own");
        await first.StartAsync();
        await second.StartAsync();
        var a = new WorkerClient(first);
        var b = new WorkerClient(second);
        string folder = Path.Combine(dir, "atlas");
        var info = await a.CreateWorkspaceAsync(folder, "Atlas");
        string db = info.DatabasePath;
        var task = await a.CreateTaskAsync(db, "t1", info.ProjectId, "Draft", null);
        await a.UpdateTaskAsync(db, "t2", task.Id, 0, reviewRequired: true);
        var out1 = await a.RunRoleAsync(db, "b1", task.Id, "builder");

        var refused = await Refused(() => b.CreateTaskAsync(db, "x1", info.ProjectId, "From the second process", null));
        Check(refused.Code == "workspace_owned" && refused.Message.Contains($"process {first.ProcessId}") && One<long>(db, "SELECT count(*) FROM tasks") == 1,
              "a second worker cannot write to a workspace the first one owns, and names it");
        var inProcess = new WorkerClient(new FakeWorker());
        Check((await Refused(() => inProcess.CreateTaskAsync(db, "x2", info.ProjectId, "From a helper", null))).Code == "workspace_owned", "a helper worker in another process cannot write either");
        Check((await b.LoadWorkspaceAsync(db))?.Name == "Atlas", "a second window can still open and read the board");
        Check(!(await b.AcquireWorkspaceAsync(db)).Held, "the second window learns it is read-only");

        // An older store that must be migrated is a write.
        string old = Path.Combine(dir, "old");
        Directory.CreateDirectory(old);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(old, "agenthub.db"), Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT); CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT, title TEXT, status TEXT, created_at TEXT); INSERT INTO projects VALUES('p','Old'); INSERT INTO tasks VALUES('t','p','T','open','1')";
            cmd.ExecuteNonQuery();
        }
        await a.AcquireWorkspaceAsync(Path.Combine(old, "agenthub.db"));
        string before = One<string>(Path.Combine(old, "agenthub.db"), "SELECT count(*) FROM sqlite_schema");
        Check((await Refused(() => b.LoadWorkspaceAsync(Path.Combine(old, "agenthub.db")))).Code == "workspace_owned" && One<string>(Path.Combine(old, "agenthub.db"), "SELECT count(*) FROM sqlite_schema") == before,
              "opening an older store, which migrates it, needs ownership and changes nothing without it");

        // An independent workspace is unaffected.
        var other = await b.CreateWorkspaceAsync(Path.Combine(dir, "independent"), "Independent");
        Check((await b.CreateTaskAsync(other.DatabasePath, "i1", other.ProjectId, "Works", null)).Revision == 0, "a second independent workspace stays writable in the second window");

        // The owner lets go: ownership moves.
        await a.ReleaseWorkspaceAsync(db);
        Check((await b.CreateTaskAsync(db, "x3", info.ProjectId, "Now the second window writes", null)).Revision == 0, "ownership transfers when the first window lets go");
        await b.ReleaseWorkspaceAsync(db);
        await a.AcquireWorkspaceAsync(db);

        // Kill the owned worker at a pending decision. Two outcomes, each forced rather than timed.
        static Task KillNow(System.Diagnostics.Process p) { p.Kill(true); p.WaitForExit(3000); p.StandardOutput.Close(); return Task.CompletedTask; }

        // (a) Killed before the commit: a second connection holds the database's write lock, so the decision cannot commit.
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            holder.Open();
            using (var begin = holder.CreateCommand()) { begin.CommandText = "BEGIN EXCLUSIVE"; begin.ExecuteNonQuery(); }
            first.AfterSend = KillNow;
            var lostBefore = await Refused(() => a.DecideOutputAsync(db, "dec-1", task.Id, out1.Id, 1, 1, "accepted", null));
            first.AfterSend = null;
            Check(lostBefore.Code == "outcome_unknown", "the owned worker died while its decision was waiting for the database");
        }
        var claimed = await b.AcquireWorkspaceAsync(db);
        Check(claimed.Held && await b.CommandStatusAsync(db, "dec-1") is not { Duplicate: true } && One<long>(db, "SELECT count(*) FROM decisions") == 0
              && One<string>(db, "SELECT accepted_by FROM outputs WHERE revision=1") == null,
              "killed before the commit: the next owner finds no decision, by command ID and by the data");
        await b.DecideOutputAsync(db, "dec-1", task.Id, out1.Id, 1, 1, "accepted", null);
        Check(One<long>(db, "SELECT count(*) FROM decisions") == 1 && One<string>(db, "SELECT accepted_by FROM outputs WHERE revision=1") == "owner",
              "the next owner repeats the same command ID and the decision is recorded once");

        // (b) Killed after the commit: the kill waits until the decision is visible in the database, then drops the answer.
        await b.ReleaseWorkspaceAsync(db);
        await a.AcquireWorkspaceAsync(db);
        var task3 = await a.CreateTaskAsync(db, "k1", info.ProjectId, "Second draft", null);
        await a.UpdateTaskAsync(db, "k2", task3.Id, 0, reviewRequired: true);
        var out3 = await a.RunRoleAsync(db, "k3", task3.Id, "builder");
        first.AfterSend = async p =>
        {
            for (int i = 0; i < 200 && One<long>(db, "SELECT count(*) FROM decisions") < 2; i++) await Task.Delay(25);
            if (One<long>(db, "SELECT count(*) FROM decisions") < 2) throw new InvalidOperationException("the decision never committed");
            await KillNow(p);
        };
        var lostAfter = await Refused(() => a.DecideOutputAsync(db, "dec-2", task3.Id, out3.Id, 1, 1, "accepted", null));
        first.AfterSend = null;
        Check(lostAfter.Code == "outcome_unknown", "the owned worker died after its decision was durably committed");
        claimed = await b.AcquireWorkspaceAsync(db);
        Check(claimed.Held && await b.CommandStatusAsync(db, "dec-2") is { Duplicate: true } && One<long>(db, "SELECT count(*) FROM decisions") == 2
              && One<string>(db, $"SELECT accepted_by FROM outputs WHERE id='{out3.Id}'") == "owner",
              "killed after the commit: the next owner reconciles by command ID and finds exactly one decision for it");
        var restarted = await Refused(() => a.CreateTaskAsync(db, "x4", info.ProjectId, "From the restarted first worker", null));
        Check(restarted.Code == "workspace_owned" && One<long>(db, "SELECT count(*) FROM tasks WHERE title LIKE 'From the restarted%'") == 0,
              "the restarted first worker stays read-only while the second window owns the workspace");
        var board = new HubViewModel { Workspaces = new WorkspaceService(a) };
        await board.OpenWorkspaceFolderAsync(folder);
        Check(board.Tasks.Count >= 1 && board.ReadOnlyReason != null && board.ReadOnlyReason.Contains($"process {second.ProcessId}"), "the read-only window can still inspect the board and says who owns it");
        await b.ReleaseWorkspaceAsync(db);
        await board.RefreshCommand.RunAsync();
        Check(board.CanWrite && board.ReadOnlyReason == null, "after the owner releases, Refresh makes the waiting window writable");
        await board.ReleaseWriteLockAsync();
    }
}
