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

    internal sealed record Outcome(string Id, int Revision, string TaskId, string? From, string? To, string? Detail);

    private static string Label(string state) => state.Replace('_', ' ');

    internal static CommandResult Run(string workspace, string commandId, string kind, Func<SqliteConnection, Outcome> body)
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
                c.CommandText = "INSERT INTO events(command_id,task_id,kind,from_state,to_state,detail,created_at,event_schema) VALUES($c,$t,$k,$f,$to,$d,$at,2); SELECT last_insert_rowid()";
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

    internal static void Exec(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    /// <summary>Runs one statement and says how many rows it changed.</summary>
    internal static int Exec2(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c.ExecuteNonQuery();
    }

    internal static T? Scalar<T>(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return ProjectStore.ReadRetry(db, () => c.ExecuteScalar()) is { } v2 and not DBNull ? (T)Convert.ChangeType(v2, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)) : default;
    }

    internal sealed record Row(string Id, string ProjectId, string Title, string Status, int Revision, string? Outcome, bool ReviewRequired);

    internal static Row Task(SqliteConnection db, string id)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT id, project_id, title, status, revision, outcome, review_required FROM tasks WHERE id=$i";
        c.Parameters.AddWithValue("$i", id);
        using var r = c.ExecuteReader();
        if (!r.Read()) throw new StoreException("not_found", "That task no longer exists.", "Reload the board.");
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6) != 0);
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

    /// <summary>Read-only: what a command ID did, or null when it was never applied. Lets a client reconcile after a lost answer.</summary>
    public static CommandResult? CommandStatus(string workspace, string commandId)
    {
        string path = ProjectStore.DatabaseIn(workspace);
        if (!File.Exists(path)) throw new StoreException("not_found", $"No workspace database at {path}.", "Open the workspace again.");
        using var db = ProjectStore.OpenRead(path);
        using var c = db.CreateCommand();
        c.CommandText = "SELECT seq, detail FROM events WHERE command_id=$c";
        c.Parameters.AddWithValue("$c", commandId);
        using var r = c.ExecuteReader();
        return r.Read() ? JsonSerializer.Deserialize<CommandResult>(r.GetString(1), Protocol.Json)! with { Duplicate = true, Seq = r.GetInt64(0) } : null;
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
            return new(id, 0, id, null, "open", "title=" + title);
        });

    /// <summary>Null leaves a field alone; an empty string clears an outcome or owner.</summary>
    public static CommandResult UpdateTask(string workspace, string commandId, string taskId, int expectedRevision, string? title, string? outcome, string? owner, bool? reviewRequired = null) =>
        Run(workspace, commandId, "task_updated", db =>
        {
            var t = Current(db, taskId, expectedRevision);
            List<(string, object?)> sets = [];
            if (title is not null) sets.Add(("$title", CleanTitle(title)));
            if (outcome is not null) sets.Add(("$outcome", string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim()));
            if (owner is not null) sets.Add(("$owner", string.IsNullOrWhiteSpace(owner) ? null : owner.Trim()));
            if (reviewRequired is { } rr) sets.Add(("$review_required", rr ? 1 : 0));
            Bump(db, taskId, [.. sets]);
            return new(taskId, t.Revision + 1, taskId, null, null, title is not null ? "title=" + CleanTitle(title) : null);
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

    private sealed record OpenPrerequisite(string Title, string Status, bool AwaitingReview)
    {
        public string Label => Status.Replace('_', ' ') + (AwaitingReview ? ", awaiting your review" : "");
    }

    /// <summary>
    /// The one completion rule. A prerequisite is satisfied only when it is Done and, if it needs review, its latest output is accepted.
    /// Direct moves, acceptance and dependants all use this.
    /// </summary>
    private static List<OpenPrerequisite> OpenPrerequisites(SqliteConnection db, string taskId, string projectId)
    {
        using var c = db.CreateCommand();
        c.CommandText = """
            SELECT p.title, p.status,
                   (p.review_required=1 AND COALESCE((SELECT accepted_by FROM outputs WHERE task_id=p.id ORDER BY revision DESC LIMIT 1),'')='')
            FROM hub_dependencies e JOIN tasks p ON p.id=e.requires_id AND p.project_id=$proj
            WHERE e.task_id=$t AND (p.status<>'done' OR (p.review_required=1 AND COALESCE((SELECT accepted_by FROM outputs WHERE task_id=p.id ORDER BY revision DESC LIMIT 1),'')=''))
            ORDER BY p.title, p.id
            """;
        c.Parameters.AddWithValue("$t", taskId);
        c.Parameters.AddWithValue("$proj", projectId);
        using var r = c.ExecuteReader();
        List<OpenPrerequisite> open = [];
        while (r.Read()) open.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2) != 0));
        return open;
    }

    private static bool LatestAccepted(SqliteConnection db, string taskId) =>
        Scalar<string>(db, "SELECT accepted_by FROM outputs WHERE task_id=$t ORDER BY revision DESC LIMIT 1", ("$t", taskId)) != null;

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
                var open = OpenPrerequisites(db, taskId, t.ProjectId);
                if (open.Count > 0)
                    throw new StoreException("blocked", $"Can’t move “{t.Title}” to {Label(toState)} until “{open[0].Title}” ({open[0].Label}) is done" + (open.Count > 1 ? $", plus {open.Count - 1} more." : "."),
                        "Finish that task first, or use Go to blocker.");
            }
            if (toState == "done" && t.ReviewRequired && !LatestAccepted(db, taskId))
                throw new StoreException("review_required", $"“{t.Title}” needs an accepted output before it can be Done.",
                    "Open the task, read its latest output and accept it.");
            Bump(db, taskId, ("$status", toState));
            return new(taskId, t.Revision + 1, taskId, t.Status, toState, null);
        });

    private static string Quoted(string heading)
    {
        int a = heading.IndexOf('“'), b = heading.IndexOf('”');
        return a >= 0 && b > a ? heading[a..(b + 1)] : heading;
    }

    /// <summary>A run keeps the packet its recipient was given: project, limit, size, text and the source outputs with their revisions.</summary>
    private static void RecordRun(SqliteConnection db, string run, string taskId, string role, Packet packet) =>
        Exec(db, "INSERT INTO runs(id,task_id,role,created_at,packet_project,packet_limit,packet_tokens,packet_text,packet_sources) VALUES($r,$t,$o,$at,$p,$l,$k,$x,$s)",
            ("$r", run), ("$t", taskId), ("$o", role), ("$at", DateTime.UtcNow.ToString("O")), ("$p", packet.ProjectId), ("$l", packet.TokenLimit), ("$k", packet.TokensUsed), ("$x", packet.Text),
            ("$s", JsonSerializer.Serialize(packet.Sections.Where(p => p.SourceId != null).Select(p => new { id = p.SourceId, revision = p.SourceRevision }))));

    /// <summary>Deterministic offline roles. The builder omits a summary until a reviewer asks for one; the reviewer only recommends.</summary>
    public static CommandResult RunRole(string workspace, string commandId, string taskId, string role, string? failure, Func<string> newId) =>
        Run(workspace, commandId, role == "reviewer" ? "review_recorded" : "output_proposed", db =>
        {
            var t = Task(db, taskId);
            if (failure == role) throw new StoreException("fixture_failure", $"The {role} fixture was set to fail.", "Run it again without the failure setting.");
            int? latest = Scalar<int?>(db, "SELECT max(revision) FROM outputs WHERE task_id=$t", ("$t", taskId));
            if (role == "builder")
            {
                int n = (latest ?? 0) + 1;
                // The builder writes from the handoff packet it is given and from nothing else: the criteria and the correction come from what the
                // packet admitted. Content the packet left out (a long criteria tail, feedback that did not fit) is not available to it.
                var packet = Handoff.Build(db, t.ProjectId, taskId, "builder", Limits.DefaultPacketTokens);
                string criteriaBody = packet.Sections.First(p => p.Heading.StartsWith("Task and criteria")).Body;
                int at = criteriaBody.IndexOf("\nCriteria: ", StringComparison.Ordinal);
                string goal = at >= 0 ? criteriaBody[(at + "\nCriteria: ".Length)..] : "(none recorded)";
                string? comment = packet.Sections.FirstOrDefault(p => p.Heading.StartsWith("Reviewer feedback"))?.Body;
                string sources = string.Join("; ", packet.Sections.Where(p => p.SourceId != null && p.Heading.StartsWith("Accepted output of"))
                    .Select(p => $"{Quoted(p.Heading)} output {p.SourceId![..8]} revision {p.SourceRevision}"));
                string text = $"Draft {n} for “{t.Title}”.\nGoal: {goal}\n"
                    + (comment is null ? "Summary is not written yet." : $"Summary: {t.Title} is ready for review.\nCorrection applied: {comment}")
                    + (sources.Length > 0 ? $"\nSources: {sources}" : "");
                string id = newId(), run = newId();
                RecordRun(db, run, taskId, "builder", packet);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                Exec(db, "INSERT INTO outputs(id,task_id,revision,text,author_role,accepted_by,created_at,run_id,content_hash,kind,byte_size) VALUES($i,$t,$n,$x,'builder (fixture)',NULL,$at,$r,$h,'text',$z)",
                    ("$i", id), ("$t", taskId), ("$n", n), ("$x", text), ("$at", DateTime.UtcNow.ToString("O")), ("$r", run), ("$h", ProjectStore.Hash(bytes)), ("$z", bytes.Length));
                return new(id, n, taskId, null, null, text);
            }
            if (role != "reviewer") throw new StoreException("unknown_role", $"There is no “{role}” fixture.", "Use builder or reviewer.");
            if (latest is null) throw new StoreException("no_output", "There is no output to review yet.", "Run the builder first.");
            string output = Scalar<string>(db, "SELECT id FROM outputs WHERE task_id=$t AND revision=$v", ("$t", taskId), ("$v", latest))!;
            // The reviewer reads the draft from its handoff packet, not from the store: what it was given is what it judged.
            var packet2 = Handoff.Build(db, t.ProjectId, taskId, "reviewer", Limits.DefaultPacketTokens);
            var draft = packet2.Sections.FirstOrDefault(p => p.Heading.StartsWith("Output under review") && p.SourceId == output)
                ?? throw new StoreException("packet_omitted_draft", "The draft is too large for the handoff limit, so the reviewer was not given it.", "Shorten the draft, then ask again.");
            bool hasSummary = draft.Body.Contains("Summary:");
            string rec = hasSummary ? "approve_recommended" : "request_changes";
            string note = hasSummary ? $"Revision {latest} includes the requested summary." : "Add a one-line summary.";
            string rid = newId(), rrun = newId();
            RecordRun(db, rrun, taskId, "reviewer", packet2);
            Exec(db, "INSERT INTO reviews(id,output_id,recommendation,comment,reviewer_role,created_at,run_id) VALUES($i,$o,$r,$c,'reviewer (fixture)',$at,$u)",
                ("$i", rid), ("$o", output), ("$r", rec), ("$c", note), ("$at", DateTime.UtcNow.ToString("O")), ("$u", rrun));
            return new(rid, latest.Value, taskId, null, null, $"{rec}: {note}");
        });

    /// <summary>
    /// Fixture-only: stores an output of another kind (patch, data or image) as the task's next revision.
    /// Text, patch and data keep their content in the row; an image's bytes go to a file next to the database, named by the output ID.
    /// </summary>
    public static CommandResult AddFixtureOutput(string workspace, string commandId, string taskId, string kind, byte[] content, string author, Func<string> newId) =>
        Run(workspace, commandId, "output_proposed", db =>
        {
            if (kind is not ("text" or "patch" or "data" or "image"))
                throw new StoreException("unknown_kind", $"“{kind}” is not an output kind.", "Use text, patch, data or image.");
            var t = Task(db, taskId);
            int n = (Scalar<int?>(db, "SELECT max(revision) FROM outputs WHERE task_id=$t", ("$t", taskId)) ?? 0) + 1;
            string id = newId(), run = newId();
            string? blob = null, text = "";
            if (kind == "image")
            {
                blob = "blobs/" + id;
                string full = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ProjectStore.DatabaseIn(workspace)))!, blob);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, content);
            }
            else text = System.Text.Encoding.UTF8.GetString(content);
            Exec(db, "INSERT INTO runs(id,task_id,role,created_at) VALUES($r,$t,'builder',$at)", ("$r", run), ("$t", taskId), ("$at", DateTime.UtcNow.ToString("O")));
            Exec(db, "INSERT INTO outputs(id,task_id,revision,text,author_role,accepted_by,created_at,run_id,content_hash,kind,blob_path,byte_size) VALUES($i,$t,$n,$x,$a,NULL,$at,$r,$h,$k,$b,$z)",
                ("$i", id), ("$t", taskId), ("$n", n), ("$x", text), ("$a", author), ("$at", DateTime.UtcNow.ToString("O")), ("$r", run),
                ("$h", ProjectStore.Hash(content)), ("$k", kind), ("$b", blob), ("$z", content.Length));
            return new(id, n, taskId, null, null, $"{kind} output, revision {n}");
        });

    private static string CleanSpeech(string? text, string what)
    {
        text = text?.Trim() ?? "";
        if (text.Length is 0 or > 2000 || text.Any(c => char.IsControl(c) && c != '\n'))
            throw new StoreException("invalid_text", $"{what} needs 1 to 2000 characters.", "Shorten it and remove control characters.");
        return text;
    }

    /// <summary>
    /// Fixture-only: the offline builder says something without producing an output. A claim says the work is done (it is not an acceptance),
    /// progress is a transient update, and a question is put to the owner with its own durable ID.
    /// </summary>
    public static CommandResult FixtureSay(string workspace, string commandId, string taskId, string kind, string text, Func<string> newId) =>
        Run(workspace, commandId, kind switch { "claim" => "agent_claim", "progress" => "agent_progress", "question" => "question_asked", _ => "unknown" }, db =>
        {
            if (kind is not ("claim" or "progress" or "question"))
                throw new StoreException("unknown_kind", $"“{kind}” is not something a fixture can say.", "Use claim, progress or question.");
            var t = Task(db, taskId);
            text = CleanSpeech(text, "A message");
            string run = newId();
            Exec(db, "INSERT INTO runs(id,task_id,role,created_at) VALUES($r,$t,'builder',$at)", ("$r", run), ("$t", taskId), ("$at", DateTime.UtcNow.ToString("O")));
            if (kind != "question") return new(run, t.Revision, taskId, null, null, text);
            string id = newId();
            Exec(db, "INSERT INTO questions(id,project_id,task_id,run_id,asked_by,text,created_at) VALUES($i,$p,$t,$r,'builder (fixture)',$x,$at)",
                ("$i", id), ("$p", t.ProjectId), ("$t", taskId), ("$r", run), ("$x", text), ("$at", DateTime.UtcNow.ToString("O")));
            return new(id, t.Revision, taskId, null, null, text);
        });

    /// <summary>The owner answers a question once. The same command ID returns the first answer; a second answer under another ID is refused.</summary>
    public static CommandResult AnswerQuestion(string workspace, string commandId, string questionId, string text, string actor) =>
        Run(workspace, commandId, "question_answered", db =>
        {
            if (actor != "owner") throw new StoreException("not_authorized", $"{actor} cannot answer a question.", "Only the workspace owner answers.");
            text = CleanSpeech(text, "An answer");
            var q = db.CreateCommand();
            q.CommandText = "SELECT task_id, answer FROM questions WHERE id=$i";
            q.Parameters.AddWithValue("$i", questionId);
            string taskId; string? answered;
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) throw new StoreException("not_found", "That question no longer exists.", "Reload the board.");
                (taskId, answered) = (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
            }
            if (answered != null) throw new StoreException("already_answered", "That question was already answered.", "Reload to read the answer.");
            Exec(db, "UPDATE questions SET answer=$a, answered_by=$b, answered_at=$at, answer_command_id=$c WHERE id=$i",
                ("$a", text), ("$b", actor), ("$at", DateTime.UtcNow.ToString("O")), ("$c", commandId), ("$i", questionId));
            return new(questionId, Task(db, taskId).Revision, taskId, null, null, text);
        });

    private static readonly string[] Policies = ["human", "reviewer"];

    /// <summary>Review-gated tasks whose latest output has no decision yet.</summary>
    private static long OpenDecisions(SqliteConnection db) => Scalar<long>(db, """
        SELECT count(*) FROM tasks t JOIN outputs o ON o.task_id=t.id AND o.revision=(SELECT max(revision) FROM outputs WHERE task_id=t.id)
        WHERE t.review_required=1 AND o.accepted_by IS NULL AND NOT EXISTS (SELECT 1 FROM decisions d WHERE d.output_id=o.id)
        """);

    /// <summary>Only the workspace owner changes the policy, and not while a decision is waiting.</summary>
    public static CommandResult SetPolicy(string workspace, string commandId, string name, int expectedRevision, string actor) =>
        Run(workspace, commandId, "policy_changed", db =>
        {
            if (actor != "owner")
                throw new StoreException("not_authorized", $"{actor} cannot change the review policy.", "Only the workspace owner can change it.");
            if (!Policies.Contains(name)) throw new StoreException("invalid_policy", $"“{name}” is not a review policy.", "Choose human acceptance or reviewer approval.");
            int current = Scalar<int>(db, "SELECT revision FROM policy WHERE id=1");
            if (current != expectedRevision)
                throw new StoreException("conflict", $"The review policy changed (now revision {current}, you had {expectedRevision}).", "Reload the board, then choose again.");
            if (OpenDecisions(db) > 0)
                throw new StoreException("decision_open", "A decision is waiting, so the policy cannot change under it.", "Accept or request changes first, then change the policy.");
            Exec(db, "UPDATE policy SET name=$n, revision=revision+1 WHERE id=1", ("$n", name));
            return new("policy", current + 1, "", null, null, name);
        });

    /// <summary>One decision on one exact revision, committed with its event and its effect on the task.</summary>
    public static CommandResult DecideOutput(string workspace, string submissionId, string taskId, string outputId, int contentRevision, int policyRevision,
                                             string decision, string? comment, string actor, Func<string> newId) =>
        Run(workspace, submissionId, decision == "accepted" ? "output_accepted" : "changes_requested", db =>
        {
            if (decision is not ("accepted" or "changes_requested"))
                throw new StoreException("invalid_decision", $"“{decision}” is not a decision.", "Accept or request changes.");
            var t = Task(db, taskId);
            if (!t.ReviewRequired) throw new StoreException("not_review_gated", $"“{t.Title}” does not need a review decision.", "Turn on “Require review” for this task first.");
            string? runId; int revision; string? acceptedBy;
            using (var c = db.CreateCommand())
            {
                c.CommandText = "SELECT id, revision, run_id, accepted_by FROM outputs WHERE id=$o AND task_id=$t";
                c.Parameters.AddWithValue("$o", outputId);
                c.Parameters.AddWithValue("$t", taskId);
                using var r = c.ExecuteReader();
                if (!r.Read()) throw new StoreException("not_found", "That output no longer exists.", "Reload the board.");
                (revision, runId, acceptedBy) = (r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
            }
            int latest = Scalar<int>(db, "SELECT max(revision) FROM outputs WHERE task_id=$t", ("$t", taskId));
            if (revision != latest || contentRevision != revision)
                throw new StoreException("stale_revision", $"Revision {contentRevision} is no longer the current draft; revision {latest} is waiting for review.",
                    $"Open revision {latest} and decide on that one.");
            int policyNow = Scalar<int>(db, "SELECT revision FROM policy WHERE id=1");
            string policy = Scalar<string>(db, "SELECT name FROM policy WHERE id=1")!;
            if (policyNow != policyRevision)
                throw new StoreException("policy_changed", "The review policy changed while this decision was open.", "Reload the board and decide again under the new rule.");
            bool reviewerApproved = actor.StartsWith("reviewer") && decision == "accepted" && Scalar<string>(db,
                "SELECT recommendation FROM reviews WHERE output_id=$o ORDER BY rowid DESC LIMIT 1", ("$o", outputId)) == "approve_recommended";
            if (!(actor == "owner" || (policy == "reviewer" && reviewerApproved)))
                throw new StoreException("not_authorized",
                    actor.StartsWith("builder") ? "The author of a draft cannot decide on it." : $"Under {(policy == "human" ? "human acceptance" : "reviewer approval")}, {actor} cannot make this decision.",
                    policy == "human" ? "Only the workspace owner decides. Agents can recommend." : "A reviewer can accept only after recommending approval.");
            if (acceptedBy != null || Scalar<long>(db, "SELECT count(*) FROM decisions WHERE output_id=$o", ("$o", outputId)) > 0)
                throw new StoreException("already_decided", $"Revision {revision} already has a decision.", "Ask the builder for a new revision if more work is needed.");

            string id = newId();
            Exec(db, "INSERT INTO decisions VALUES($i,$t,$r,$o,$cr,$pr,$a,$d,$c,$s,$at)", ("$i", id), ("$t", taskId), ("$r", runId), ("$o", outputId), ("$cr", revision),
                ("$pr", policyNow), ("$a", actor), ("$d", decision), ("$c", comment), ("$s", submissionId), ("$at", DateTime.UtcNow.ToString("O")));
            if (decision == "accepted")
            {
                Exec(db, "UPDATE outputs SET accepted_by=$a WHERE id=$o", ("$a", actor), ("$o", outputId));
                // Acceptance settles the output. The task completes only if it could be completed by a direct move: prerequisites still count.
                // A cancelled task stays cancelled: the acceptance is recorded, but only an explicit reopen brings the task back.
                if (t.Status == "cancelled")
                {
                    Bump(db, taskId);
                    return new(id, t.Revision + 1, taskId, t.Status, null, $"accepted revision {revision}; the task is cancelled, so it stays cancelled until reopened");
                }
                var waiting = OpenPrerequisites(db, taskId, t.ProjectId);
                if (waiting.Count == 0)
                {
                    Bump(db, taskId, ("$status", "done"));
                    return new(id, t.Revision + 1, taskId, t.Status, "done", $"accepted revision {revision}");
                }
                Bump(db, taskId);
                return new(id, t.Revision + 1, taskId, t.Status, null, $"accepted revision {revision}; waiting on {waiting[0].Title}");
            }
            Exec(db, "INSERT INTO reviews(id,output_id,recommendation,comment,reviewer_role,created_at,run_id) VALUES($i,$o,'request_changes',$c,$a,$at,NULL)",
                ("$i", newId()), ("$o", outputId), ("$c", string.IsNullOrWhiteSpace(comment) ? "Changes requested." : comment.Trim()), ("$a", actor), ("$at", DateTime.UtcNow.ToString("O")));
            return new(id, t.Revision, taskId, null, null, $"changes requested on revision {revision}");
        });

}
