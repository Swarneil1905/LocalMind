using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

/// <summary>The Agent Hub project store. Only the worker writes to it.</summary>
public static class ProjectStore
{
    public const int CurrentSchema = 4;
    public const string FileName = "agenthub.db";

    /// <summary>Test seam: runs after each migration step, inside the transaction.</summary>
    public static Action<int>? AfterMigrationStep { get; set; }

    internal static SqliteConnection Connect(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());

    internal static void Run(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar());
    }

    private static bool HasTable(SqliteConnection db, string name) =>
        Scalar(db, $"SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='{name}'") != 0;

    /// <summary>-1 not a project database, 0 unversioned legacy layout, otherwise the stored schema version.</summary>
    public static int ReadVersion(SqliteConnection db)
    {
        if (!HasTable(db, "projects") || !HasTable(db, "tasks")) return -1;
        if (!HasTable(db, "meta")) return 0;
        using var c = db.CreateCommand();
        c.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
        return c.ExecuteScalar() is { } v ? int.Parse(v.ToString()!) : 0;
    }

    /// <summary>Upgrades in one transaction: a failure leaves the earlier version untouched.</summary>
    public static bool Migrate(string path)
    {
        using var db = Connect(path, SqliteOpenMode.ReadWrite);
        db.Open();
        int version = ReadVersion(db);
        if (version < 0) throw NotAStore();
        if (version > CurrentSchema)
            throw new StoreException("newer_store", $"This workspace uses schema {version}; this build understands {CurrentSchema}.",
                "Update LocalMind Agent Hub, then open the workspace again.");
        if (version == CurrentSchema) return false;
        using var tx = db.BeginTransaction();
        if (version < 1)
        {
            Run(db, "CREATE TABLE IF NOT EXISTS hub_dependencies(task_id TEXT NOT NULL, requires_id TEXT NOT NULL, PRIMARY KEY(task_id, requires_id))", tx);
            Run(db, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)", tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','1')", tx);
            AfterMigrationStep?.Invoke(1);
        }
        if (version < 2)
        {
            Run(db, "ALTER TABLE tasks ADD COLUMN revision INTEGER NOT NULL DEFAULT 0", tx);
            Run(db, "CREATE TABLE IF NOT EXISTS workspace(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL)", tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','2')", tx);
            AfterMigrationStep?.Invoke(2);
        }
        if (version < 3)
        {
            Run(db, "ALTER TABLE tasks ADD COLUMN outcome TEXT", tx);
            Run(db, "ALTER TABLE tasks ADD COLUMN owner TEXT", tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','3')", tx);
            AfterMigrationStep?.Invoke(3);
        }
        if (version < 4)
        {
            Run(db, """
            CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT, command_id TEXT NOT NULL UNIQUE, task_id TEXT NOT NULL, kind TEXT NOT NULL,
                from_state TEXT, to_state TEXT, detail TEXT, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outputs(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, revision INTEGER NOT NULL, text TEXT NOT NULL, author_role TEXT NOT NULL,
                accepted_by TEXT, created_at TEXT NOT NULL, UNIQUE(task_id, revision));
            CREATE TABLE IF NOT EXISTS reviews(id TEXT PRIMARY KEY, output_id TEXT NOT NULL, recommendation TEXT NOT NULL, comment TEXT NOT NULL,
                reviewer_role TEXT NOT NULL, created_at TEXT NOT NULL);
            """, tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','4')", tx);
            AfterMigrationStep?.Invoke(4);
        }
        tx.Commit();
        return true;
    }

    private static StoreException NotAStore() => new("not_a_workspace", "This file is not an Agent Hub or LocalMind project database.",
        "Choose a projects.db or agenthub.db file, or create a new workspace.");

    internal static string DatabaseIn(string folderOrFile) =>
        Directory.Exists(folderOrFile) ? Path.Combine(folderOrFile, FileName) : folderOrFile;

    public static WorkspaceInfo Create(string folder, string name, Func<string> newId)
    {
        name = name.Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl))
            throw new StoreException("invalid_name", "A workspace name needs 1 to 80 characters.", "Enter a shorter name without control characters.");
        folder = Path.GetFullPath(folder);
        string target = Path.Combine(folder, FileName), temp = target + "." + Guid.NewGuid().ToString("N")[..10] + ".creating";
        if (File.Exists(target))
            throw new StoreException("destination_not_empty", $"{target} already holds a workspace.",
                "Choose an empty folder, or open the existing workspace instead.");
        bool madeFolder = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        string projectId = newId();
        try
        {
            using (var db = Connect(temp, SqliteOpenMode.ReadWriteCreate))
            {
                db.Open();
                using var tx = db.BeginTransaction();
                Run(db, """
                    CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT NOT NULL);
                    CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), title TEXT NOT NULL,
                        status TEXT NOT NULL, created_at TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0,
                        outcome TEXT, owner TEXT);
                    CREATE TABLE hub_dependencies(task_id TEXT NOT NULL, requires_id TEXT NOT NULL, PRIMARY KEY(task_id, requires_id));
                    CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE workspace(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
                    INSERT INTO meta VALUES('schema_version','4');
                    CREATE TABLE events(seq INTEGER PRIMARY KEY AUTOINCREMENT, command_id TEXT NOT NULL UNIQUE, task_id TEXT NOT NULL, kind TEXT NOT NULL,
                        from_state TEXT, to_state TEXT, detail TEXT, created_at TEXT NOT NULL);
                    CREATE TABLE outputs(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, revision INTEGER NOT NULL, text TEXT NOT NULL, author_role TEXT NOT NULL,
                        accepted_by TEXT, created_at TEXT NOT NULL, UNIQUE(task_id, revision));
                    CREATE TABLE reviews(id TEXT PRIMARY KEY, output_id TEXT NOT NULL, recommendation TEXT NOT NULL, comment TEXT NOT NULL,
                        reviewer_role TEXT NOT NULL, created_at TEXT NOT NULL);
                    """, tx);
                using var c = db.CreateCommand();
                c.Transaction = tx;
                c.CommandText = "INSERT INTO projects VALUES($id,$n); INSERT INTO workspace VALUES($w,$n,$t)";
                c.Parameters.AddWithValue("$id", projectId);
                c.Parameters.AddWithValue("$n", name);
                c.Parameters.AddWithValue("$w", newId());
                c.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                c.ExecuteNonQuery();
                tx.Commit();
            }
            File.Move(temp, target); // never overwrites: a late-arriving workspace wins
        }
        catch (IOException) when (File.Exists(target))
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw new StoreException("destination_not_empty", $"{target} already holds a workspace.", "Choose an empty folder, or open the existing workspace instead.");
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (madeFolder && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            throw;
        }
        return new(target, name, projectId, CurrentSchema, false);
    }

    public static WorkspaceInfo Load(string folderOrFile)
    {
        string path = DatabaseIn(folderOrFile);
        if (!File.Exists(path))
            throw new StoreException("not_found", $"No workspace database at {path}.", "Choose the folder that contains agenthub.db.");
        bool migrated = Migrate(path);
        using var db = Connect(path, SqliteOpenMode.ReadOnly);
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = """
            SELECT p.id, COALESCE((SELECT name FROM workspace LIMIT 1), p.name) FROM projects p
            ORDER BY p.name COLLATE NOCASE, p.id LIMIT 1
            """;
        using var r = c.ExecuteReader();
        string id = "", name = Path.GetFileNameWithoutExtension(path);
        if (r.Read()) (id, name) = (r.GetString(0), r.GetString(1));
        return new(path, name, id, CurrentSchema, migrated);
    }

    /// <summary>Copies a legacy database into a new workspace with SQLite's backup API (consistent, includes committed WAL pages).</summary>
    public static WorkspaceInfo Import(string source, string folder, CancellationToken ct, Func<string> newId)
    {
        source = Path.GetFullPath(source);
        folder = Path.GetFullPath(folder);
        string target = Path.Combine(folder, FileName), temp = target + "." + Guid.NewGuid().ToString("N")[..10] + ".importing";
        if (!File.Exists(source))
            throw new StoreException("not_found", $"No file at {source}.", "Choose an existing projects.db file.");
        if (Same(source, target))
            throw new StoreException("same_file", "The source and the new workspace are the same file.", "Choose a different folder for the imported copy.");
        if (File.Exists(target))
            throw new StoreException("destination_not_empty", $"{target} already holds a workspace.", "Choose an empty folder.");
        ct.ThrowIfCancellationRequested();
        bool madeFolder = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        try
        {
            using (var src = Connect(source, SqliteOpenMode.ReadOnly))
            using (var dst = Connect(temp, SqliteOpenMode.ReadWriteCreate))
            {
                src.Open();
                dst.Open();
                if (ReadVersion(src) < 0) throw NotAStore();
                src.BackupDatabase(dst);
            }
            using (var check = Connect(temp, SqliteOpenMode.ReadOnly))
            {
                check.Open();
                using var c = check.CreateCommand();
                c.CommandText = "PRAGMA integrity_check";
                if (c.ExecuteScalar()?.ToString() != "ok")
                    throw new StoreException("copy_failed", "The imported copy failed its integrity check.", "Try the import again; your original file was not changed.");
            }
            Migrate(temp);
            using (var db = Connect(temp, SqliteOpenMode.ReadWrite))
            {
                db.Open();
                using var c = db.CreateCommand();
                c.CommandText = "INSERT INTO workspace SELECT $id,$n,$t WHERE NOT EXISTS (SELECT 1 FROM workspace)";
                c.Parameters.AddWithValue("$id", newId());
                c.Parameters.AddWithValue("$n", Path.GetFileNameWithoutExtension(source));
                c.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                c.ExecuteNonQuery();
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, target);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (madeFolder && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            throw;
        }
        return Load(target);
    }

    private static bool Same(string a, string b)
    {
        static string Resolve(string p) => File.Exists(p) ? new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p : p;
        return string.Equals(Resolve(a), Resolve(b), StringComparison.OrdinalIgnoreCase);
    }
}
