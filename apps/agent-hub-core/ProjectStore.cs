using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

/// <summary>The Agent Hub project store. Only the worker writes to it.</summary>
public static class ProjectStore
{
    public const int CurrentSchema = 11;
    public const string FileName = "agenthub.db";

    /// <summary>Test seam: runs after each migration step, inside the transaction.</summary>
    public static Action<int>? AfterMigrationStep { get; set; }

    internal static SqliteConnection Connect(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());

    /// <summary>Test seam: called with the attempt number before each read-only open's first query; a test can throw the error a racing writer causes.</summary>
    public static Action<int>? BeforeReadProbe { get; set; }

    /// <summary>
    /// A read-only connection that opens while another connection is committing or closing can fail its first query with SQLITE_BUSY (5), SQLITE_LOCKED (6) or
    /// SQLITE_READONLY (8, seen in the Day 5 independent Debug run). None of these means the store is damaged, so the open is retried briefly.
    /// </summary>
    /// <summary>
    /// Runs one read on a read-only connection, repeating it briefly when SQLite reports the transient codes (5, 6, 8) another connection's commit can cause between two queries.
    /// A read-write connection is never retried: there an error is real.
    /// </summary>
    /// <summary>Test seam: runs before every read on a read-only connection, with the attempt number.</summary>
    public static Action<int>? BeforeRead { get; set; }

    internal static T ReadRetry<T>(SqliteConnection db, Func<T> read)
    {
        bool readOnly = db.ConnectionString.Contains("ReadOnly", StringComparison.OrdinalIgnoreCase);
        for (int attempt = 1; ; attempt++)
        {
            try { BeforeRead?.Invoke(attempt); return read(); }
            catch (SqliteException e) when (readOnly && e.SqliteErrorCode is 5 or 6 or 8 && attempt < 12) { Thread.Sleep(10 * attempt); }
        }
    }

