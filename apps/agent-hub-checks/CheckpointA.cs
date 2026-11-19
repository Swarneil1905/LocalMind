using System.Security.Cryptography;
using LocalMind.AgentHub;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 2 Checkpoint A: worker contract, channel credentials, store migration, import, workspace creation.</summary>
internal static class CheckpointA
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static SqliteConnection Db(string file, bool readOnly = false) =>
        new(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ToString());

    private static void Sql(string file, string text)
    {
        using var db = Db(file);
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = text;
        c.ExecuteNonQuery();
    }

    private static long Count(string file, string sql)
    {
        using var db = Db(file, true);
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar());
    }

    private static string Snapshot(string file)
    {
        using var db = Db(file, true);
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = """
            SELECT 'p:'||id||'='||name FROM projects UNION ALL
            SELECT 't:'||id||'/'||project_id||'/'||title||'/'||status FROM tasks UNION ALL
            SELECT 'e:'||task_id||'>'||requires_id FROM hub_dependencies ORDER BY 1
            """;
        using var r = c.ExecuteReader();
        List<string> rows = [];
        while (r.Read()) rows.Add(r.GetString(0));
        return string.Join("|", rows);
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    public static async Task Run(string dir)
    {
        string exe = ProcessWorker.Locate() ?? throw new InvalidOperationException("worker executable not found next to the checks");
        await Contract(new FakeWorker(), "fake", Path.Combine(dir, "contract-fake"));
        var real = new ProcessWorker(exe, "profile-check");
        await real.StartAsync();
        await using (real) await Contract(real, "process", Path.Combine(dir, "contract-process"));
        await Recovery(exe, Path.Combine(dir, "recovery"));
        Migration(Path.Combine(dir, "migrate"));
        Import(Path.Combine(dir, "import"));
        await Service(Path.Combine(dir, "service"));
    }

    private static async Task Contract(IWorker worker, string label, string dir)
    {
        var client = new WorkerClient(worker);
        var info = await client.IdentifyAsync();
        Check(info.Protocol == Protocol.Version && info.ProfileId == worker.ProfileId, $"[{label}] identify reports protocol and owning profile");

        client.VersionOverride = 99;
        var old = await client.CallAsync("identify");
        client.VersionOverride = null;
        Check(!old.Ok && old.Error!.Code == "incompatible_version" && old.Error.Recovery.Contains("Update"), $"[{label}] incompatible version gives a recovery action");

        string atlas = Path.Combine(dir, "atlas");
        var made = await client.CreateWorkspaceAsync(atlas, "Atlas");
        var loaded = await client.LoadWorkspaceAsync(atlas);
        Check(made.Name == "Atlas" && loaded is { Name: "Atlas", SchemaVersion: ProjectStore.CurrentSchema } && loaded.ProjectId == made.ProjectId && File.Exists(made.DatabasePath),
              $"[{label}] create then load returns the same workspace");
        string before = Hash(made.DatabasePath);
        var again = await client.CallAsync("createWorkspace", new CreateArgs(atlas, "Other"));
        Check(!again.Ok && again.Error!.Code == "destination_not_empty" && Hash(made.DatabasePath) == before, $"[{label}] populated destination is never overwritten");
        var missing = await client.CallAsync("loadWorkspace", new FolderArgs(Path.Combine(dir, "nope")));
        Check(missing.Error?.Code == "not_found", $"[{label}] missing workspace is a typed error");
        var blank = await client.CallAsync("createWorkspace", new CreateArgs(Path.Combine(dir, "blank"), "  "));
        Check(blank.Error?.Code == "invalid_name" && !Directory.Exists(Path.Combine(dir, "blank")), $"[{label}] invalid name creates no partial profile");

        string never = Path.Combine(dir, "never");
        string good = worker.Token;
        client.TokenOverride = "wrong";
        var wrong = await client.CallAsync("createWorkspace", new CreateArgs(never, "X"));
        client.TokenOverride = null;
        client.OmitToken = true;
        var none = await client.CallAsync("createWorkspace", new CreateArgs(never, "X"));
        client.OmitToken = false;
        Check(wrong.Error?.Code == "unauthorized" && none.Error?.Code == "unauthorized" && !Directory.Exists(never) && Hash(made.DatabasePath) == before,
              $"[{label}] wrong or missing credentials are refused and the store is unchanged");

        await client.RotateAsync();
        client.TokenOverride = good;
        var stale = await client.CallAsync("identify");
        client.TokenOverride = null;
        Check(stale.Error?.Code == "unauthorized" && (await client.IdentifyAsync()).Protocol == Protocol.Version, $"[{label}] rotation retires the old credential");

        string rotated = worker.Token;
        await worker.RestartAsync();
        client.TokenOverride = rotated;
        var afterRestart = await client.CallAsync("identify");
        client.TokenOverride = null;
        Check(afterRestart.Error?.Code == "unauthorized" && (await client.IdentifyAsync()).ProfileId == worker.ProfileId && Hash(made.DatabasePath) == before,
              $"[{label}] a restarted worker issues fresh credentials");

        if (worker is FakeWorker slow) slow.BeforeHandle = _ => Task.Delay(50); // hold the first answer back, as a busy process would
        var first = client.LoadWorkspaceAsync(atlas);
        var second = client.LoadWorkspaceAsync(atlas);
        Check(await first is null && await second is { Name: "Atlas" }, $"[{label}] a response for an old load cannot replace the current one");
        if (worker is FakeWorker done) done.BeforeHandle = null;
    }

    /// <summary>Temp-file collisions, a killed worker, and writes whose outcome is unknown.</summary>
    private static async Task Recovery(string exe, string dir)
    {
        Directory.CreateDirectory(dir);
        // Files the user already has next to a new workspace are never touched or deleted.
        string busy = Path.Combine(dir, "busy");
        Directory.CreateDirectory(busy);
        string[] keep = [ProjectStore.FileName + ".creating", ProjectStore.FileName + ".importing", "notes.txt"];
        foreach (var k in keep) File.WriteAllText(Path.Combine(busy, k), "mine: " + k);
        int n = 0;
        string Id() => "r" + Interlocked.Increment(ref n);
        var created = ProjectStore.Create(busy, "Busy", Id);
        Check(keep.All(k => File.ReadAllText(Path.Combine(busy, k)) == "mine: " + k) && File.Exists(created.DatabasePath), "create keeps pre-existing .creating, .importing and other files");

        string legacy = Path.Combine(dir, "legacy.db");
        Legacy(legacy, 0);
        string importDir = Path.Combine(dir, "import-busy");
        Directory.CreateDirectory(importDir);
        foreach (var k in keep) File.WriteAllText(Path.Combine(importDir, k), "mine: " + k);
        var imported = ProjectStore.Import(legacy, importDir, CancellationToken.None, Id);
        Check(keep.All(k => File.ReadAllText(Path.Combine(importDir, k)) == "mine: " + k) && File.Exists(imported.DatabasePath), "import keeps pre-existing .creating, .importing and other files");

        string race = Path.Combine(dir, "race");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() =>
        {
            try { ProjectStore.Create(race, "Race" + i, Id); return "ok"; }
            catch (Exception e) when (e.GetType().Name == "StoreException") { return e.Message.Contains("already holds") ? "refused" : "other"; }
        })));
        Check(results.Count(r => r == "ok") == 1 && results.Count(r => r == "refused") == 1 && Directory.GetFiles(race).Length == 1,
              "two creates into one folder: one wins, the other is refused, no temp files remain");

        // A killed worker: the next call starts a fresh one.
        var worker = new ProcessWorker(exe, "profile-recovery");
        await worker.StartAsync();
        await using var _ = worker;
        var client = new WorkerClient(worker);
        await client.IdentifyAsync();
        string oldToken = worker.Token;
        worker.Kill();
        var made = await client.CreateWorkspaceAsync(Path.Combine(dir, "after-kill"), "After kill");
        Check(made.Name == "After kill" && worker.Token != oldToken && worker.RequestsSent == 2, "a killed worker is replaced and the request runs once");

        // A write whose outcome is unknown is reported, never replayed.
        int before = worker.RequestsSent;
        // The worker applies the write, then its answer is lost: the client cannot tell whether it was applied.
        worker.AfterSend = p => { p.Kill(true); p.WaitForExit(2000); p.StandardOutput.Close(); return Task.CompletedTask; };
        string uncertain = Path.Combine(dir, "uncertain");
        try { await client.CreateWorkspaceAsync(uncertain, "Uncertain"); Check(false, "uncertain write should be reported"); }
        catch (WorkerException e) { Check(e.Error.Code == "outcome_unknown" && e.Error.Recovery.Contains("same command ID") && worker.RequestsSent == before + 1, "a write that may have been applied is reported once, not replayed"); }
        worker.AfterSend = null;
        Check((await client.IdentifyAsync()).Protocol == Protocol.Version && worker.RequestsSent == before + 2, "the next call after an uncertain write starts a fresh worker");

        // A read can be repeated once on a fresh worker.
        before = worker.RequestsSent;
        int hooks = 0;
        worker.AfterSend = p => { if (hooks++ == 0) { p.Kill(true); p.WaitForExit(2000); p.StandardOutput.Close(); } return Task.CompletedTask; };
        var reloaded = await client.LoadWorkspaceAsync(made.DatabasePath);
        worker.AfterSend = null;
        Check(reloaded is { Name: "After kill" } && worker.RequestsSent == before + 2, "an interrupted read is repeated once on a fresh worker");
    }

    private static void Legacy(string file, int version)
    {
        Sql(file, """
            CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT);
            CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT, title TEXT, status TEXT, created_at TEXT);
            INSERT INTO projects VALUES ('a','Launch'),('b','Other');
            INSERT INTO tasks VALUES ('a1','a','Review','open','1'),('a2','a','Ship','open','2'),('b1','b','Secret','done','1');
            """);
        if (version >= 1)
            Sql(file, """
                CREATE TABLE hub_dependencies(task_id TEXT NOT NULL, requires_id TEXT NOT NULL, PRIMARY KEY(task_id, requires_id));
                INSERT INTO hub_dependencies VALUES ('a2','a1');
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta VALUES('schema_version','1');
                """);
    }

    private static void Migration(string dir)
    {
        Directory.CreateDirectory(dir);
        string v1 = Path.Combine(dir, "v1.db"), v0 = Path.Combine(dir, "v0.db"), newer = Path.Combine(dir, "newer.db");
        Legacy(v1, 1);
        string before = Snapshot(v1);
        var info = ProjectStore.Load(v1);
        Check(info.Migrated && info.SchemaVersion == ProjectStore.CurrentSchema && Snapshot(v1) == before && Count(v1, "SELECT count(*) FROM pragma_table_info('tasks') WHERE name='owner'") == 1,
              "migration v1→current keeps projects, task IDs and dependency edges");
        Check(!ProjectStore.Load(v1).Migrated, "loading a current store does not migrate again");

        Legacy(v0, 0);
        // v0 has no edge table, so compare projects and tasks only.
        string tasksBefore = Count(v0, "SELECT count(*) FROM tasks") + ":" + Count(v0, "SELECT count(*) FROM projects");
        Check(ProjectStore.Load(v0).SchemaVersion == ProjectStore.CurrentSchema && Count(v0, "SELECT count(*) FROM tasks") + ":" + Count(v0, "SELECT count(*) FROM projects") == tasksBefore,
              "migration from an unversioned legacy layout keeps its rows");

        string fail = Path.Combine(dir, "fail.db");
        Legacy(fail, 1);
        before = Snapshot(fail);
        ProjectStore.AfterMigrationStep = step => { if (step == 2) throw new IOException("injected"); };
        try { ProjectStore.Load(fail); Check(false, "injected failure should surface"); }
        catch (IOException) { }
        finally { ProjectStore.AfterMigrationStep = null; }
        using (var db = Db(fail, true))
        {
            db.Open();
            Check(ProjectStore.ReadVersion(db) == 1 && Snapshot(fail) == before, "a failed migration leaves the earlier version intact");
        }
        Check(ProjectStore.Load(fail).Migrated && Snapshot(fail) == before, "reopening after a failed migration completes the upgrade");

        Legacy(newer, 1);
        Sql(newer, "UPDATE meta SET value='99'");
        try { ProjectStore.Load(newer); Check(false, "newer store should be refused"); }
        catch (Exception e) when (e.GetType().Name == "StoreException") { Check(e.Message.Contains("schema 99"), "a store from a newer build is refused, not downgraded"); }
    }

    private static void Import(string dir)
    {
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "projects.db");
        Sql(source, "PRAGMA journal_mode=WAL");
        Legacy(source, 0);
        // Keep a connection open so committed rows stay in the WAL file, not the main database file.
        using var holder = Db(source);
        holder.Open();
        using (var c = holder.CreateCommand())
        {
            c.CommandText = "INSERT INTO projects VALUES ('w','Wal only'); INSERT INTO tasks VALUES ('w1','w','From the WAL','open','9')";
            c.ExecuteNonQuery();
        }
        Check(File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length > 0, "fixture keeps committed rows in the WAL");

        int counter = 0;
        string Id() => "i" + ++counter;
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
            {
                using var c = holder.CreateCommand();
                c.CommandText = $"INSERT INTO tasks VALUES ('x{n++}','a','Concurrent','open','5')";
                try { c.ExecuteNonQuery(); } catch (SqliteException) { }
                Thread.Sleep(1);
            }
        });
        string dest = Path.Combine(dir, "hub");
        var info = ProjectStore.Import(source, dest, CancellationToken.None, Id);
        stop.Cancel();
        writer.Wait();
        string copy = info.DatabasePath;
        Check(Count(copy, "SELECT count(*) FROM tasks WHERE id='w1'") == 1 && Count(copy, "SELECT count(*) FROM projects WHERE id='w'") == 1,
              "import includes rows committed only to the WAL");
        Check(Count(copy, "SELECT count(*) FROM tasks WHERE project_id NOT IN (SELECT id FROM projects)") == 0 && info.SchemaVersion == ProjectStore.CurrentSchema,
              "import under a concurrent legacy writer is consistent and migrated");

        long sourceProjects = Count(source, "SELECT count(*) FROM projects");
        Sql(copy, "INSERT INTO projects VALUES ('hub-only','Hub edit')");
        Check(Count(source, "SELECT count(*) FROM projects") == sourceProjects && Count(copy, "SELECT count(*) FROM projects") == sourceProjects + 1,
              "Hub edits touch only the imported copy");

        try { ProjectStore.Import(source, Path.Combine(dir, "hub"), CancellationToken.None, Id); Check(false, "populated destination should refuse"); }
        catch (Exception e) when (e.GetType().Name == "StoreException") { Check(e.Message.Contains("already holds"), "import never overwrites a populated destination"); }
        try { ProjectStore.Import(copy, Path.GetDirectoryName(copy)!, CancellationToken.None, Id); Check(false, "alias should refuse"); }
        catch (Exception e) when (e.GetType().Name == "StoreException") { Check(e.Message.Contains("same file"), "source and destination aliases are refused"); }

        string cancelled = Path.Combine(dir, "cancelled");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { ProjectStore.Import(source, cancelled, cts.Token, Id); Check(false, "cancelled import should stop"); }
        catch (OperationCanceledException) { Check(!Directory.Exists(cancelled), "a cancelled import leaves no partial profile"); }
    }

    private static async Task Service(string dir)
    {
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "projects.db");
        Legacy(source, 0);
        var vm = new HubViewModel();
        await vm.OpenAsync(source);
        Check(vm.Projects.Count == 2 && vm.ModeLabel.Contains("Read-only"), "the legacy path still opens read-only after import work");

        var client = new WorkerClient(new FakeWorker());
        var service = new WorkspaceService(client);
        var hub = new HubViewModel { Workspaces = service };
        string atlas = Path.Combine(dir, "atlas");
        Check(await hub.CreateWorkspaceAsync("Atlas", atlas) is null, "File → New workspace opens the new project");
        Check(hub.ProjectHeading == "Atlas" && hub.Projects.Count == 1 && hub.Tasks.Count == 0 && hub.ModeLabel.Contains("Atlas"), "a new workspace shows its project and an empty board");

        var reopened = new HubViewModel { Workspaces = new WorkspaceService(new WorkerClient(new FakeWorker())) };
        Check(await reopened.OpenWorkspaceFolderAsync(atlas) is null && reopened.ProjectHeading == "Atlas" && reopened.Projects.Select(p => p.Id).SequenceEqual(hub.Projects.Select(p => p.Id)),
              "reopening finds the same project");

        string beforeHeading = hub.ProjectHeading;
        var error = await hub.CreateWorkspaceAsync("Second", atlas);
        Check(error is not null && error.Contains("Choose an empty folder") && hub.ProjectHeading == beforeHeading, "a populated destination reports a recovery and keeps the selection");
        var bad = await hub.CreateWorkspaceAsync(" ", Path.Combine(dir, "blank"));
        Check(bad is not null && !Directory.Exists(Path.Combine(dir, "blank")) && hub.ProjectHeading == beforeHeading, "an invalid name creates nothing and keeps the selection");
    }
}
