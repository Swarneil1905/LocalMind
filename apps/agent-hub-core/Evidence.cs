using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

// Read-only views over a workspace: output inspection and comparison, handoff packets, the team channel and the decision inbox.
// Nothing here writes, so opening a preview, a diff or a packet can never create a review decision.

public static class Limits
{
    public const int MaxTextChars = 100_000;
    public const int MaxDiffLines = 2_000;
    public const int MaxShownDiffLines = 400;
    public const long MaxImageBytes = 1_048_576;
    public const int MaxImageSide = 4_096;
    public const int DefaultPacketTokens = 800;
    public const int MaxPage = 200;
}

internal static class Reader
{
    public static SqliteConnection Open(string workspace)
    {
        string path = ProjectStore.DatabaseIn(workspace);
        if (!File.Exists(path)) throw new StoreException("not_found", $"No workspace database at {path}.", "Open the workspace again.");
        var db = ProjectStore.Connect(path, SqliteOpenMode.ReadOnly);
        db.Open();
        if (ProjectStore.ReadVersion(db) != ProjectStore.CurrentSchema)
        {
            db.Dispose();
            throw new StoreException("store_outdated", "This workspace has not been upgraded yet.", "Open the workspace again so it can be upgraded.");
        }
        return db;
    }

    public static List<object?[]> Rows(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = c.ExecuteReader();
        List<object?[]> rows = [];
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public static string S(object? v) => v?.ToString() ?? "";
    public static int I(object? v) => v is null ? 0 : Convert.ToInt32(v);
    public static string Dir(string workspace) => Path.GetDirectoryName(Path.GetFullPath(ProjectStore.DatabaseIn(workspace)))!;
    public static int Tokens(string text) => (text.Length + 3) / 4;
}

// --- Inspecting and comparing outputs ------------------------------------------------------------------------------

/// <summary>What one stored output shows. Integrity says whether the content can be trusted; when it cannot, no content is returned.</summary>
public sealed record OutputView(string OutputId, int Revision, string Kind, string Hash, long Size, string Integrity, string Note,
                                string? Text, bool Truncated, byte[]? Image, int Width, int Height)
{
    public bool Verified => Integrity == "verified";
}

public sealed record DiffLine(char Op, string Text);

public sealed record OutputDiff(string Kind, bool Unchanged, string Summary, IReadOnlyList<DiffLine> Lines, bool Truncated, IReadOnlyList<string> ChangedSections,
                                bool OutsidePreview = false);

public static class Outputs
{
    public static OutputView Inspect(string workspace, string outputId)
    {
        using var db = Reader.Open(workspace);
        var row = Reader.Rows(db, "SELECT revision, kind, content_hash, byte_size, text, blob_path FROM outputs WHERE id=$i", ("$i", outputId)).FirstOrDefault()
            ?? throw new StoreException("not_found", "That output no longer exists.", "Reload the board.");
        int revision = Reader.I(row[0]);
        string kind = Reader.S(row[1]), recorded = Reader.S(row[2]);
        long size = Convert.ToInt64(row[3] ?? 0);
        OutputView Bad(string integrity, string note) => new(outputId, revision, kind, recorded, size, integrity, note, null, false, null, 0, 0);

        if (kind == "image")
        {
            string rel = Reader.S(row[5]);
            string full = Path.GetFullPath(Path.Combine(Reader.Dir(workspace), rel));
            if (rel.Length == 0 || !full.StartsWith(Reader.Dir(workspace) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return Bad("missing_blob", "The stored image file is missing, so nothing can be shown. The recorded hash is kept for comparison.");
            if (new FileInfo(full).Length > Limits.MaxImageBytes)
                return Bad("oversized_image", $"The image is larger than the {Limits.MaxImageBytes / 1024} KB preview limit, so it was not opened.");
            byte[] bytes = File.ReadAllBytes(full);
            if (ProjectStore.Hash(bytes) != recorded)
                return Bad("hash_mismatch", "The stored image does not match the hash recorded when it was saved, so it was not shown.");
            if (!TryPngSize(bytes, out int w, out int h))
                return Bad("malformed_image", "The file is not a readable PNG image, so it was not shown.");
            if (w is < 1 or > Limits.MaxImageSide || h is < 1 or > Limits.MaxImageSide)
                return Bad("oversized_image", $"The image is {w}×{h} pixels; previews stop at {Limits.MaxImageSide}×{Limits.MaxImageSide}.");
            return new(outputId, revision, kind, recorded, size, "verified", $"{w}×{h} pixel image · hash {recorded[..12]}", null, false, bytes, w, h);
        }

        string text = Reader.S(row[4]);
        if (ProjectStore.Hash(Encoding.UTF8.GetBytes(text)) != recorded)
            return Bad("hash_mismatch", "The stored content does not match the hash recorded when it was saved, so it was not shown.");
        string note = $"{kind} · {size} bytes · hash {recorded[..12]}";
        if (kind == "data")
        {
            try { text = JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
            catch (JsonException) { return new(outputId, revision, kind, recorded, size, "malformed_data", "The data is not valid JSON. It is shown as plain text.", Clip(text, out bool t1), t1, null, 0, 0); }
        }
        return new(outputId, revision, kind, recorded, size, "verified", note, Clip(text, out bool clipped), clipped, null, 0, 0);
    }

    private static string Clip(string text, out bool clipped)
    {
        clipped = text.Length > Limits.MaxTextChars;
        return clipped ? text[..Limits.MaxTextChars] : text;
    }

    private static bool TryPngSize(byte[] b, out int w, out int h)
    {
        (w, h) = (0, 0);
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (b.Length < 33 || !b.AsSpan(0, 8).SequenceEqual(sig) || Encoding.ASCII.GetString(b, 12, 4) != "IHDR") return false;
        w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        return true;
    }

    /// <summary>Compares two outputs of one task. Opening this never records a decision.</summary>
    public static OutputDiff Compare(string workspace, string fromId, string toId)
    {
        var a = Inspect(workspace, fromId);
        var b = Inspect(workspace, toId);
        if (!a.Verified || !b.Verified)
        {
            var bad = a.Verified ? b : a;
            return new("unavailable", false, $"Revision {bad.Revision} cannot be compared: {bad.Note}", [], false, []);
        }
        if (a.Hash == b.Hash) return new(b.Kind, true, $"Revision {b.Revision} is identical to revision {a.Revision}.", [], false, []);
        if (a.Kind == "image" || b.Kind == "image")
            return new("image", false, $"The image changed: revision {a.Revision} is {a.Width}×{a.Height}, revision {b.Revision} is {b.Width}×{b.Height}.", [], false, []);

        var left = a.Text!.Split('\n');
        var right = b.Text!.Split('\n');
        bool cut = left.Length > Limits.MaxDiffLines || right.Length > Limits.MaxDiffLines;
        if (cut) { left = left[..Math.Min(left.Length, Limits.MaxDiffLines)]; right = right[..Math.Min(right.Length, Limits.MaxDiffLines)]; }
        var lines = Diff(left, right);
        int added = lines.Count(l => l.Op == '+'), removed = lines.Count(l => l.Op == '-');
        var changed = ChangedSections(a.Kind, a.Text, b.Text);
        var shown = lines.Where(l => l.Op != ' ').Take(Limits.MaxShownDiffLines).ToList();
        // The hashes differ. When either side was clipped for display, the difference may lie beyond what was read.
        bool clipped = a.Truncated || b.Truncated || cut;
        bool truncated = cut || clipped || lines.Count(l => l.Op != ' ') > shown.Count;
        string kind = b.Kind;
        string what = $"Revision {b.Revision} against revision {a.Revision}: {added} line{(added == 1 ? "" : "s")} added, {removed} removed"
            + (changed.Count > 0 ? $". Changed: {string.Join(", ", changed)}" : "");
        if (clipped)
        {
            string beyond = "Some content differs outside the displayed portion"
                + (a.Truncated || b.Truncated ? $" (only the first {Limits.MaxTextChars:N0} characters of a revision are read)" : $" (only the first {Limits.MaxDiffLines:N0} lines are compared)");
            what = added + removed == 0 ? $"Revision {b.Revision} differs from revision {a.Revision}, but not within the displayed portion. {beyond}" : what + ". " + beyond;
        }
        else if (truncated) what += ". Only part of the change is shown";
        return new(kind, false, what + ".", shown, truncated, changed, clipped);
    }

    /// <summary>Line diff by longest common subsequence.</summary>
    internal static List<DiffLine> Diff(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        List<DiffLine> result = [];
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add(new(' ', a[x])); x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) result.Add(new('-', a[x++]));
            else result.Add(new('+', b[y++]));
        }
        while (x < a.Length) result.Add(new('-', a[x++]));
        while (y < b.Length) result.Add(new('+', b[y++]));
        return result;
    }

    /// <summary>Patch: the files it touches. Prose: the labelled sections ("Summary:", "Goal:") whose text differs. The first line is a title and is not a section.</summary>
    internal static List<string> ChangedSections(string kind, string before, string after)
    {
        if (kind == "patch")
        {
            static HashSet<string> Files(string t) => [.. t.Split('\n').Where(l => l.StartsWith("+++ ")).Select(l => l[4..].Trim().TrimStart('b', '/'))];
            var changed = Files(after);
            changed.UnionWith(Files(before));
            return [.. changed.Order()];
        }
        static Dictionary<string, string> Sections(string t)
        {
            Dictionary<string, string> map = [];
            foreach (var line in t.Split('\n').Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && colon < 40) map[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            return map;
        }
        var was = Sections(before);
        var now = Sections(after);
        return [.. now.Keys.Union(was.Keys).Where(k => !was.TryGetValue(k, out var w) || !now.TryGetValue(k, out var n) || w != n)
            .Select(k => !was.ContainsKey(k) ? $"{k} (added)" : !now.ContainsKey(k) ? $"{k} (removed)" : $"{k} (changed)")];
    }
}

// --- Acceptance: what it would do, and who is waiting for a decision ------------------------------------------------

public sealed record InboxItem(string ProjectId, string ProjectName, string TaskId, string TaskTitle, string TaskStatus, string OutputId, int Revision,
                               string Author, string CreatedAt, IReadOnlyList<string> Effects);

public static class Decisions
{
    private sealed record T(string Id, string Title, string Status, bool Gated, bool Accepted);

    /// <summary>One sentence per consequence of accepting the task's latest output, using the same completion rule as the worker.</summary>
    public static IReadOnlyList<string> Effects(string workspace, string taskId)
    {
        using var db = Reader.Open(workspace);
        return Effects(db, taskId);
    }

    internal static List<string> Effects(SqliteConnection db, string taskId)
    {
        var self = Reader.Rows(db, "SELECT project_id FROM tasks WHERE id=$t", ("$t", taskId)).FirstOrDefault() ?? throw new StoreException("not_found", "That task no longer exists.", "Reload the board.");
        string project = Reader.S(self[0]);
        var tasks = Reader.Rows(db, """
            SELECT t.id, t.title, t.status, t.review_required, COALESCE((SELECT accepted_by FROM outputs WHERE task_id=t.id ORDER BY revision DESC LIMIT 1),'')<>''
            FROM tasks t WHERE t.project_id=$p
            """, ("$p", project)).ToDictionary(r => Reader.S(r[0]), r => new T(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.I(r[3]) != 0, Convert.ToInt64(r[4]) != 0));
        var edges = Reader.Rows(db, "SELECT e.task_id, e.requires_id FROM hub_dependencies e JOIN tasks t ON t.id=e.task_id WHERE t.project_id=$p", ("$p", project));
        List<T> Needs(string id) => [.. edges.Where(e => Reader.S(e[0]) == id).Select(e => Reader.S(e[1])).Where(tasks.ContainsKey).Select(k => tasks[k]).OrderBy(x => x.Title).ThenBy(x => x.Id)];
        bool Satisfied(T t, bool assumeAccepted) => t.Status == "done" && (!t.Gated || t.Accepted || assumeAccepted);

        var me = tasks[taskId];
        var open = Needs(taskId).Where(p => !Satisfied(p, false)).ToList();
        List<string> lines = [];
        bool willBeDone;
        if (me.Status == "cancelled") { lines.Add($"“{me.Title}” stays Cancelled. Accepting records your decision only; reopen the task to continue."); willBeDone = false; }
        else if (open.Count > 0) { lines.Add($"“{me.Title}” stays {me.Status.Replace('_', ' ')}, waiting on “{open[0].Title}”" + (open.Count > 1 ? $" and {open.Count - 1} more." : ".")); willBeDone = false; }
        else { lines.Add($"“{me.Title}” will be marked Done."); willBeDone = true; }

        foreach (var d in tasks.Values.Where(d => d.Status is "open" or "in_progress" && edges.Any(e => Reader.S(e[0]) == d.Id && Reader.S(e[1]) == taskId)).OrderBy(d => d.Title))
        {
            var others = Needs(d.Id).Where(p => p.Id != taskId && !Satisfied(p, false)).ToList();
            if (willBeDone && others.Count == 0) lines.Add($"“{d.Title}” can then start.");
            else if (willBeDone) lines.Add($"“{d.Title}” still waits on “{others[0].Title}”.");
            else lines.Add($"“{d.Title}” keeps waiting on “{me.Title}”.");
        }
        return lines;
    }

    /// <summary>Every review-gated task, in every project of the workspace, whose latest output has no decision.</summary>
    public static IReadOnlyList<InboxItem> Inbox(string workspace)
    {
        using var db = Reader.Open(workspace);
        var rows = Reader.Rows(db, """
            SELECT p.id, p.name, t.id, t.title, t.status, o.id, o.revision, o.author_role, o.created_at
            FROM tasks t JOIN projects p ON p.id=t.project_id
            JOIN outputs o ON o.task_id=t.id AND o.revision=(SELECT max(revision) FROM outputs WHERE task_id=t.id)
            WHERE t.review_required=1 AND o.accepted_by IS NULL AND NOT EXISTS (SELECT 1 FROM decisions d WHERE d.output_id=o.id)
            ORDER BY o.created_at, o.id
            """);
        return [.. rows.Select(r => new InboxItem(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.S(r[4]), Reader.S(r[5]), Reader.I(r[6]), Reader.S(r[7]), Reader.S(r[8]),
            Effects(db, Reader.S(r[2]))))];
    }
}

// --- The team channel ---------------------------------------------------------------------------------------------

/// <summary>One persisted event as a channel message. Actor kind is "agent", "human" or "unrecorded" when the store never kept who did it.</summary>
public sealed record ChannelMessage(long Seq, string At, string ProjectId, string TaskId, string TaskTitle, string? RunId, string ActorKind, string Actor, string Kind, string Text, string? OutputId,
                                    string Class = "activity", string? QuestionId = null, string? DecisionId = null, int? Revision = null, string? Outcome = null)
{
    /// <summary>What the message is for. Decisions and questions need the owner; progress is transient; claims are an agent's say-so, never an acceptance.</summary>
    public bool NeedsAttention => Class is "decision" or "question";
}

public sealed record ChannelPage(IReadOnlyList<ChannelMessage> Messages, long Cursor, bool More);

/// <summary>The newest messages that match a filter, read from the event store, with how many matched and how many there are in all.</summary>
public sealed record FilteredPage(IReadOnlyList<ChannelMessage> Messages, int TotalMatching, int TotalAll);

public static class Channel
{
    /// <summary>Events of one project after a cursor, oldest first, at most <paramref name="limit"/>. Pass the returned cursor to continue; nothing repeats and nothing is skipped.</summary>
    public static ChannelPage Read(string workspace, string projectId, long after, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, Limits.MaxPage);
        using var db = Reader.Open(workspace);
        var events = Reader.Rows(db, """
            SELECT e.seq, e.created_at, e.task_id, t.title, e.kind, e.from_state, e.to_state, e.detail
            FROM events e JOIN tasks t ON t.id=e.task_id WHERE t.project_id=$p AND e.seq>$a ORDER BY e.seq LIMIT $n
            """, ("$p", projectId), ("$a", after), ("$n", limit + 1));
        bool more = events.Count > limit;
        List<ChannelMessage> messages = [];
        foreach (var e in events.Take(limit)) messages.Add(Describe(db, projectId, e));
        return new(messages, messages.Count > 0 ? messages[^1].Seq : after, more);
    }

    /// <summary>
    /// The newest <paramref name="limit"/> messages of a project that match "quiet" (everything but transient progress) or "attention" (decisions and questions),
    /// oldest first. It reads the store, not what a window kept, so an item older than the window's retained rows is still found.
    /// </summary>
    public static FilteredPage ReadMatching(string workspace, string projectId, string filter, int limit)
    {
        limit = Math.Clamp(limit, 1, 5000);
        string cond = filter == "quiet" ? "e.kind <> 'agent_progress'" : "e.kind IN ('output_accepted','changes_requested','question_asked','question_answered')";
        using var db = Reader.Open(workspace);
        var events = Reader.Rows(db, $"""
            SELECT e.seq, e.created_at, e.task_id, t.title, e.kind, e.from_state, e.to_state, e.detail
            FROM events e JOIN tasks t ON t.id=e.task_id WHERE t.project_id=$p AND {cond} ORDER BY e.seq DESC LIMIT $n
            """, ("$p", projectId), ("$n", limit));
        events.Reverse();
        long matching = Convert.ToInt64(Reader.Rows(db, $"SELECT count(*) FROM events e JOIN tasks t ON t.id=e.task_id WHERE t.project_id=$p AND {cond}", ("$p", projectId))[0][0]);
        long all = Convert.ToInt64(Reader.Rows(db, "SELECT count(*) FROM events e JOIN tasks t ON t.id=e.task_id WHERE t.project_id=$p", ("$p", projectId))[0][0]);
        return new([.. events.Select(r => Describe(db, projectId, r))], (int)matching, (int)all);
    }

    /// <summary>The newest event number in the workspace: a cheap way to learn that something new exists.</summary>
    public static long LatestSeq(string workspace)
    {
        using var db = Reader.Open(workspace);
        return Reader.Rows(db, "SELECT COALESCE(max(seq),0) FROM events")[0][0] is { } v ? Convert.ToInt64(v) : 0;
    }

    private static ChannelMessage Describe(SqliteConnection db, string project, object?[] e)
    {
        long seq = Convert.ToInt64(e[0]);
        string at = Reader.S(e[1]), task = Reader.S(e[2]), title = Reader.S(e[3]), kind = Reader.S(e[4]), from = Reader.S(e[5]), to = Reader.S(e[6]);
        string? recordId = null;
        try { using var d = JsonDocument.Parse(Reader.S(e[7])); foreach (var p in d.RootElement.EnumerateObject()) if (p.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) recordId = p.Value.GetString(); }
        catch (JsonException) { }
        ChannelMessage Make(string? run, string kindOf, string actor, string text, string? output = null, string cls = "work", string? question = null, string? decision = null, int? revision = null, string? outcome = null)
            => new(seq, at, project, task, title, run, kindOf, actor, kind, text, output, cls, question, decision, revision, outcome);
        ChannelMessage Plain(string text) => Make(null, "unrecorded", "Not recorded", text, cls: "activity");
        string detail = "";
        try { using var dd = JsonDocument.Parse(Reader.S(e[7])); if (dd.RootElement.TryGetProperty("detail", out var dv) && dv.ValueKind == JsonValueKind.String) detail = dv.GetString() ?? ""; }
        catch (JsonException) { }

        switch (kind)
        {
            case "output_proposed" when recordId != null && Reader.Rows(db, "SELECT revision, author_role, run_id, kind, content_hash, text FROM outputs WHERE id=$i", ("$i", recordId)).FirstOrDefault() is { } o:
            {
                string excerpt = Reader.S(o[3]) == "image" ? "(image)" : Reader.S(o[5]).Replace('\n', ' ');
                if (excerpt.Length > 160) excerpt = excerpt[..160] + "…";
                return Make(o[2] is null ? null : Reader.S(o[2]), "agent", Reader.S(o[1]),
                    $"Draft revision {Reader.I(o[0])} of “{title}” submitted for review. This is a draft, not an accepted output. {excerpt}", recordId);
            }
            case "review_recorded" when recordId != null && Reader.Rows(db, "SELECT r.recommendation, r.comment, r.reviewer_role, r.run_id, o.revision, o.id FROM reviews r JOIN outputs o ON o.id=r.output_id WHERE r.id=$i", ("$i", recordId)).FirstOrDefault() is { } r:
                return Make(r[3] is null ? null : Reader.S(r[3]), "agent", Reader.S(r[2]),
                    $"{(Reader.S(r[0]) == "approve_recommended" ? "Recommends approving" : "Recommends changes to")} revision {Reader.I(r[4])}: {Reader.S(r[1])} (a recommendation, not a decision)", Reader.S(r[5]));
            case "output_accepted" or "changes_requested" when recordId != null && Reader.Rows(db, "SELECT d.actor, d.comment, d.run_id, d.content_revision, d.decision, d.output_id FROM decisions d WHERE d.id=$i", ("$i", recordId)).FirstOrDefault() is { } d:
            {
                string actor = Reader.S(d[0]);
                string text = Reader.S(d[4]) == "accepted" ? $"Accepted revision {Reader.I(d[3])} of “{title}”." : $"Asked for changes to revision {Reader.I(d[3])} of “{title}”: {Reader.S(d[1])}";
                return Make(d[2] is null ? null : Reader.S(d[2]), actor == "owner" ? "human" : "agent", actor == "owner" ? "You (workspace owner)" : actor, text, Reader.S(d[5]),
                    "decision", null, recordId, Reader.I(d[3]), Reader.S(d[4]));
            }
            case "agent_claim" or "agent_progress":
            {
                var run = Reader.Rows(db, "SELECT role FROM runs WHERE id=$i", ("$i", recordId ?? "")).FirstOrDefault();
                string who = run == null ? "builder (fixture)" : Reader.S(run[0]) + " (fixture)";
                return Make(recordId, "agent", who, kind == "agent_claim" ? $"Claims: {detail} This is a claim, not an acceptance." : detail, null, kind == "agent_claim" ? "claim" : "progress");
            }
            case "question_asked" when recordId != null && Reader.Rows(db, "SELECT asked_by, text, run_id FROM questions WHERE id=$i", ("$i", recordId)).FirstOrDefault() is { } qa:
                return Make(qa[2] is null ? null : Reader.S(qa[2]), "agent", Reader.S(qa[0]), $"Asks you: {Reader.S(qa[1])}", null, "question", recordId);
            case "question_answered" when recordId != null && Reader.Rows(db, "SELECT text, answer FROM questions WHERE id=$i", ("$i", recordId)).FirstOrDefault() is { } qr:
                return Make(null, "human", "You (workspace owner)", $"Answered “{Reader.S(qr[0])}”: {Reader.S(qr[1])}", null, "question", recordId);
            case "attempt_started" when recordId != null && Reader.Rows(db, "SELECT a.role, a.requested_model, c.label, c.is_cloud, length(a.packet_text), a.run_id, a.instruction_id FROM attempts a JOIN connections c ON c.id=a.connection_id WHERE a.id=$i", ("$i", recordId)).FirstOrDefault() is { } st:
                return Make(Reader.S(st[5]), "human", "You (workspace owner)",
                    $"Sent the {Reader.S(st[0])} a packet of about {(Reader.I(st[4]) + 3) / 4} tokens: {Reader.S(st[1])} on {Reader.S(st[2])} ({(Reader.I(st[3]) != 0 ? "cloud" : "this computer")})" + (st[6] is null ? "." : ", with your instruction."), null, "work");
            case "attempt_failed" or "attempt_interrupted" or "attempt_stopped" or "attempt_cancelled" or "attempt_paused" or "attempt_finished" when recordId != null && Reader.Rows(db, "SELECT role, requested_model, status, stop_reason, run_id FROM attempts WHERE id=$i", ("$i", recordId)).FirstOrDefault() is { } af:
                return Make(Reader.S(af[4]), kind is "attempt_cancelled" or "attempt_paused" ? "human" : "agent", kind is "attempt_cancelled" or "attempt_paused" ? "You (workspace owner)" : $"{Reader.S(af[0])} · {Reader.S(af[1])}",
                    $"The {Reader.S(af[0])}'s attempt is {Reader.S(af[2]).Replace('_', ' ')}. {Reader.S(af[3])}", null, "work");
            case "instruction_sent" when recordId != null && Reader.Rows(db, "SELECT recipients, text FROM instructions WHERE id=$i", ("$i", recordId)).FirstOrDefault() is { } ins:
                return Make(null, "human", "You (workspace owner)", $"To {string.Join(", ", JsonSerializer.Deserialize<string[]>(Reader.S(ins[0])) ?? [])}: {Reader.S(ins[1])}", null, "question");
            case "task_moved": return Plain($"“{title}” moved from {from.Replace('_', ' ')} to {to.Replace('_', ' ')}.");
            case "task_created": return Plain($"“{title}” was added.");
            case "task_updated": return Plain($"“{title}” was edited.");
            case "dependency_added": return Plain($"A prerequisite was added to “{title}”.");
            case "dependency_removed": return Plain($"A prerequisite was removed from “{title}”.");
            case "policy_changed": return Plain("The review policy changed.");
            default: return Plain($"“{title}”: {kind.Replace('_', ' ')}.");
        }
    }
}

// --- Handoff packets -----------------------------------------------------------------------------------------------

public sealed record PacketSection(string Heading, string Body, int Tokens, string? SourceId, int? SourceRevision, bool Draft, string? FileName = null);

/// <summary>
/// What the next agent is given: this project's evidence only, inside a token limit, with everything left out named.
/// <see cref="Text"/> is the whole payload the recipient receives and is itself inside the limit. <see cref="Omitted"/> is the full report for the inspector.
/// Tokens are estimated at four characters each until a model-specific tokenizer exists.
/// </summary>
public sealed record Packet(string Role, string ProjectId, string ProjectName, string TaskId, string TaskTitle, int TokenLimit, int TokensUsed,
                            IReadOnlyList<PacketSection> Sections, IReadOnlyList<string> Omitted, string Text)
{
    public const string Estimator = "estimated at 4 characters per token";
}

public static class Handoff
{
    public static Packet Build(string workspace, string projectId, string taskId, string role, int tokenLimit = Limits.DefaultPacketTokens, string? instruction = null)
    {
        using var db = Reader.Open(workspace);
        return Build(db, projectId, taskId, role, tokenLimit, instruction);
    }

