using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalMind.AgentHub.Core;

public sealed record ReplayEvent(long Seq, string At, string Kind, string TaskId, string Title, string? From, string? To, int Schema, string Summary);
public sealed record ReplayTask(string Id, string Title, string Status, int Revisions, int? AcceptedRevision);
/// <summary>The project as it stood after one event. <see cref="StoppedAt"/> names an event the replay could not safely pass.</summary>
public sealed record ReplayState(long Seq, long Latest, IReadOnlyList<ReplayTask> Tasks, long? StoppedAt, string? StopReason, IReadOnlyList<string> Notes);

/// <summary>
/// Read-only history. State is rebuilt from the stored events and the immutable records they point at (outputs and decisions); nothing here writes, and nothing here
/// can reach a provider or a tool. An event kind this build does not know is skipped and reported, unless it says it is irreversible: then the replay stops at it.
/// </summary>
public static class Replay
{
    public const int KnownEventSchema = 2;

    /// <summary>The event kinds this build writes and understands, by exact name. A familiar prefix is not enough: a future "task_purge" is not a "task_moved".</summary>
    private static readonly HashSet<string> KnownKinds =
    [
        "task_created", "task_updated", "task_moved", "dependency_added", "dependency_removed", "policy_changed", "output_proposed", "review_recorded", "output_accepted", "changes_requested",
        "question_asked", "question_answered", "agent_claim", "agent_progress", "file_supplied", "instruction_sent", "smoke_test", "role_assigned", "project_settings_changed", "price_recorded",
        "consent_granted", "consent_revoked", "connection_added", "connection_removed", "connection_probed", "credential_stored", "credential_rotated", "credential_deleted",
        "attempt_started", "attempt_finished", "attempt_failed", "attempt_interrupted", "attempt_stopped", "attempt_cancelled", "attempt_paused", "attempt_queued", "attempt_queue_dropped",
        "attempt_resolved", "attempt_reconciled", "plan_proposed", "plan_edited", "plan_dismissed", "plan_approved", "document_added", "document_edited", "document_removed", "document_forgotten", "documents_asked",
    ];
    public static bool Known(string kind) => KnownKinds.Contains(kind);

    private static string Summary(string kind, string? from, string? to, string title) => kind switch
    {
        "task_created" => $"“{title}” was added.",
        "task_moved" => $"“{title}” moved from {from?.Replace('_', ' ')} to {to?.Replace('_', ' ')}.",
        "output_proposed" => $"A draft of “{title}” was submitted.",
        "output_accepted" => $"The owner accepted a revision of “{title}”.",
        "changes_requested" => $"The owner asked for changes to “{title}”.",
        "review_recorded" => $"A review of “{title}” was recorded.",
        _ => $"“{title}”: {kind.Replace('_', ' ')}.",
    };

