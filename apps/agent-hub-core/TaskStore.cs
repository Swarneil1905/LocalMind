using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

/// <summary>Answer to a command. A repeated command ID returns the first answer with Duplicate set and writes nothing.</summary>
public sealed record CommandResult(string Id, int Revision, long Seq, bool Duplicate, string? Detail = null);

/// <summary>Task, dependency, move and fixture-role writes. Every command commits its change and its event in one transaction.</summary>
public static class TaskStore
{
    /// <summary>Test seam: runs between the data write and the event write, inside the transaction.</summary>
    public static Action? BeforeEventInsert { get; set; }

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["open"] = ["in_progress", "cancelled"],
        ["in_progress"] = ["open", "done", "cancelled"],
        ["done"] = ["open"],
        ["cancelled"] = ["open"],
    };

    private sealed record Outcome(string Id, int Revision, string TaskId, string? From, string? To, string? Detail);

    private static string Label(string state) => state.Replace('_', ' ');

    private static CommandResult Run(string workspace, string commandId, string kind, Func<SqliteConnection, Outcome> body)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            throw new StoreException("bad_request", "A command ID is required.", "Update LocalMind Agent Hub so the app and worker match.");
        string path = ProjectStore.DatabaseIn(workspace);
        if (!File.Exists(path)) throw new StoreException("not_found", $"No workspace database at {path}.", "Open the workspace again.");
        using var db = ProjectStore.Connect(path, SqliteOpenMode.ReadWrite);
        db.Open();
        if (ProjectStore.ReadVersion(db) != ProjectStore.CurrentSchema)
            throw new StoreException("store_outdated", "This workspace has not been upgraded yet.", "Open the workspace again so it can be upgraded, then retry.");
        Exec(db, "BEGIN IMMEDIATE");
        try
        {
            using (var c = db.CreateCommand())
            {
                c.CommandText = "SELECT seq, kind, detail FROM events WHERE command_id=$c";
                c.Parameters.AddWithValue("$c", commandId);
                using var r = c.ExecuteReader();
                if (r.Read())
                {
                    if (r.GetString(1) != kind) throw new StoreException("command_reused", "That command ID was already used for a different action.", "Start the action again.");
                    var first = JsonSerializer.Deserialize<CommandResult>(r.GetString(2), Protocol.Json)!;
                    long dupSeq = r.GetInt64(0);
                    Exec(db, "ROLLBACK");
                    return first with { Duplicate = true, Seq = dupSeq };
                }
            }
            var o = body(db);
            BeforeEventInsert?.Invoke();
            var stored = new CommandResult(o.Id, o.Revision, 0, false, o.Detail);
            long seq;
            using (var c = db.CreateCommand())
            {
                c.CommandText = "INSERT INTO events(command_id,task_id,kind,from_state,to_state,detail,created_at) VALUES($c,$t,$k,$f,$to,$d,$at); SELECT last_insert_rowid()";
                c.Parameters.AddWithValue("$c", commandId);
                c.Parameters.AddWithValue("$t", o.TaskId);
                c.Parameters.AddWithValue("$k", kind);
                c.Parameters.AddWithValue("$f", (object?)o.From ?? DBNull.Value);
                c.Parameters.AddWithValue("$to", (object?)o.To ?? DBNull.Value);
                c.Parameters.AddWithValue("$d", JsonSerializer.Serialize(stored, Protocol.Json));
                c.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
                seq = Convert.ToInt64(c.ExecuteScalar());
            }
            Exec(db, "COMMIT");
            return stored with { Seq = seq };
        }
        catch
        {
            try { Exec(db, "ROLLBACK"); } catch (SqliteException) { }
            throw;
        }
    }

    private static void Exec(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    private static T? Scalar<T>(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c.ExecuteScalar() is { } v2 and not DBNull ? (T)Convert.ChangeType(v2, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)) : default;
    }

    private sealed record Row(string Id, string ProjectId, string Title, string Status, int Revision, string? Outcome);

    private static Row Task(SqliteConnection db, string id)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT id, project_id, title, status, revision, outcome FROM tasks WHERE id=$i";
        c.Parameters.AddWithValue("$i", id);
        using var r = c.ExecuteReader();
        if (!r.Read()) throw new StoreException("not_found", "That task no longer exists.", "Reload the board.");
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5));
    }

    private static Row Current(SqliteConnection db, string id, int expected)
    {
        var t = Task(db, id);
        if (t.Revision != expected)
            throw new StoreException("conflict", $"“{t.Title}” was changed by someone else (now revision {t.Revision}, you had {expected}).",
                "Reload the board to see the latest version, then make your change again.");
        return t;
    }

    private static void Bump(SqliteConnection db, string taskId, params (string, object?)[] sets)
    {
        string assign = string.Concat(sets.Select(s => $", {s.Item1[1..]}=" + s.Item1));
        Exec(db, $"UPDATE tasks SET revision=revision+1{assign} WHERE id=$id", [.. sets, ("$id", taskId)]);
    }

    private static string CleanTitle(string? title)
    {
        title = title?.Trim() ?? "";
        if (title.Length is 0 or > 200 || title.Any(char.IsControl))
            throw new StoreException("invalid_title", "A task title needs 1 to 200 characters.", "Enter a shorter title without control characters.");
        return title;
    }

    public static CommandResult CreateTask(string workspace, string commandId, string projectId, string title, string? outcome, Func<string> newId) =>
        Run(workspace, commandId, "task_created", db =>
        {
            title = CleanTitle(title);
            if (Scalar<long>(db, "SELECT count(*) FROM projects WHERE id=$p", ("$p", projectId)) == 0)
                throw new StoreException("not_found", "That project no longer exists.", "Reload the workspace.");
            string id = newId();
            Exec(db, "INSERT INTO tasks(id,project_id,title,status,created_at,revision,outcome,owner) VALUES($i,$p,$t,'open',$at,0,$o,NULL)",
                ("$i", id), ("$p", projectId), ("$t", title), ("$at", DateTime.UtcNow.ToString("O")), ("$o", string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim()));
            return new(id, 0, id, null, "open", null);
        });

    /// <summary>Null leaves a field alone; an empty string clears an outcome or owner.</summary>
    public static CommandResult UpdateTask(string workspace, string commandId, string taskId, int expectedRevision, string? title, string? outcome, string? owner) =>
        Run(workspace, commandId, "task_updated", db =>
        {
            var t = Current(db, taskId, expectedRevision);
            List<(string, object?)> sets = [];
            if (title is not null) sets.Add(("$title", CleanTitle(title)));
            if (outcome is not null) sets.Add(("$outcome", string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim()));
            if (owner is not null) sets.Add(("$owner", string.IsNullOrWhiteSpace(owner) ? null : owner.Trim()));
            Bump(db, taskId, [.. sets]);
            return new(taskId, t.Revision + 1, taskId, null, null, null);
        });

    public static CommandResult AddDependency(string workspace, string commandId, string taskId, string requiresId, int expectedRevision) =>
        Run(workspace, commandId, "dependency_added", db =>
        {
            var t = Current(db, taskId, expectedRevision);
            var other = Task(db, requiresId);
            if (other.ProjectId != t.ProjectId)
                throw new StoreException("cross_project", "A task can only depend on a task in the same project.", "Choose a task from this project.");
            if (taskId == requiresId || Reaches(db, requiresId, taskId))
                throw new StoreException("cycle", $"“{other.Title}” already depends on “{t.Title}”, so this would loop.", "Choose a different prerequisite.");
            if (Scalar<long>(db, "SELECT count(*) FROM hub_dependencies WHERE task_id=$t AND requires_id=$r", ("$t", taskId), ("$r", requiresId)) > 0)
                throw new StoreException("dependency_exists", $"“{t.Title}” already requires “{other.Title}”.", "Nothing to change.");
            Exec(db, "INSERT INTO hub_dependencies VALUES($t,$r)", ("$t", taskId), ("$r", requiresId));
            Bump(db, taskId);
            return new(taskId, t.Revision + 1, taskId, null, null, requiresId);
        });

    public static CommandResult RemoveDependency(string workspace, string commandId, string taskId, string requiresId, int expectedRevision) =>
        Run(workspace, commandId, "dependency_removed", db =>
        {
            var t = Current(db, taskId, expectedRevision);
            using (var c = db.CreateCommand())
            {
                c.CommandText = "DELETE FROM hub_dependencies WHERE task_id=$t AND requires_id=$r";
                c.Parameters.AddWithValue("$t", taskId);
                c.Parameters.AddWithValue("$r", requiresId);
                if (c.ExecuteNonQuery() == 0) throw new StoreException("not_found", "That prerequisite is already gone.", "Reload the board.");
            }
            Bump(db, taskId); // anything that computed readiness from the old edge is now stale
            return new(taskId, t.Revision + 1, taskId, null, null, requiresId);
        });

    private static bool Reaches(SqliteConnection db, string from, string target)
    {
        HashSet<string> seen = [];
        Stack<string> todo = new([from]);
        while (todo.Count > 0)
        {
            string n = todo.Pop();
            if (!seen.Add(n)) continue;
            using var c = db.CreateCommand();
            c.CommandText = "SELECT requires_id FROM hub_dependencies WHERE task_id=$n";
            c.Parameters.AddWithValue("$n", n);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                string next = r.GetString(0);
                if (next == target) return true;
                todo.Push(next);
            }
        }
        return false;
    }

    public static CommandResult MoveTask(string workspace, string commandId, string taskId, string toState, int expectedRevision) =>
        Run(workspace, commandId, "task_moved", db =>
        {
            if (!Allowed.ContainsKey(toState)) throw new StoreException("invalid_state", $"“{toState}” is not a column.", "Choose Open, In progress, Done or Cancelled.");
            var t = Current(db, taskId, expectedRevision);
            if (!Allowed[t.Status].Contains(toState))
                throw new StoreException("invalid_transition", $"“{t.Title}” cannot go from {Label(t.Status)} to {Label(toState)}.",
                    t.Status == toState ? "It is already there." : $"Move it to {string.Join(" or ", Allowed[t.Status].Select(Label))} instead.");
            if (toState is "in_progress" or "done")
            {
                using var c = db.CreateCommand();
                c.CommandText = """
                    SELECT p.title, p.status FROM hub_dependencies e JOIN tasks p ON p.id=e.requires_id AND p.project_id=$proj
                    WHERE e.task_id=$t AND p.status<>'done' ORDER BY p.title, p.id
                    """;
                c.Parameters.AddWithValue("$t", taskId);
                c.Parameters.AddWithValue("$proj", t.ProjectId);
                using var r = c.ExecuteReader();
                List<(string, string)> open = [];
                while (r.Read()) open.Add((r.GetString(0), r.GetString(1)));
                if (open.Count > 0)
                    throw new StoreException("blocked", $"Can’t move “{t.Title}” to {Label(toState)} until “{open[0].Item1}” ({Label(open[0].Item2)}) is done" + (open.Count > 1 ? $", plus {open.Count - 1} more." : "."),
                        "Finish that task first, or use Go to blocker.");
            }
            Bump(db, taskId, ("$status", toState));
            return new(taskId, t.Revision + 1, taskId, t.Status, toState, null);
        });

    /// <summary>Deterministic offline roles. The builder omits a summary until a reviewer asks for one; the reviewer only recommends.</summary>
    public static CommandResult RunRole(string workspace, string commandId, string taskId, string role, string? failure, Func<string> newId) =>
        Run(workspace, commandId, role == "reviewer" ? "review_recorded" : "output_proposed", db =>
        {
            var t = Task(db, taskId);
            if (failure == role) throw new StoreException("fixture_failure", $"The {role} fixture was set to fail.", "Run it again without the failure setting.");
            string? goal = t.Outcome;
            int? latest = Scalar<int?>(db, "SELECT max(revision) FROM outputs WHERE task_id=$t", ("$t", taskId));
            if (role == "builder")
            {
                string? lastRec = latest is null ? null : Scalar<string>(db,
                    "SELECT r.recommendation FROM reviews r JOIN outputs o ON o.id=r.output_id WHERE o.task_id=$t AND o.revision=$v ORDER BY r.rowid DESC LIMIT 1", ("$t", taskId), ("$v", latest));
                string? comment = lastRec == "request_changes" ? Scalar<string>(db,
                    "SELECT r.comment FROM reviews r JOIN outputs o ON o.id=r.output_id WHERE o.task_id=$t AND o.revision=$v ORDER BY r.rowid DESC LIMIT 1", ("$t", taskId), ("$v", latest)) : null;
                int n = (latest ?? 0) + 1;
                string text = $"Draft {n} for “{t.Title}”.\nGoal: {goal ?? "(none recorded)"}\n"
                    + (comment is null ? "Summary is not written yet." : $"Summary: {t.Title} is ready for review.\nCorrection applied: {comment}");
                string id = newId();
                Exec(db, "INSERT INTO outputs(id,task_id,revision,text,author_role,accepted_by,created_at) VALUES($i,$t,$n,$x,'builder (fixture)',NULL,$at)",
                    ("$i", id), ("$t", taskId), ("$n", n), ("$x", text), ("$at", DateTime.UtcNow.ToString("O")));
                return new(id, n, taskId, null, null, text);
            }
            if (role != "reviewer") throw new StoreException("unknown_role", $"There is no “{role}” fixture.", "Use builder or reviewer.");
            if (latest is null) throw new StoreException("no_output", "There is no output to review yet.", "Run the builder first.");
            string output = Scalar<string>(db, "SELECT id FROM outputs WHERE task_id=$t AND revision=$v", ("$t", taskId), ("$v", latest))!;
            string body = Scalar<string>(db, "SELECT text FROM outputs WHERE id=$i", ("$i", output))!;
            bool hasSummary = body.Contains("Summary:");
            string rec = hasSummary ? "approve_recommended" : "request_changes";
            string note = hasSummary ? $"Revision {latest} includes the requested summary." : "Add a one-line summary.";
            string rid = newId();
            Exec(db, "INSERT INTO reviews(id,output_id,recommendation,comment,reviewer_role,created_at) VALUES($i,$o,$r,$c,'reviewer (fixture)',$at)",
                ("$i", rid), ("$o", output), ("$r", rec), ("$c", note), ("$at", DateTime.UtcNow.ToString("O")));
            return new(rid, latest.Value, taskId, null, null, $"{rec}: {note}");
        });
}