    public static SqliteConnection OpenRead(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            var db = Connect(path, SqliteOpenMode.ReadOnly);
            try
            {
                db.Open();
                BeforeReadProbe?.Invoke(attempt);
                Scalar(db, "SELECT count(*) FROM sqlite_schema");
                return db;
            }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6 or 8 && attempt < 12)
            {
                db.Dispose();
                Thread.Sleep(10 * attempt);
            }
            catch { db.Dispose(); throw; }
        }
    }

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
        return Convert.ToInt64(ReadRetry(db, () => c.ExecuteScalar()));
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
        if (version < 10)
        {
            Run(db, V10Objects, tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','10')", tx);
            AfterMigrationStep?.Invoke(10);
        }
        if (version < 11)
        {
            Run(db, V11Objects, tx);
            Run(db, "INSERT OR REPLACE INTO meta VALUES('schema_version','11')", tx);
            AfterMigrationStep?.Invoke(11);
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

    /// <summary>What an attempt was bound to when it started: the preview it was sent from, the draft a review is about, and the file versions its tool may read.</summary>
    private const string V10Objects = """
        ALTER TABLE attempts ADD COLUMN review_output_id TEXT;
        ALTER TABLE attempts ADD COLUMN review_revision INTEGER;
        ALTER TABLE attempts ADD COLUMN binding TEXT;
        CREATE TABLE attempt_files(attempt_id TEXT NOT NULL, name TEXT NOT NULL, content_hash TEXT NOT NULL, content TEXT NOT NULL, PRIMARY KEY(attempt_id, name));
        ALTER TABLE project_settings ADD COLUMN max_concurrency INTEGER NOT NULL DEFAULT 2;
        CREATE TABLE plans(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, brief TEXT NOT NULL, status TEXT NOT NULL, version INTEGER NOT NULL, created_at TEXT NOT NULL, decided_at TEXT,
            planner_model TEXT, planner_input INTEGER, planner_output INTEGER);
        CREATE TABLE plan_tasks(plan_id TEXT NOT NULL, key TEXT NOT NULL, title TEXT NOT NULL, owner TEXT, criteria TEXT NOT NULL DEFAULT '', review INTEGER NOT NULL DEFAULT 1, task_id TEXT,
            position INTEGER NOT NULL, PRIMARY KEY(plan_id, key));
        CREATE TABLE plan_edges(plan_id TEXT NOT NULL, key TEXT NOT NULL, requires_key TEXT NOT NULL, PRIMARY KEY(plan_id, key, requires_key));
        """;

    /// <summary>Plan edits keep the owner's own prerequisite text; work that could not start waits in a visible queue instead of being refused or lost.</summary>
    private const string V11Objects = """
        ALTER TABLE plan_tasks ADD COLUMN after_text TEXT;
        ALTER TABLE events ADD COLUMN event_schema INTEGER NOT NULL DEFAULT 1;
        CREATE TABLE instruction_images(instruction_id TEXT NOT NULL, idx INTEGER NOT NULL, name TEXT NOT NULL, mime TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL,
            size INTEGER NOT NULL, hash TEXT NOT NULL, bytes BLOB, PRIMARY KEY(instruction_id, idx));
        CREATE TABLE documents(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'text', created_at TEXT NOT NULL, removed_at TEXT, forgotten_at TEXT);
        CREATE TABLE document_revisions(doc_id TEXT NOT NULL, revision INTEGER NOT NULL, text TEXT, hash TEXT NOT NULL, bytes BLOB, created_at TEXT NOT NULL, PRIMARY KEY(doc_id, revision));
        CREATE TABLE doc_chunks(doc_id TEXT NOT NULL, revision INTEGER NOT NULL, idx INTEGER NOT NULL, start INTEGER NOT NULL, length INTEGER NOT NULL, PRIMARY KEY(doc_id, revision, idx));
        CREATE TABLE doc_terms(doc_id TEXT NOT NULL, revision INTEGER NOT NULL, idx INTEGER NOT NULL, term TEXT NOT NULL);
        CREATE INDEX doc_terms_term ON doc_terms(term);
        CREATE TABLE doc_thumbs(doc_id TEXT PRIMARY KEY, bytes BLOB NOT NULL);
        CREATE TABLE doc_answers(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, question TEXT NOT NULL, answer TEXT NOT NULL, status TEXT NOT NULL, model TEXT, destination TEXT, packet_text TEXT, created_at TEXT NOT NULL, invalid_refs TEXT);
        CREATE TABLE doc_citations(answer_id TEXT NOT NULL, n INTEGER NOT NULL, doc_id TEXT NOT NULL, revision INTEGER NOT NULL, start INTEGER NOT NULL, length INTEGER NOT NULL, cited INTEGER NOT NULL, PRIMARY KEY(answer_id, n));
        CREATE TABLE reconciliations(attempt_id TEXT PRIMARY KEY, choice TEXT NOT NULL, amount REAL, note TEXT, created_at TEXT NOT NULL);
        CREATE TABLE start_queue(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, task_id TEXT NOT NULL, role TEXT NOT NULL, binding TEXT, accept_price INTEGER NOT NULL DEFAULT 0,
            instruction TEXT, instruction_id TEXT, require_eligible INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, status TEXT NOT NULL, note TEXT, attempt_id TEXT);
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
                    INSERT INTO meta VALUES('schema_version','11');
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
                    """ + V6Objects + V8Objects + V9Objects + V10Objects + V11Objects, tx);
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
        using var db = OpenRead(path);
        int v = ReadVersion(db);
        return v >= 0 && v < CurrentSchema;
    }

    public static WorkspaceInfo Load(string folderOrFile)
    {
        string path = DatabaseIn(folderOrFile);
        if (Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path)))) Migration.Resolve(Path.GetDirectoryName(Path.GetFullPath(path))!); // finish or undo an interrupted upgrade first
        if (!File.Exists(path))
            throw new StoreException("not_found", $"No workspace database at {path}.", "Choose the folder that contains agenthub.db.");
        bool migrated = Migration.UpgradeIfNeeded(path);
        using var db = OpenRead(path);
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
