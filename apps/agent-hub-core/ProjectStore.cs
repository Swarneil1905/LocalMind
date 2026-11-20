using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

/// <summary>The Agent Hub project store. Only the worker writes to it.</summary>
public static class ProjectStore
{
    public const int CurrentSchema = 9;
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
        if (version < 5)
        {
            Run(db, """
            ALTER TABLE tasks ADD COLUMN review_required INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE outputs ADD COLUMN run_id TEXT;
            ALTER TABLE reviews ADD COLUMN run_id TEXT;
            CREATE TABLE runs(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, role TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE policy(id INTEGER PRIMARY KEY CHECK(id=1), name TEXT NOT NULL, revision INTEGER NOT NULL);
            INSERT INTO policy VALUES(1,'human',1);
            CREATE TABLE decisions(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, run_id TEXT, output_id TEXT NOT NULL, content_revision INTEGER NOT NULL,
                policy_revision INTEGER NOT NULL, actor TEXT NOT NULL, decision TEXT NOT NULL, comment TEXT, submission_id TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL);
            -- Output and review rows from v4 get a run of their own, so every record has a stable run identity.
            INSERT INTO runs SELECT 'run-'||id, task_id, 'builder', created_at FROM outputs;
            UPDATE outputs SET run_id='run-'||id;
            INSERT INTO runs SELECT 'run-'||r.id, o.task_id, 'reviewer', r.created_at FROM reviews r JOIN outputs o ON o.id=r.output_id;
            UPDATE reviews SET run_id='run-'||id;
            """, tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','5')", tx);
            AfterMigrationStep?.Invoke(5);
        }
        if (version < 6)
        {
            Run(db, """
            ALTER TABLE outputs ADD COLUMN content_hash TEXT;
            ALTER TABLE outputs ADD COLUMN kind TEXT NOT NULL DEFAULT 'text';
            ALTER TABLE outputs ADD COLUMN blob_path TEXT;
            ALTER TABLE outputs ADD COLUMN byte_size INTEGER NOT NULL DEFAULT 0;
            """, tx);
            // Existing outputs keep their IDs; only the hash and size are added, computed from the stored text.
            List<(string Id, string Text)> rows = [];
            using (var q = db.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT id, text FROM outputs";
                using var r = q.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
            }
            foreach (var (id, text) in rows)
            {
                using var u = db.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE outputs SET content_hash=$h, byte_size=$n WHERE id=$i";
                u.Parameters.AddWithValue("$h", Hash(System.Text.Encoding.UTF8.GetBytes(text)));
                u.Parameters.AddWithValue("$n", System.Text.Encoding.UTF8.GetByteCount(text));
                u.Parameters.AddWithValue("$i", id);
                u.ExecuteNonQuery();
            }
            Run(db, V6Objects, tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','6')", tx);
            AfterMigrationStep?.Invoke(6);
        }
        if (version < 7)
        {
            Run(db, """
            ALTER TABLE runs ADD COLUMN packet_project TEXT;
            ALTER TABLE runs ADD COLUMN packet_limit INTEGER;
            ALTER TABLE runs ADD COLUMN packet_tokens INTEGER;
            ALTER TABLE runs ADD COLUMN packet_text TEXT;
            ALTER TABLE runs ADD COLUMN packet_sources TEXT;
            """, tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','7')", tx);
            AfterMigrationStep?.Invoke(7);
        }
        if (version < 8)
        {
            Run(db, V8Objects, tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','8')", tx);
            AfterMigrationStep?.Invoke(8);
        }
        if (version < 9)
        {
            Run(db, V9Objects, tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','9')", tx);
            AfterMigrationStep?.Invoke(9);
        }
        tx.Commit();
        return true;
    }

    /// <summary>SHA-256 of the exact bytes, lowercase hex.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A question an agent put to the owner. It has its own durable ID and is open until answered once.</summary>
    private const string V8Objects = """
        CREATE TABLE questions(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, task_id TEXT NOT NULL, run_id TEXT, asked_by TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL,
            answer TEXT, answered_by TEXT, answered_at TEXT, answer_command_id TEXT);
        """;

    /// <summary>
    /// Models and runs: connections and what they offer, who plays which role, per-project limits, consent to a destination,
    /// versioned prices, each attempt with its lease and route, a budget ledger, owner instructions and supplied files.
    /// </summary>
    private const string V9Objects = """
        CREATE TABLE connections(id TEXT PRIMARY KEY, provider TEXT NOT NULL, endpoint TEXT NOT NULL, label TEXT NOT NULL, is_cloud INTEGER NOT NULL,
            credential_ref TEXT, credential_status TEXT NOT NULL DEFAULT 'none', credential_changed_at TEXT, last_probe_at TEXT, last_probe_ok INTEGER, last_error TEXT, created_at TEXT NOT NULL);
        CREATE TABLE connection_models(connection_id TEXT NOT NULL, model_id TEXT NOT NULL, capabilities TEXT NOT NULL, available INTEGER NOT NULL, discovered_at TEXT NOT NULL,
            PRIMARY KEY(connection_id, model_id));
        CREATE TABLE assignments(project_id TEXT NOT NULL, role TEXT NOT NULL, connection_id TEXT NOT NULL, model_id TEXT NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY(project_id, role));
        CREATE TABLE project_settings(project_id TEXT PRIMARY KEY, local_only INTEGER NOT NULL DEFAULT 1, budget_limit REAL, currency TEXT NOT NULL DEFAULT 'USD',
            max_steps INTEGER NOT NULL DEFAULT 4, max_seconds INTEGER NOT NULL DEFAULT 180, max_tool_calls INTEGER NOT NULL DEFAULT 3, max_output_tokens INTEGER NOT NULL DEFAULT 600,
            only_providers TEXT, auto_review INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE consents(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, connection_id TEXT NOT NULL, model_id TEXT NOT NULL, files TEXT NOT NULL, granted_at TEXT NOT NULL, revoked_at TEXT);
        CREATE TABLE prices(id TEXT PRIMARY KEY, connection_id TEXT NOT NULL, model_id TEXT NOT NULL, currency TEXT NOT NULL, input_per_mtok REAL NOT NULL, output_per_mtok REAL NOT NULL,
            source TEXT NOT NULL, effective_at TEXT NOT NULL, recorded_at TEXT NOT NULL);
        CREATE TABLE attempts(id TEXT PRIMARY KEY, command_id TEXT NOT NULL UNIQUE, run_id TEXT NOT NULL, project_id TEXT NOT NULL, task_id TEXT NOT NULL, role TEXT NOT NULL,
            connection_id TEXT NOT NULL, provider TEXT NOT NULL, requested_model TEXT NOT NULL, reported_model TEXT, reported_provider TEXT, route_note TEXT,
            status TEXT NOT NULL, lease_owner TEXT, lease_expires TEXT, created_at TEXT NOT NULL, sent_at TEXT, ended_at TEXT, stop_reason TEXT, error_code TEXT,
            partial_text TEXT NOT NULL DEFAULT '', transcript TEXT NOT NULL DEFAULT '[]', packet_text TEXT NOT NULL DEFAULT '', steps INTEGER NOT NULL DEFAULT 0, tool_calls INTEGER NOT NULL DEFAULT 0,
            input_tokens INTEGER, output_tokens INTEGER, estimate REAL, cost REAL, currency TEXT, price_id TEXT, price_status TEXT, instruction_id TEXT, output_id TEXT, retry_of TEXT, revision INTEGER);
        CREATE TABLE budget_ledger(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, attempt_id TEXT NOT NULL, kind TEXT NOT NULL, amount REAL, currency TEXT NOT NULL, created_at TEXT NOT NULL,
            UNIQUE(attempt_id, kind));
        CREATE TABLE instructions(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, task_id TEXT NOT NULL, recipients TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE task_files(task_id TEXT NOT NULL, name TEXT NOT NULL, content TEXT NOT NULL, PRIMARY KEY(task_id, name));
        """;

    private const string V6Objects = """
        CREATE TABLE output_checks(id INTEGER PRIMARY KEY AUTOINCREMENT, output_id TEXT NOT NULL, name TEXT NOT NULL, status TEXT NOT NULL, detail TEXT, created_at TEXT NOT NULL);
        CREATE TRIGGER outputs_immutable BEFORE UPDATE OF task_id, revision, text, author_role, content_hash, kind, blob_path, byte_size ON outputs
        BEGIN SELECT RAISE(ABORT, 'An output is immutable once it is stored.'); END;
        """;

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
                        outcome TEXT, owner TEXT, review_required INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE hub_dependencies(task_id TEXT NOT NULL, requires_id TEXT NOT NULL, PRIMARY KEY(task_id, requires_id));
                    CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE workspace(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
                    INSERT INTO meta VALUES('schema_version','9');
                    CREATE TABLE events(seq INTEGER PRIMARY KEY AUTOINCREMENT, command_id TEXT NOT NULL UNIQUE, task_id TEXT NOT NULL, kind TEXT NOT NULL,
                        from_state TEXT, to_state TEXT, detail TEXT, created_at TEXT NOT NULL);
                    CREATE TABLE outputs(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, revision INTEGER NOT NULL, text TEXT NOT NULL, author_role TEXT NOT NULL,
                        accepted_by TEXT, created_at TEXT NOT NULL, run_id TEXT, content_hash TEXT, kind TEXT NOT NULL DEFAULT 'text', blob_path TEXT, byte_size INTEGER NOT NULL DEFAULT 0, UNIQUE(task_id, revision));
                    CREATE TABLE reviews(id TEXT PRIMARY KEY, output_id TEXT NOT NULL, recommendation TEXT NOT NULL, comment TEXT NOT NULL,
                        reviewer_role TEXT NOT NULL, created_at TEXT NOT NULL, run_id TEXT);
                    CREATE TABLE runs(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, role TEXT NOT NULL, created_at TEXT NOT NULL,
                        packet_project TEXT, packet_limit INTEGER, packet_tokens INTEGER, packet_text TEXT, packet_sources TEXT);
                    CREATE TABLE policy(id INTEGER PRIMARY KEY CHECK(id=1), name TEXT NOT NULL, revision INTEGER NOT NULL);
                    INSERT INTO policy VALUES(1,'human',1);
                    CREATE TABLE decisions(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, run_id TEXT, output_id TEXT NOT NULL, content_revision INTEGER NOT NULL,
                        policy_revision INTEGER NOT NULL, actor TEXT NOT NULL, decision TEXT NOT NULL, comment TEXT, submission_id TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL);
                    """ + V6Objects + V8Objects + V9Objects, tx);
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

    /// <summary>True when opening this store would have to upgrade it, which is a write.</summary>
    public static bool NeedsMigration(string path)
    {
        using var db = Connect(path, SqliteOpenMode.ReadOnly);
        db.Open();
        int v = ReadVersion(db);
        return v >= 0 && v < CurrentSchema;
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