    public static IReadOnlyList<ReplayEvent> Timeline(string workspace, string projectId)
    {
        using var db = Reader.Open(workspace);
        return [.. Reader.Rows(db, """
            SELECT e.seq, e.created_at, e.kind, e.task_id, t.title, e.from_state, e.to_state, e.event_schema
            FROM events e JOIN tasks t ON t.id=e.task_id WHERE t.project_id=$p ORDER BY e.seq
            """, ("$p", projectId)).Select(r => new ReplayEvent(Convert.ToInt64(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.S(r[4]), r[5] as string, r[6] as string, Reader.I(r[7]),
                Summary(Reader.S(r[2]), r[5] as string, r[6] as string, Reader.S(r[4]))))];
    }

    private static bool Irreversible(string? detail)
    {
        try { return JsonNode.Parse(detail ?? "{}")?["irreversible"]?.GetValue<bool>() == true; }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    private static string? DetailId(string? detail)
    {
        try { return JsonNode.Parse(detail ?? "{}")?["id"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    private static string? DetailText(string? detail)
    {
        try { return JsonNode.Parse(detail ?? "{}")?["detail"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The state after event <paramref name="seq"/> (or the newest event when it is larger). If an event this build cannot safely pass is reached, the state is the one just before it:
    /// <see cref="ReplayState.Seq"/> is the last event actually applied, tasks created after it are absent, and titles are those recorded up to it.
    /// </summary>
    public static ReplayState StateAt(string workspace, string projectId, long seq)
    {
        using var db = Reader.Open(workspace);
        long latest = TaskStore.Scalar<long?>(db, "SELECT max(e.seq) FROM events e JOIN tasks t ON t.id=e.task_id WHERE t.project_id=$p", ("$p", projectId)) ?? 0;
        var tasks = Reader.Rows(db, "SELECT id, title FROM tasks WHERE project_id=$p ORDER BY created_at, id", ("$p", projectId)).Select(r => (Id: Reader.S(r[0]), Current: Reader.S(r[1]))).ToList();
        var status = tasks.ToDictionary(t => t.Id, t => "open");
        var revisions = tasks.ToDictionary(t => t.Id, _ => 0);
        var accepted = tasks.ToDictionary(t => t.Id, _ => (int?)null);
        var titles = new Dictionary<string, string>();       // a title only once an event has recorded it
        var created = new Dictionary<string, long>();         // tasks with a creation event applied
        var everCreated = Reader.Rows(db, "SELECT DISTINCT e.task_id FROM events e JOIN tasks t ON t.id=e.task_id WHERE t.project_id=$p AND e.kind='task_created'", ("$p", projectId)).Select(r => Reader.S(r[0])).ToHashSet();
        List<string> notes = [];
        long? stopped = null; string? why = null; long applied = 0;
        long limit = Math.Min(seq, latest);
        foreach (var e in Reader.Rows(db, """
            SELECT e.seq, e.kind, e.task_id, e.from_state, e.to_state, e.detail, e.event_schema FROM events e JOIN tasks t ON t.id=e.task_id
            WHERE t.project_id=$p AND e.seq<=$s ORDER BY e.seq
            """, ("$p", projectId), ("$s", limit)))
        {
            long n = Convert.ToInt64(e[0]);
            string kind = Reader.S(e[1]), task = Reader.S(e[2]);
            int schema = Reader.I(e[6]);
            if (!Known(kind) || schema > KnownEventSchema)
            {
                if (Irreversible(e[5] as string)) { stopped = n; why = $"Event {n} ({kind}, schema {schema}) is irreversible and this build does not understand it, so the replay stops at event {applied}, the last one it can show."; break; }
                notes.Add($"Event {n} ({kind}, schema {schema}) is not understood and was skipped.");
                applied = n;
                continue;
            }
            applied = n;
            if (!status.ContainsKey(task)) continue;
            string? text = DetailText(e[5] as string);
            if (kind == "task_created") { created[task] = n; if (text?.StartsWith("title=") == true) titles[task] = text[6..]; }
            else if (kind == "task_updated" && text?.StartsWith("title=") == true) titles[task] = text[6..];
            else if (kind == "task_moved" && e[4] is string to) status[task] = to;
            else if (kind == "output_proposed") revisions[task]++;
            else if (kind == "output_accepted")
            {
                if (e[4] is string done) status[task] = done;
                if (DetailId(e[5] as string) is { } did && TaskStore.Scalar<int?>(db, "SELECT content_revision FROM decisions WHERE id=$i", ("$i", did)) is { } rev) accepted[task] = rev;
            }
        }
        var shown = tasks.Where(t => !everCreated.Contains(t.Id) || created.ContainsKey(t.Id)).ToList(); // a task whose creation was not reached is not there yet
        bool guessed = false;
        var rows = shown.Select(t =>
        {
            bool known = titles.TryGetValue(t.Id, out var title);
            guessed |= !known;
            return new ReplayTask(t.Id, known ? title! : t.Current, status[t.Id], revisions[t.Id], accepted[t.Id]);
        }).ToList();
        if (guessed) notes.Add("Some titles were not recorded in the history, so the current title is shown for them; a later rename may not be reflected.");
        return new(stopped != null ? applied : limit, latest, rows, stopped, why, notes);
    }

    /// <summary>
    /// Every event as one JSON line. The stored detail is embedded exactly as written, so a field this build does not know about travels through
    /// an export unchanged.
    /// </summary>
    public static string ExportEvents(string workspace)
    {
        using var db = Reader.Open(workspace);
        var sb = new System.Text.StringBuilder();
        foreach (var e in Reader.Rows(db, "SELECT seq, created_at, kind, task_id, from_state, to_state, event_schema, command_id, detail FROM events ORDER BY seq"))
        {
            JsonNode? detail;
            try { detail = JsonNode.Parse(Reader.S(e[8])); } catch (JsonException) { detail = JsonValue.Create(Reader.S(e[8])); }
            sb.AppendLine(new JsonObject
            {
                ["seq"] = Convert.ToInt64(e[0]), ["at"] = Reader.S(e[1]), ["kind"] = Reader.S(e[2]), ["task"] = Reader.S(e[3]), ["from"] = e[4] as string, ["to"] = e[5] as string,
                ["schema"] = Reader.I(e[6]), ["command"] = Reader.S(e[7]), ["detail"] = detail,
            }.ToJsonString());
        }
        return sb.ToString();
    }
}