    /// <summary>The file names a packet admitted. Consent to a cloud destination must cover each of them.</summary>
    public static IReadOnlyList<string> FileNames(Packet p) => [.. p.Sections.Where(x => x.FileName != null).Select(x => x.FileName!)];

    internal static Packet Build(SqliteConnection db, string projectId, string taskId, string role, int tokenLimit, string? instruction = null)
    {
        if (role is not ("builder" or "reviewer")) throw new StoreException("unknown_role", $"There is no “{role}” fixture.", "Use builder or reviewer.");
        if (tokenLimit < 20) throw new StoreException("invalid_limit", "A packet needs a token limit of at least 20.", "Choose a larger limit.");
        var task = Reader.Rows(db, "SELECT t.title, t.outcome, p.name FROM tasks t JOIN projects p ON p.id=t.project_id WHERE t.id=$t AND t.project_id=$p", ("$t", taskId), ("$p", projectId)).FirstOrDefault()
            ?? throw new StoreException("not_found", "That task is not in this project.", "Select the task in its own project.");
        string title = Reader.S(task[0]), projectName = Reader.S(task[2]);
        int max = tokenLimit * 4; // characters

        List<PacketSection> candidates = [];
        List<string> unavailable = [];
        candidates.Add(new("Task and criteria", $"Task: {title}\nCriteria: {(task[1] is null ? "(none recorded)" : Reader.S(task[1]))}", 0, null, null, false));

        // A private instruction from the owner goes only into the packet of the role it was addressed to.
        if (!string.IsNullOrWhiteSpace(instruction))
            candidates.Add(new($"Instruction from the owner, to the {role} only", instruction.Trim(), 0, null, null, false));

        // Files supplied with the task, by name. Only admitted ones are sent, and their names are what consent covers.
        bool hasFilesTable = Reader.Rows(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='task_files'")[0][0] is long n && n > 0;
        var supplied = hasFilesTable ? Team.Files(db, taskId) : [];

        if (role == "builder")
        {
            // The reviewer's request for changes on the latest draft is an input: it is budgeted and recorded like any other, with its review ID and the revision it was about.
            var fb = Reader.Rows(db, """
                SELECT o.revision, r.id, r.recommendation, r.comment FROM outputs o JOIN reviews r ON r.output_id=o.id
                WHERE o.task_id=$t AND o.revision=(SELECT max(revision) FROM outputs WHERE task_id=$t) ORDER BY r.rowid DESC LIMIT 1
                """, ("$t", taskId)).FirstOrDefault();
            if (fb != null && Reader.S(fb[2]) == "request_changes")
                candidates.Add(new($"Reviewer feedback on revision {Reader.I(fb[0])}", Reader.S(fb[3]), 0, Reader.S(fb[1]), Reader.I(fb[0]), false));
        }

        if (role == "reviewer")
        {
            var latest = Reader.Rows(db, "SELECT id, revision, text, accepted_by, kind FROM outputs WHERE task_id=$t ORDER BY revision DESC LIMIT 1", ("$t", taskId)).FirstOrDefault();
            if (latest == null) unavailable.Add($"There is no draft of “{title}” to review yet.");
            else
            {
                string state = latest[3] is null ? "DRAFT, not accepted" : $"accepted by {Reader.S(latest[3])}";
                string body = Reader.S(latest[4]) == "image" ? "(image content is not included in a packet)" : Reader.S(latest[2]);
                candidates.Add(new($"Output under review · revision {Reader.I(latest[1])} · {state} · scope: review of “{title}” only", body, 0, Reader.S(latest[0]), Reader.I(latest[1]), latest[3] is null));
            }
        }

        foreach (var (name, content) in supplied)
            candidates.Add(new($"Supplied file “{name}”", content, 0, null, null, false, name));

        // The latest accepted output of each prerequisite in this project. Another project's task is never reachable from here.
        var upstream = Reader.Rows(db, """
            SELECT p.id, p.title, o.id, o.revision, o.text, o.kind FROM hub_dependencies e
            JOIN tasks p ON p.id=e.requires_id AND p.project_id=$proj
            LEFT JOIN outputs o ON o.task_id=p.id AND o.revision=(SELECT max(revision) FROM outputs WHERE task_id=p.id AND accepted_by IS NOT NULL)
            WHERE e.task_id=$t ORDER BY p.title, p.id
            """, ("$proj", projectId), ("$t", taskId));
        foreach (var u in upstream)
        {
            string name = Reader.S(u[1]);
            if (u[2] is null) { unavailable.Add($"“{name}” has no accepted output, so nothing from it is included."); continue; }
            string body = Reader.S(u[5]) == "image" ? "(image content is not included in a packet)" : Reader.S(u[4]);
            candidates.Add(new($"Accepted output of “{name}” · output {Reader.S(u[2])} · revision {Reader.I(u[3])}", body, 0, Reader.S(u[2]), Reader.I(u[3]), false));
        }

        string header = $"Project: {projectName}\nFor: {role}\n";
        const string marker = "\n[Some items were left out; the inspector lists them.]\n";
        static string Render(string header, IEnumerable<PacketSection> sections, bool omitted) =>
            header + string.Concat(sections.Select(s => $"\n## {s.Heading}\n{s.Body}\n")) + (omitted ? marker : "");

        List<PacketSection> chosen = [];
        List<string> omitted = [.. unavailable];
        // First try everything without a marker; if anything is left out, redo it with room kept for the marker.
        bool reserve = unavailable.Count > 0;
        for (int pass = 0; pass < 2; pass++)
        {
            chosen = [];
            omitted = [.. unavailable];
            foreach (var c in candidates)
            {
                if (chosen.Count == 0)
                {
                    // The task itself always goes in, cut to the limit if it has to be.
                    var fit = c;
                    string whole = Render(header, [fit], reserve);
                    if (whole.Length > max)
                    {
                        int room = Math.Max(0, max - Render(header, [fit with { Body = "" }], reserve).Length - 1);
                        fit = fit with { Heading = c.Heading + " (cut to the limit)", Body = c.Body[..Math.Min(c.Body.Length, room)] + "…" };
                        omitted.Add("The task and its criteria were cut to fit the limit; nothing else was included.");
                        chosen.Add(fit);
                        break;
                    }
                    chosen.Add(fit);
                    continue;
                }
                if (Render(header, [.. chosen, c], reserve).Length <= max) chosen.Add(c);
                else omitted.Add($"{c.Heading} (about {Reader.Tokens(c.Heading + c.Body)} tokens) did not fit the {tokenLimit}-token limit.");
            }
            if (reserve || omitted.Count == unavailable.Count) break;
            reserve = true;
        }
        bool anyLeftOut = omitted.Count > 0;
        string text = Render(header, chosen, anyLeftOut);
        if (text.Length > max) text = text[..(max - 1)] + "…"; // the header alone is over a tiny limit
        chosen = [.. chosen.Select(s => s with { Tokens = Reader.Tokens(s.Heading + s.Body) })];
        text = text.TrimEnd('\n');
        return new(role, projectId, projectName, taskId, title, tokenLimit, Reader.Tokens(text), chosen, omitted, text);
    }
}

// --- What a run was given ------------------------------------------------------------------------------------------

public sealed record RunPacket(string RunId, string Role, string? ProjectId, int? Limit, int? Tokens, string? Text, IReadOnlyList<(string Id, int Revision)> Sources)
{
    public bool Recorded => ProjectId != null;
}

public static class Runs
{
    /// <summary>The packet a fixture run consumed, or one with nothing recorded for runs from before packets existed.</summary>
    public static RunPacket Packet(string workspace, string runId)
    {
        using var db = Reader.Open(workspace);
        var r = Reader.Rows(db, "SELECT role, packet_project, packet_limit, packet_tokens, packet_text, packet_sources FROM runs WHERE id=$i", ("$i", runId)).FirstOrDefault()
            ?? throw new StoreException("not_found", "That run no longer exists.", "Reload the board.");
        List<(string, int)> sources = [];
        if (r[5] is string json)
            using (var d = JsonDocument.Parse(json))
                foreach (var e in d.RootElement.EnumerateArray()) sources.Add((e.GetProperty("id").GetString() ?? "", e.GetProperty("revision").GetInt32()));
        return new(runId, Reader.S(r[0]), r[1] as string, r[2] is null ? null : Reader.I(r[2]), r[3] is null ? null : Reader.I(r[3]), r[4] as string, sources);
    }
}

// --- Questions the agents put to the owner -------------------------------------------------------------------------

public sealed record QuestionItem(string Id, string ProjectId, string ProjectName, string TaskId, string TaskTitle, string AskedBy, string Text, string CreatedAt,
                                  string? Answer, string? AnsweredBy, string? AnsweredAt)
{
    public bool Open => Answer == null;
}

public static class Questions
{
    /// <summary>Every question in the workspace, oldest first. Open ones are the owner's to answer; answered ones keep their reply.</summary>
    public static IReadOnlyList<QuestionItem> All(string workspace)
    {
        using var db = Reader.Open(workspace);
        return [.. Reader.Rows(db, """
            SELECT q.id, q.project_id, p.name, q.task_id, t.title, q.asked_by, q.text, q.created_at, q.answer, q.answered_by, q.answered_at
            FROM questions q JOIN tasks t ON t.id=q.task_id JOIN projects p ON p.id=q.project_id ORDER BY q.created_at, q.id
            """).Select(r => new QuestionItem(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.S(r[4]), Reader.S(r[5]), Reader.S(r[6]), Reader.S(r[7]),
                r[8] as string, r[9] as string, r[10] as string))];
    }
}
