using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

public sealed record DocumentInfo(string Id, string ProjectId, string Name, string Kind, int Revision, string State, string CreatedAt)
{
    public string Label => $"{Name} · revision {Revision}" + (State == "current" ? "" : $" · {State}");
}

/// <summary>One cited span at the revision the answer used. <see cref="State"/> says whether that revision is still the current one.</summary>
public sealed record Citation(int N, string DocId, string DocName, int Revision, int Start, int Length, bool Cited, string Quote, string State);

public sealed record DocAnswer(string Id, string ProjectId, string Question, string Answer, string Status, string? Model, string? Destination, string CreatedAt, IReadOnlyList<Citation> Citations, IReadOnlyList<string>? InvalidRefs = null);

public sealed record PassageView(string DocId, string DocName, int Revision, int Start, int Length, string Before, string Text, string After, string State, string Note);

public sealed record ForgetReport(string DocId, string Name, int RevisionsErased, int IndexRowsErased, bool ThumbnailErased, int PacketsScrubbed, int AnswersScrubbed,
                                  IReadOnlyList<string> TransmittedCopies, IReadOnlyList<string> Remaining);

/// <summary>
/// Project documents the team can be asked about. Every document belongs to one project. Each edit is a new revision, so an old answer still opens the
/// passage it cited; removing a document makes it ineligible for new answers; forgetting it erases its content, index, thumbnail and reusable packets.
/// </summary>
public static class Documents
{
    private static string Now => DateTime.UtcNow.ToString("O");
    private static StoreException Bad(string code, string message, string recovery) => new(code, message, recovery);
    private static readonly HashSet<string> Stop = ["the", "and", "for", "are", "was", "were", "what", "which", "who", "how", "does", "did", "that", "this", "with", "from", "have", "has", "about", "into", "than", "then", "there", "their", "will", "would", "should", "could", "can"];

    public const int MaxChars = 200_000;

    public static IEnumerable<string> Terms(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Where(t => !Stop.Contains(t)).Select(t => t.Length > 3 && t.EndsWith('s') ? t[..^1] : t).Distinct(); // shortcut: plural "s" only, no real stemmer; upgrade when recall matters

    /// <summary>Paragraphs, with their offsets in the exact text, so a citation can name a span and nothing else.</summary>
    public static IReadOnlyList<(int Start, int Length)> Chunks(string text)
    {
        List<(int, int)> list = [];
        int start = 0;
        foreach (Match gap in Regex.Matches(text, @"(\r?\n){2,}").Append(null!))
        {
            int end = gap?.Index ?? text.Length;
            if (end > start && text.AsSpan(start, end - start).Trim().Length > 0) list.Add((start, end - start));
            if (gap != null) start = gap.Index + gap.Length;
        }
        return list;
    }

    private static void Index(SqliteConnection db, string docId, int revision, string text)
    {
        var chunks = Chunks(text);
        for (int i = 0; i < chunks.Count; i++)
        {
            TaskStore.Exec(db, "INSERT INTO doc_chunks VALUES($d,$r,$i,$s,$l)", ("$d", docId), ("$r", revision), ("$i", i), ("$s", chunks[i].Start), ("$l", chunks[i].Length));
            foreach (var t in Terms(text.Substring(chunks[i].Start, chunks[i].Length)))
                TaskStore.Exec(db, "INSERT INTO doc_terms VALUES($d,$r,$i,$t)", ("$d", docId), ("$r", revision), ("$i", i), ("$t", t));
        }
    }

    private static string Clean(string name)
    {
        name = Path.GetFileName(name.Trim());
        if (name.Length is 0 or > 120 || name.Any(char.IsControl)) throw Bad("invalid_name", "A document needs a name of 1 to 120 characters.", "Choose a shorter name.");
        return name;
    }

    public static CommandResult Add(string workspace, string commandId, string projectId, string name, string text, Func<string> newId) =>
        TaskStore.Run(workspace, commandId, "document_added", db =>
        {
            name = Clean(name);
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) throw Bad("invalid_document", $"A document needs 1 to {MaxChars:N0} characters of text.", "Shorten it or split it.");
            if (TaskStore.Scalar<long>(db, "SELECT count(*) FROM projects WHERE id=$p", ("$p", projectId)) == 0) throw Bad("not_found", "That project no longer exists.", "Reload the workspace.");
            string id = newId();
            TaskStore.Exec(db, "INSERT INTO documents(id,project_id,name,kind,created_at) VALUES($i,$p,$n,'text',$at)", ("$i", id), ("$p", projectId), ("$n", name), ("$at", Now));
            TaskStore.Exec(db, "INSERT INTO document_revisions(doc_id,revision,text,hash,created_at) VALUES($i,1,$t,$h,$at)", ("$i", id), ("$t", text), ("$h", ProjectStore.Hash(Encoding.UTF8.GetBytes(text))), ("$at", Now));
            Index(db, id, 1, text);
            return new(id, 1, id, null, null, $"“{name}” added");
        });

    /// <summary>An image kept as a source. Its thumbnail is stored separately, so forgetting it has something real to remove.</summary>
    public static CommandResult AddImage(string workspace, string commandId, string projectId, ImageData image, Func<string> newId) =>
        TaskStore.Run(workspace, commandId, "document_added", db =>
        {
            string id = newId();
            TaskStore.Exec(db, "INSERT INTO documents(id,project_id,name,kind,created_at) VALUES($i,$p,$n,'image',$at)", ("$i", id), ("$p", projectId), ("$n", Clean(image.Name)), ("$at", Now));
            TaskStore.Exec(db, "INSERT INTO document_revisions(doc_id,revision,text,hash,bytes,created_at) VALUES($i,1,NULL,$h,$b,$at)", ("$i", id), ("$h", image.Hash), ("$b", image.Bytes), ("$at", Now));
            TaskStore.Exec(db, "INSERT INTO doc_thumbs VALUES($i,$b)", ("$i", id), ("$b", image.Bytes));
            return new(id, 1, id, null, null, $"image “{image.Name}” added");
        });

    public static CommandResult Edit(string workspace, string commandId, string docId, string text) =>
        TaskStore.Run(workspace, commandId, "document_edited", db =>
        {
            var d = Reader.Rows(db, "SELECT name, kind, removed_at, forgotten_at FROM documents WHERE id=$i", ("$i", docId)).FirstOrDefault() ?? throw Bad("not_found", "That document no longer exists.", "Reload the list.");
            if (d[3] != null) throw Bad("forgotten", "That source was forgotten and cannot be edited.", "Add it again as a new document.");
            if (d[2] != null) throw Bad("removed", "That document was removed.", "Add it again as a new document.");
            if (Reader.S(d[1]) != "text") throw Bad("not_text", "An image has no text to edit.", "Add a new image instead.");
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) throw Bad("invalid_document", $"A document needs 1 to {MaxChars:N0} characters of text.", "Shorten it or split it.");
            int rev = (TaskStore.Scalar<int?>(db, "SELECT max(revision) FROM document_revisions WHERE doc_id=$i", ("$i", docId)) ?? 0) + 1;
            TaskStore.Exec(db, "INSERT INTO document_revisions(doc_id,revision,text,hash,created_at) VALUES($i,$r,$t,$h,$at)", ("$i", docId), ("$r", rev), ("$t", text), ("$h", ProjectStore.Hash(Encoding.UTF8.GetBytes(text))), ("$at", Now));
            Index(db, docId, rev, text);
            return new(docId, rev, docId, null, null, $"“{Reader.S(d[0])}” revision {rev}");
        });

    public static CommandResult Remove(string workspace, string commandId, string docId) =>
        TaskStore.Run(workspace, commandId, "document_removed", db =>
        {
            string name = TaskStore.Scalar<string>(db, "SELECT name FROM documents WHERE id=$i", ("$i", docId)) ?? throw Bad("not_found", "That document no longer exists.", "Reload the list.");
            TaskStore.Exec(db, "UPDATE documents SET removed_at=COALESCE(removed_at,$at) WHERE id=$i", ("$at", Now), ("$i", docId));
            return new(docId, 0, docId, null, null, $"“{name}” removed; new answers no longer use it");
        });

    public static IReadOnlyList<DocumentInfo> List(string workspace, string projectId)
    {
        using var db = Reader.Open(workspace);
        return [.. Reader.Rows(db, """
            SELECT d.id, d.project_id, d.name, d.kind, COALESCE((SELECT max(revision) FROM document_revisions WHERE doc_id=d.id),0), d.removed_at, d.forgotten_at, d.created_at
            FROM documents d WHERE d.project_id=$p ORDER BY d.created_at, d.id
            """, ("$p", projectId)).Select(r => new DocumentInfo(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.I(r[4]), r[6] != null ? "forgotten" : r[5] != null ? "removed" : "current", Reader.S(r[7])))];
    }

    // --- Retrieval ----------------------------------------------------------------------------------------------------
    internal sealed record Hit(string DocId, string Name, int Revision, int Start, int Length, string Text, int Score);

    /// <summary>The best passages from this project's current, eligible documents. A removed, forgotten or other-project document is never a candidate.</summary>
    internal static List<Hit> Retrieve(SqliteConnection db, string projectId, string question, int k = 3)
    {
        var terms = Terms(question).Take(24).ToList();
        if (terms.Count == 0) return [];
        string marks = string.Join(',', terms.Select((_, i) => "$t" + i));
        var args = terms.Select((t, i) => ("$t" + i, (object?)t)).Append(("$p", (object?)projectId)).ToArray();
        var rows = Reader.Rows(db, $"""
            SELECT c.doc_id, d.name, c.revision, c.start, c.length, count(DISTINCT t.term) AS score
            FROM doc_terms t
            JOIN documents d ON d.id=t.doc_id AND d.project_id=$p AND d.removed_at IS NULL AND d.forgotten_at IS NULL
            JOIN doc_chunks c ON c.doc_id=t.doc_id AND c.revision=t.revision AND c.idx=t.idx
            WHERE t.term IN ({marks}) AND t.revision=(SELECT max(revision) FROM document_revisions WHERE doc_id=d.id)
            GROUP BY c.doc_id, c.revision, c.idx ORDER BY score DESC, c.doc_id, c.idx LIMIT {k}
            """, args);
        List<Hit> hits = [];
        foreach (var r in rows)
        {
            string text = TaskStore.Scalar<string>(db, "SELECT text FROM document_revisions WHERE doc_id=$d AND revision=$r", ("$d", Reader.S(r[0])), ("$r", Reader.I(r[2]))) ?? "";
            int s = Reader.I(r[3]), l = Reader.I(r[4]);
            if (s + l <= text.Length) hits.Add(new(Reader.S(r[0]), Reader.S(r[1]), Reader.I(r[2]), s, l, text.Substring(s, l), Reader.I(r[5])));
        }
        return hits;
    }

    /// <summary>Opens a cited span at the revision that was cited, with a little context, and says plainly when that revision is no longer the current one or the source is gone.</summary>
    public static PassageView Open(string workspace, string docId, int revision, int start, int length)
    {
        using var db = Reader.Open(workspace);
        var d = Reader.Rows(db, "SELECT name, removed_at, forgotten_at FROM documents WHERE id=$i", ("$i", docId)).FirstOrDefault() ?? throw Bad("not_found", "That source no longer exists.", "It may have been erased with its workspace.");
        string name = Reader.S(d[0]);
        string? text = TaskStore.Scalar<string>(db, "SELECT text FROM document_revisions WHERE doc_id=$d AND revision=$r", ("$d", docId), ("$r", revision));
        int latest = TaskStore.Scalar<int?>(db, "SELECT max(revision) FROM document_revisions WHERE doc_id=$i", ("$i", docId)) ?? 0;
        if (d[2] != null || text == null) return new(docId, name, revision, start, length, "", "", "", "forgotten", $"“{name}” was forgotten, so the passage is no longer stored. The answer that cited it remains as written.");
        if (start < 0 || length < 0 || start + length > text.Length) throw Bad("bad_citation", "That citation does not fit the stored revision.", "Reload the answer.");
        string state = d[1] != null ? "removed" : revision == latest ? "current" : "superseded";
        string note = state switch
        {
            "current" => $"Revision {revision}, the current one.",
            "superseded" => $"Revision {revision}, as cited. The document now has revision {latest}.",
            _ => $"Revision {revision}, as cited. “{name}” has since been removed from the project's sources.",
        };
        int b = Math.Max(0, start - 120), a = Math.Min(text.Length, start + length + 120);
        return new(docId, name, revision, start, length, text[b..start], text.Substring(start, length), text[(start + length)..a], state, note);
    }

    public static IReadOnlyList<DocAnswer> Answers(string workspace, string projectId)
    {
        using var db = Reader.Open(workspace);
        return [.. Reader.Rows(db, "SELECT id, project_id, question, answer, status, model, destination, created_at, invalid_refs FROM doc_answers WHERE project_id=$p ORDER BY created_at, rowid", ("$p", projectId)).Select(r =>
            new DocAnswer(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.S(r[4]), r[5] as string, r[6] as string, Reader.S(r[7]), [.. Reader.Rows(db, """
                SELECT c.n, c.doc_id, d.name, c.revision, c.start, c.length, c.cited, r.text, d.removed_at, d.forgotten_at,
                       (SELECT max(revision) FROM document_revisions WHERE doc_id=c.doc_id)
                FROM doc_citations c JOIN documents d ON d.id=c.doc_id LEFT JOIN document_revisions r ON r.doc_id=c.doc_id AND r.revision=c.revision
                WHERE c.answer_id=$a ORDER BY c.n
                """, ("$a", Reader.S(r[0]))).Select(c =>
            {
                string? t = c[7] as string;
                string quote = t != null && Reader.I(c[4]) + Reader.I(c[5]) <= t.Length ? t.Substring(Reader.I(c[4]), Reader.I(c[5])) : "";
                string state = c[9] != null ? "forgotten" : c[8] != null ? "removed" : Reader.I(c[3]) == Reader.I(c[10]) ? "current" : "superseded";
                return new Citation(Reader.I(c[0]), Reader.S(c[1]), Reader.S(c[2]), Reader.I(c[3]), Reader.I(c[4]), Reader.I(c[5]), Reader.I(c[6]) != 0, quote, state);
            })], r[8] is string inv ? System.Text.Json.JsonSerializer.Deserialize<string[]>(inv) : null))];
    }

    // --- Forgetting ---------------------------------------------------------------------------------------------------
    /// <summary>
    /// Erases a source's stored content: every revision's text and bytes, its search index, its thumbnail, and the passages quoted in reusable packets and answers.
    /// A tombstone (the name and the content hash) stays so the owner can see that something was here. Copies already sent to a provider cannot be recalled; they are listed.
    /// </summary>
    public static (CommandResult Result, ForgetReport Report) Forget(string workspace, string commandId, string docId)
    {
        ForgetReport? report = null;
        var result = TaskStore.Run(workspace, commandId, "document_forgotten", db =>
        {
            var d = Reader.Rows(db, "SELECT name, forgotten_at FROM documents WHERE id=$i", ("$i", docId)).FirstOrDefault() ?? throw Bad("not_found", "That source no longer exists.", "Reload the list.");
            string name = Reader.S(d[0]);
            var spans = Reader.Rows(db, "SELECT DISTINCT c.answer_id, r.text, c.start, c.length FROM doc_citations c JOIN document_revisions r ON r.doc_id=c.doc_id AND r.revision=c.revision WHERE c.doc_id=$d AND r.text IS NOT NULL", ("$d", docId))
                .Where(r => Reader.I(r[2]) + Reader.I(r[3]) <= Reader.S(r[1]).Length).Select(r => (Answer: Reader.S(r[0]), Passage: Reader.S(r[1]).Substring(Reader.I(r[2]), Reader.I(r[3])))).ToList();
            TaskStore.Exec(db, "PRAGMA secure_delete=ON"); // freed pages are overwritten, so the raw file no longer holds what was erased
            int packets = 0, answers = 0;
            const string mark = "[passage from a forgotten source removed]";
            foreach (var g in spans.GroupBy(s => s.Answer))
            {
                string packet = TaskStore.Scalar<string>(db, "SELECT packet_text FROM doc_answers WHERE id=$a", ("$a", g.Key)) ?? "";
                string p2 = packet;
                foreach (var s in g.Where(s => s.Passage.Length > 0)) p2 = p2.Replace(s.Passage, mark);
                if (p2 != packet) packets++;
                // A model's answer may quote or paraphrase the passage, so an answer that relied on this source is removed whole, not edited.
                TaskStore.Exec(db, "UPDATE doc_answers SET packet_text=$p, answer='[This answer was written from a source that has since been forgotten, so it was removed.]' WHERE id=$i", ("$p", p2), ("$i", g.Key));
                answers++;
            }
            int revisions = (int)TaskStore.Scalar<long>(db, "SELECT count(*) FROM document_revisions WHERE doc_id=$d AND (text IS NOT NULL OR bytes IS NOT NULL)", ("$d", docId));
            int index = (int)(TaskStore.Scalar<long>(db, "SELECT count(*) FROM doc_terms WHERE doc_id=$d", ("$d", docId)) + TaskStore.Scalar<long>(db, "SELECT count(*) FROM doc_chunks WHERE doc_id=$d", ("$d", docId)));
            bool thumb = TaskStore.Scalar<long>(db, "SELECT count(*) FROM doc_thumbs WHERE doc_id=$d", ("$d", docId)) > 0;
            TaskStore.Exec(db, "UPDATE document_revisions SET text=NULL, bytes=NULL WHERE doc_id=$d", ("$d", docId));
            TaskStore.Exec(db, "DELETE FROM doc_terms WHERE doc_id=$d", ("$d", docId));
            TaskStore.Exec(db, "DELETE FROM doc_chunks WHERE doc_id=$d", ("$d", docId));
            TaskStore.Exec(db, "DELETE FROM doc_thumbs WHERE doc_id=$d", ("$d", docId));
            TaskStore.Exec(db, "UPDATE documents SET forgotten_at=COALESCE(forgotten_at,$at), removed_at=COALESCE(removed_at,$at) WHERE id=$d", ("$at", Now), ("$d", docId));
            var sent = Reader.Rows(db, "SELECT DISTINCT a.id, a.model, a.destination FROM doc_citations c JOIN doc_answers a ON a.id=c.answer_id WHERE c.doc_id=$d AND a.model IS NOT NULL ORDER BY a.created_at", ("$d", docId))
                .Select(r => $"A question was sent to {Reader.S(r[1])} ({Reader.S(r[2])}); passages from this source were in it.").ToList();
            report = new(docId, name, revisions, index, thumb, packets, answers, sent,
                ["Anything a model wrote from this source and the owner kept (outputs, reviews, decisions) stays in the project; it is not traced back to the source.",
                 "Passages a model already received cannot be recalled" + (sent.Any(s => s.Contains("cloud")) ? "; at least one went to a cloud service." : "; these stayed on this computer."),
                 "Exports made before this point still contain the source. Delete or replace them.",
                 "The name and a content fingerprint stay as a tombstone, so you can see something was here."]);
            return new(docId, 0, docId, null, null, $"“{name}” forgotten");
        });
        report ??= ForgetReportFor(workspace, docId); // a repeated command: nothing more to erase
        return (result, report);
    }

    private static ForgetReport ForgetReportFor(string workspace, string docId)
    {
        using var db = Reader.Open(workspace);
        string name = TaskStore.Scalar<string>(db, "SELECT name FROM documents WHERE id=$i", ("$i", docId)) ?? "";
        return new(docId, name, 0, 0, false, 0, 0, [], ["This source was already forgotten."]);
    }
}
