using System.Text;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 3 checkpoint B: output inspection, handoff packets, the team channel, the decision inbox and acceptance effects.</summary>
internal static class CheckpointF
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

    private static void Sql(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static WorkerError Fault(Action act)
    {
        try { act(); }
        catch (Exception e) when (e.GetType().Name == "StoreException") { return (WorkerError)e.GetType().GetProperty("Error")!.GetValue(e)!; }
        throw new InvalidOperationException("expected a store error");
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static int cmd;
    private static string C() => "f" + ++cmd;

    /// <summary>A PNG with a valid signature and header. Only the header is read by the inspector.</summary>
    internal static byte[] Png(int w, int h, int pad = 0)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        foreach (int v in new[] { w, h }) b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        b.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0]);
        b.AddRange(new byte[pad]);
        return [.. b];
    }

    /// <summary>A small real PNG: a gradient that any decoder can read.</summary>
    internal static byte[] RealPng(int w, int h)
    {
        static uint[] Table() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
        var table = Table();
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            uint crc = 0xFFFFFFFF;
            foreach (byte b in body) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            crc ^= 0xFFFFFFFF;
            ms.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            ms.Write(body);
            ms.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        }
        Chunk("IHDR", [(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 2, 0, 0, 0]);
        var raw = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            raw.Add(0);
            for (int x = 0; x < w; x++) raw.AddRange([(byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), 128]);
        }
        using (var z = new MemoryStream())
        {
            using (var zl = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) zl.Write(raw.ToArray());
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", []);
        return ms.ToArray();
    }

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Migration(Path.Combine(dir, "migrate"));
        Outputs_(Path.Combine(dir, "outputs"));
        Handoffs(Path.Combine(dir, "handoff"));
        Recipient(Path.Combine(dir, "recipient"));
        Channel_(Path.Combine(dir, "channel"));
        Inbox(Path.Combine(dir, "inbox"));
    }

    private static (WorkspaceInfo Info, string Task) Task1(string folder, string title = "Draft", string? outcome = null, bool review = true)
    {
        var info = ProjectStore.Create(folder, "Atlas", NewId);
        string t = TaskStore.CreateTask(info.DatabasePath, C(), info.ProjectId, title, outcome, NewId).Id;
        if (review) TaskStore.UpdateTask(info.DatabasePath, C(), t, 0, null, null, null, reviewRequired: true);
        return (info, t);
    }

    // AH32 · identities and hashes survive migration ---------------------------------------------------------------------
    private static void Migration(string dir)
    {
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "agenthub.db");
        Sql(db, """
            CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, title TEXT NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0, outcome TEXT, owner TEXT);
            CREATE TABLE hub_dependencies(task_id TEXT NOT NULL, requires_id TEXT NOT NULL, PRIMARY KEY(task_id, requires_id));
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE workspace(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE events(seq INTEGER PRIMARY KEY AUTOINCREMENT, command_id TEXT NOT NULL UNIQUE, task_id TEXT NOT NULL, kind TEXT NOT NULL,
                from_state TEXT, to_state TEXT, detail TEXT, created_at TEXT NOT NULL);
            CREATE TABLE outputs(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, revision INTEGER NOT NULL, text TEXT NOT NULL, author_role TEXT NOT NULL,
                accepted_by TEXT, created_at TEXT NOT NULL, UNIQUE(task_id, revision));
            CREATE TABLE reviews(id TEXT PRIMARY KEY, output_id TEXT NOT NULL, recommendation TEXT NOT NULL, comment TEXT NOT NULL, reviewer_role TEXT NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO meta VALUES('schema_version','4');
            INSERT INTO workspace VALUES('w','Atlas','1');
            INSERT INTO projects VALUES('p','Atlas');
            INSERT INTO tasks VALUES('t','p','Draft','in_progress','1',2,NULL,'Maya');
            INSERT INTO events(command_id,task_id,kind,created_at,detail) VALUES('c1','t','task_created','1','{}');
            INSERT INTO outputs VALUES('o1','t',1,'Draft 1','builder (fixture)',NULL,'2');
            INSERT INTO outputs VALUES('o2','t',2,'Draft 2','builder (fixture)',NULL,'3');
            """);
        ProjectStore.Load(db);
        string expected = ProjectStore.Hash(Encoding.UTF8.GetBytes("Draft 1"));
        Check(One<string>(db, "SELECT group_concat(id||':'||revision, '|') FROM outputs ORDER BY revision") is "o1:1|o2:2" && One<string>(db, "SELECT content_hash FROM outputs WHERE id='o1'") == expected
              && One<string>(db, "SELECT kind FROM outputs WHERE id='o1'") == "text" && One<int>(db, "SELECT byte_size FROM outputs WHERE id='o1'") == 7,
              "v4 text outputs keep their IDs through migration and gain a content hash, kind and size");
        Check(One<long>(db, "SELECT count(*) FROM pragma_table_info('questions')") == 11 && One<long>(db, "SELECT count(*) FROM pragma_table_info('runs') WHERE name='packet_text'") == 1, "a v4 store also gains the packet columns on runs and the questions table");
        var inspected = Outputs.Inspect(db, "o1");
        Check(inspected.Verified && inspected.Text == "Draft 1" && Outputs.Compare(db, "o1", "o2") is { Unchanged: false } d && d.Lines.Any(l => l is { Op: '+', Text: "Draft 2" }),
              "a migrated output verifies against its hash and compares with the next revision");
        var immutable = false;
        try { Sql(db, "UPDATE outputs SET text='changed' WHERE id='o1'"); } catch (SqliteException) { immutable = true; }
        Sql(db, "UPDATE outputs SET accepted_by='owner' WHERE id='o1'");
        Check(immutable && One<string>(db, "SELECT text FROM outputs WHERE id='o1'") == "Draft 1" && One<string>(db, "SELECT accepted_by FROM outputs WHERE id='o1'") == "owner",
              "a stored output cannot be rewritten, while recording its acceptance still works");
    }

    // AH32 · inspect and compare -----------------------------------------------------------------------------------------
    private static void Outputs_(string dir)
    {
        var (info, task) = Task1(dir);
        string db = info.DatabasePath;
        var o1 = TaskStore.RunRole(db, C(), task, "builder", null, NewId);
        TaskStore.RunRole(db, C(), task, "reviewer", null, NewId);
        var o2 = TaskStore.RunRole(db, C(), task, "builder", null, NewId);
        long events = One<long>(db, "SELECT count(*) FROM events");

        var diff = Outputs.Compare(db, o1.Id, o2.Id);
        Check(diff.Kind == "text" && diff.Lines.Any(l => l.Op == '+' && l.Text.StartsWith("Summary:")) && diff.ChangedSections.Any(s => s.StartsWith("Summary"))
              && diff.Summary.Contains("Revision 2 against revision 1"), "comparing v1 and v2 shows the added summary line and names the changed section");
        Check(One<long>(db, "SELECT count(*) FROM decisions") == 0 && One<long>(db, "SELECT count(*) FROM events") == events && One<string>(db, "SELECT count(*) FROM outputs WHERE accepted_by IS NOT NULL") == "0",
              "inspecting and comparing outputs creates no review decision and no event");

        var same = TaskStore.AddFixtureOutput(db, C(), task, "text", Encoding.UTF8.GetBytes("same"), "builder (fixture)", NewId);
        var again = TaskStore.AddFixtureOutput(db, C(), task, "text", Encoding.UTF8.GetBytes("same"), "builder (fixture)", NewId);
        Check(Outputs.Compare(db, same.Id, again.Id) is { Unchanged: true } u && u.Summary.Contains("identical"), "identical content is reported as unchanged");

        var patchA = TaskStore.AddFixtureOutput(db, C(), task, "patch", Encoding.UTF8.GetBytes("--- a/One.cs\n+++ b/One.cs\n@@\n-x\n+y\n"), "builder (fixture)", NewId);
        var patchB = TaskStore.AddFixtureOutput(db, C(), task, "patch", Encoding.UTF8.GetBytes("--- a/One.cs\n+++ b/One.cs\n@@\n-x\n+z\n--- a/Two.cs\n+++ b/Two.cs\n@@\n+new\n"), "builder (fixture)", NewId);
        var pd = Outputs.Compare(db, patchA.Id, patchB.Id);
        Check(pd.Kind == "patch" && pd.ChangedSections.SequenceEqual(["One.cs", "Two.cs"]) && pd.Lines.Any(l => l is { Op: '+', Text: "+z" }), "a patch diff lists the files it changes and the changed lines");

        var json = TaskStore.AddFixtureOutput(db, C(), task, "data", Encoding.UTF8.GetBytes("{\"a\":1,\"b\":[1,2]}"), "builder (fixture)", NewId);
        var bad = TaskStore.AddFixtureOutput(db, C(), task, "data", Encoding.UTF8.GetBytes("{not json"), "builder (fixture)", NewId);
        Check(Outputs.Inspect(db, json.Id) is { Verified: true } jv && jv.Text!.Contains("\"a\": 1") && Outputs.Inspect(db, bad.Id) is { Integrity: "malformed_data" } bv && bv.Text == "{not json",
              "structured data is shown as pretty data, and malformed data is shown as plain text with a warning");

        var img = TaskStore.AddFixtureOutput(db, C(), task, "image", Png(64, 32), "builder (fixture)", NewId);
        Check(Outputs.Inspect(db, img.Id) is { Verified: true, Width: 64, Height: 32, Image: not null }, "a header-only image passes the size check (it proves the dimension limit, not decoding)");
        var real = TaskStore.AddFixtureOutput(db, C(), task, "image", RealPng(48, 24), "builder (fixture)", NewId);
        Check(Outputs.Inspect(db, real.Id) is { Verified: true, Width: 48, Height: 24, Image: { Length: > 60 } }, "a real, decodable PNG is verified and handed to the preview");
        string blob = Path.Combine(dir, "blobs", img.Id);
        File.WriteAllBytes(blob, Png(64, 32, 4));
        Check(Outputs.Inspect(db, img.Id) is { Integrity: "hash_mismatch", Image: null }, "an image whose bytes changed after saving is not shown");
        File.Delete(blob);
        Check(Outputs.Inspect(db, img.Id) is { Integrity: "missing_blob", Image: null } m && m.Note.Contains("missing"), "a missing image file is reported, not guessed");
        var junk = TaskStore.AddFixtureOutput(db, C(), task, "image", Encoding.UTF8.GetBytes("this is not an image"), "builder (fixture)", NewId);
        var huge = TaskStore.AddFixtureOutput(db, C(), task, "image", Png(5000, 5000), "builder (fixture)", NewId);
        var heavy = TaskStore.AddFixtureOutput(db, C(), task, "image", Png(10, 10, (int)Limits.MaxImageBytes + 10), "builder (fixture)", NewId);
        Check(Outputs.Inspect(db, junk.Id).Integrity == "malformed_image" && Outputs.Inspect(db, huge.Id).Integrity == "oversized_image" && Outputs.Inspect(db, heavy.Id).Integrity == "oversized_image"
              && Outputs.Inspect(db, junk.Id).Image == null, "a malformed image and an oversized image are refused with a reason");
        Check(Outputs.Compare(db, same.Id, img.Id).Kind == "unavailable" && Outputs.Compare(db, junk.Id, huge.Id).Summary.Contains("cannot be compared"), "comparing with an unusable output says why instead of showing a partial diff");

        // A change beyond the preview must not read as "no change".
        string common = new('x', Limits.MaxTextChars);
        var longA = TaskStore.AddFixtureOutput(db, C(), task, "text", Encoding.UTF8.GetBytes(common + "A"), "builder (fixture)", NewId);
        var longB = TaskStore.AddFixtureOutput(db, C(), task, "text", Encoding.UTF8.GetBytes(common + "B"), "builder (fixture)", NewId);
        var beyond = Outputs.Compare(db, longA.Id, longB.Id);
        Check(!beyond.Unchanged && beyond.Truncated && beyond.OutsidePreview && beyond.Lines.Count == 0 && beyond.Summary.Contains("outside the displayed portion") && beyond.Summary.Contains("100,000"),
              "two outputs that differ only after the first 100,000 characters are reported as differing outside the preview, not as an empty diff");
        var longC = TaskStore.AddFixtureOutput(db, C(), task, "text", Encoding.UTF8.GetBytes("changed first\n" + common + "C"), "builder (fixture)", NewId);
        var mixed = Outputs.Compare(db, longB.Id, longC.Id);
        Check(mixed.OutsidePreview && mixed.Lines.Any(l => l.Op == '+') && mixed.Summary.Contains("outside the displayed portion"), "a visible change in a clipped revision still says that more may differ outside the preview");
        string manyLines = string.Join("\n", Enumerable.Range(0, Limits.MaxDiffLines + 400).Select(i => "line " + i));
        var linesA = TaskStore.AddFixtureOutput(db, C(), task, "text", Encoding.UTF8.GetBytes(manyLines), "builder (fixture)", NewId);
        var linesB = TaskStore.AddFixtureOutput(db, C(), task, "text", Encoding.UTF8.GetBytes(manyLines + "\nmore"), "builder (fixture)", NewId);
        var cut = Outputs.Compare(db, linesA.Id, linesB.Id);
        Check(cut.OutsidePreview && cut.Truncated && cut.Summary.Contains("first 2,000 lines"), "the line-count limit is reported the same way, separately from the character limit");

        var raw = TaskStore.AddFixtureOutput(db, C(), task, "text", Encoding.UTF8.GetBytes("<b onclick=x>not markup</b> {0} $(rm)"), "builder (fixture)", NewId);
        Check(Outputs.Inspect(db, raw.Id).Text == "<b onclick=x>not markup</b> {0} $(rm)", "generated content comes back as plain data, byte for byte");
        Sql(db, "DROP TRIGGER outputs_immutable; UPDATE outputs SET text='tampered' WHERE id='" + raw.Id + "'");
        Check(Outputs.Inspect(db, raw.Id) is { Integrity: "hash_mismatch", Text: null }, "tampered text no longer matches its hash and is not shown");
    }

    // AH23 · project-scoped handoffs -------------------------------------------------------------------------------------
    private static void Handoffs(string dir)
    {
        var info = ProjectStore.Create(dir, "Alpha", NewId);
        string db = info.DatabasePath;
        string beta = NewId();
        Sql(db, $"INSERT INTO projects VALUES('{beta}','Beta')");
        (string Spec, string Draft) Build(string project, string marker)
        {
            string spec = TaskStore.CreateTask(db, C(), project, "Spec", $"Criteria {marker}", NewId).Id;
            string draft = TaskStore.CreateTask(db, C(), project, "Draft", $"Write it for {marker}", NewId).Id;
            TaskStore.AddDependency(db, C(), draft, spec, 0);
            TaskStore.UpdateTask(db, C(), spec, 0, null, null, null, reviewRequired: true);
            var o = TaskStore.RunRole(db, C(), spec, "builder", null, NewId);
            TaskStore.DecideOutput(db, C(), spec, o.Id, 1, 1, "accepted", null, "owner", NewId);
            TaskStore.UpdateTask(db, C(), draft, 1, null, null, null, reviewRequired: true);
            TaskStore.RunRole(db, C(), draft, "builder", null, NewId);
            return (spec, draft);
        }
        var a = Build(info.ProjectId, "ALPHA-MARK");
        var b = Build(beta, "BETA-MARK");

        var forA = Handoff.Build(db, info.ProjectId, a.Draft, "reviewer");
        var forB = Handoff.Build(db, beta, b.Draft, "reviewer");
        Check(forA.Text.Contains("ALPHA-MARK") && !forA.Text.Contains("BETA-MARK") && forB.Text.Contains("BETA-MARK") && !forB.Text.Contains("ALPHA-MARK"),
              "two projects with the same task titles each hand over only their own evidence");
        var upstream = forA.Sections.Single(s => s.Heading.StartsWith("Accepted output of “Spec”"));
        Check(upstream.SourceId == One<string>(db, $"SELECT id FROM outputs WHERE task_id='{a.Spec}'") && upstream.SourceRevision == 1 && upstream.Heading.Contains(upstream.SourceId!),
              "the packet names the accepted upstream output by ID and revision");
        var draft = forA.Sections.Single(s => s.Heading.StartsWith("Output under review"));
        Check(draft.Draft && draft.Heading.Contains("DRAFT, not accepted") && draft.Heading.Contains("review of “Draft” only"), "a draft under review is labelled as a draft and scoped to its review step");

        Sql(db, $"INSERT INTO hub_dependencies VALUES('{b.Draft}','{a.Spec}')");
        var crossed = Handoff.Build(db, beta, b.Draft, "reviewer");
        Check(!crossed.Text.Contains("ALPHA-MARK"), "an edge that crosses projects never pulls the other project's output into a packet");
        Check(Fault(() => Handoff.Build(db, beta, a.Draft, "builder")).Code == "not_found", "a task cannot be handed over under another project");

        var tight = Handoff.Build(db, info.ProjectId, a.Draft, "reviewer", 45);
        Check(tight.TokensUsed <= 45 && tight.Text.Length <= 45 * 4 && tight.Omitted.Count > 0 && tight.Omitted.Any(o => o.Contains("did not fit")) && tight.Text.Contains("left out"),
              "a packet inside a small limit stays within it, payload included, and the full list of what was left out stays with the inspector");
        foreach (int limit in new[] { 20, 32, 60, 120, 400 })
        {
            var p = Handoff.Build(db, info.ProjectId, a.Draft, "reviewer", limit);
            Check(p.Text.Length <= limit * 4 && (p.Text.Length + 3) / 4 == p.TokensUsed && p.TokensUsed <= limit, $"the payload itself fits a limit of {limit} tokens by the four-characters-per-token estimate");
        }
        string longTask = TaskStore.CreateTask(db, C(), info.ProjectId, "Long", new string('y', 4000), NewId).Id;
        var cutP = Handoff.Build(db, info.ProjectId, longTask, "builder", 50);
        Check(cutP.Text.Length <= 200 && cutP.Omitted.Any(o => o.Contains("cut to fit")) && cutP.Sections[0].Heading.Contains("cut to the limit"), "very long criteria are cut to the limit and the cut is reported");
        string hub = TaskStore.CreateTask(db, C(), info.ProjectId, "Hub", "needs several", NewId).Id;
        for (int i = 0; i < 4; i++)
        {
            string up = TaskStore.CreateTask(db, C(), info.ProjectId, "Upstream " + i, "criteria " + i, NewId).Id;
            TaskStore.UpdateTask(db, C(), up, 0, null, null, null, reviewRequired: true);
            var uo = TaskStore.RunRole(db, C(), up, "builder", null, NewId);
            TaskStore.DecideOutput(db, C(), up, uo.Id, 1, 1, "accepted", null, "owner", NewId);
            TaskStore.AddDependency(db, C(), hub, up, One<int>(db, $"SELECT revision FROM tasks WHERE id='{hub}'"));
        }
        var many = Handoff.Build(db, info.ProjectId, hub, "builder", 90);
        Check(many.Text.Length <= 90 * 4 && many.Omitted.Count >= 2 && many.Omitted.All(o => o.Contains("Upstream")) && many.Text.Contains("left out"),
              "several omissions are all named in the report while the payload stays inside the limit");
        var none = Handoff.Build(db, info.ProjectId, a.Spec, "builder", 500);
        Check(none.Sections.Count == 1 && none.Omitted.Count == 0, "a task with no prerequisites gets its own criteria only");
        // The offline recipients consume the packet: what they were given is recorded with the run.
        string draftRun = One<string>(db, $"SELECT run_id FROM outputs WHERE task_id='{a.Draft}' ORDER BY revision DESC LIMIT 1");
        var gave = Runs.Packet(db, draftRun);
        string specOut = One<string>(db, $"SELECT id FROM outputs WHERE task_id='{a.Spec}'");
        Check(gave.Recorded && gave.ProjectId == info.ProjectId && gave.Role == "builder" && gave.Limit == 800 && gave.Tokens <= 800 && gave.Text!.Contains("ALPHA-MARK") && !gave.Text.Contains("BETA-MARK")
              && gave.Sources.SequenceEqual([(specOut, 1)]), "the builder run records the project, limit, size, text and source revisions of the packet it consumed, with only its own project's marker");
        Check(One<string>(db, $"SELECT text FROM outputs WHERE task_id='{a.Draft}' ORDER BY revision DESC LIMIT 1").Contains("Sources: “Spec” output " + specOut[..8] + " revision 1"),
              "the builder's draft names the accepted upstream output it was written from");
        TaskStore.RunRole(db, C(), a.Draft, "reviewer", null, NewId);
        string revRun = One<string>(db, $"SELECT r.run_id FROM reviews r JOIN outputs o ON o.id=r.output_id WHERE o.task_id='{a.Draft}' ORDER BY r.rowid DESC LIMIT 1");
        string draftOut = One<string>(db, $"SELECT id FROM outputs WHERE task_id='{a.Draft}' ORDER BY revision DESC LIMIT 1");
        var seen = Runs.Packet(db, revRun);
        Check(seen is { Recorded: true, Role: "reviewer" } && seen.ProjectId == info.ProjectId && seen.Text!.Contains("DRAFT, not accepted") && !seen.Text.Contains("BETA-MARK") && seen.Sources.Contains((draftOut, 1)) && seen.Sources.Contains((specOut, 1)),
              "the reviewer run records the bounded packet it judged: the draft revision and the accepted upstream, in its own project");
        TaskStore.AddFixtureOutput(db, C(), a.Draft, "text", Encoding.UTF8.GetBytes(new string('z', 5000)), "builder (fixture)", NewId);
        Check(Fault(() => TaskStore.RunRole(db, C(), a.Draft, "reviewer", null, NewId)).Code == "packet_omitted_draft", "a draft too large for the packet is refused, not reviewed from outside it");
        string noAccept = TaskStore.CreateTask(db, C(), info.ProjectId, "Needs spec two", null, NewId).Id;
        string spec2 = TaskStore.CreateTask(db, C(), info.ProjectId, "Spec two", null, NewId).Id;
        TaskStore.AddDependency(db, C(), noAccept, spec2, 0);
        Check(Handoff.Build(db, info.ProjectId, noAccept, "builder").Omitted.Any(o => o.Contains("no accepted output")), "an upstream task with nothing accepted is named, not silently dropped");
    }

    // AH23 · the builder writes from the packet that was recorded, and from nothing else ------------------------------------------
    private static void Recipient(string dir)
    {
        var info = ProjectStore.Create(dir, "Atlas", NewId);
        string db = info.DatabasePath;
        string T(string title, string? outcome) => TaskStore.CreateTask(db, C(), info.ProjectId, title, outcome, NewId).Id;
        string RunOf(string task) => One<string>(db, $"SELECT run_id FROM outputs WHERE task_id='{task}' ORDER BY revision DESC LIMIT 1");
        string Persisted(string task) => One<string>(db, $"SELECT packet_text FROM runs WHERE id='{RunOf(task)}'");
        string Draft(string task) => One<string>(db, $"SELECT text FROM outputs WHERE task_id='{task}' ORDER BY revision DESC LIMIT 1");

        // A marker past what the packet admitted cannot reach the draft, although the store holds it.
        const string tail = "TAIL-MARKER-9f3";
        string longTask = T("Long", new string('a', 5000) + tail);
        TaskStore.RunRole(db, C(), longTask, "builder", null, NewId);
        string packetText = Persisted(longTask);
        string criteria = packetText[(packetText.IndexOf("Criteria: ", StringComparison.Ordinal) + 10)..];
        criteria = criteria[..(criteria.IndexOf('…') + 1)];
        Check(One<string>(db, $"SELECT outcome FROM tasks WHERE id='{longTask}'").Contains(tail) && !packetText.Contains(tail) && !Draft(longTask).Contains(tail) && Draft(longTask).Contains("Goal: " + criteria) && criteria.Length > 3000,
              "a marker the packet left out is in the store but not in the persisted packet, and not in the draft; the draft's goal is exactly the packet's criteria");

        // A marker inside the packet is used.
        string keep = T("Short", "KEEP-MARK-1 then the rest");
        TaskStore.RunRole(db, C(), keep, "builder", null, NewId);
        Check(Persisted(keep).Contains("KEEP-MARK-1") && Draft(keep).Contains("Goal: KEEP-MARK-1 then the rest"), "criteria inside the packet appear in the draft exactly as the packet holds them");

        // A correction comes from the packet's feedback section, which records its review and revision.
        TaskStore.RunRole(db, C(), keep, "reviewer", null, NewId);
        string reviewId = One<string>(db, $"SELECT r.id FROM reviews r JOIN outputs o ON o.id=r.output_id WHERE o.task_id='{keep}'");
        TaskStore.RunRole(db, C(), keep, "builder", null, NewId);
        string second = Persisted(keep);
        string sources = One<string>(db, $"SELECT packet_sources FROM runs WHERE id='{RunOf(keep)}'");
        Check(second.Contains("Reviewer feedback on revision 1") && second.Contains("Add a one-line summary.") && Draft(keep).Contains("Correction applied: Add a one-line summary.")
              && sources.Contains(reviewId) && sources.Contains("\"revision\":1"), "a correction in the draft is the one in the persisted packet, whose sources name the review and the revision it was about");

        // Feedback the packet could not hold is not applied, even though the store has it.
        string full = T("Full", new string('b', 6000));
        var o1 = TaskStore.RunRole(db, C(), full, "builder", null, NewId);
        Sql(db, $"INSERT INTO reviews(id,output_id,recommendation,comment,reviewer_role,created_at,run_id) VALUES('rv-x','{o1.Id}','request_changes','FEEDBACK-MARK-77','reviewer (fixture)','1',NULL)");
        TaskStore.RunRole(db, C(), full, "builder", null, NewId);
        Check(!Persisted(full).Contains("FEEDBACK-MARK-77") && !Draft(full).Contains("FEEDBACK-MARK-77") && !Draft(full).Contains("Correction applied") && Draft(full).Contains("Summary is not written yet.")
              && Persisted(full).Contains("left out"), "feedback that did not fit the packet is not applied, and the packet says something was left out");
    }

    // AH26 · the team channel --------------------------------------------------------------------------------------------
    private static void Channel_(string dir)
    {
        var (info, task) = Task1(dir);
        string db = info.DatabasePath;
        string other = NewId();
        Sql(db, $"INSERT INTO projects VALUES('{other}','Beta')");
        string otherTask = TaskStore.CreateTask(db, C(), other, "Draft", null, NewId).Id;
        var o1 = TaskStore.RunRole(db, C(), task, "builder", null, NewId);
        TaskStore.RunRole(db, C(), task, "reviewer", null, NewId);
        TaskStore.DecideOutput(db, C(), task, o1.Id, 1, 1, "changes_requested", "Add a summary.", "owner", NewId);

        var all = Channel.Read(info.DatabasePath, info.ProjectId, 0, 100);
        Check(all.Messages.Select(m => m.Seq).SequenceEqual(all.Messages.Select(m => m.Seq).Order()) && all.Messages.All(m => m.ProjectId == info.ProjectId) && !all.Messages.Any(m => m.TaskId == otherTask),
              "channel messages come in order and only from the selected project");
        var proposed = all.Messages.Single(m => m.Kind == "output_proposed");
        var review = all.Messages.Single(m => m.Kind == "review_recorded");
        var decision = all.Messages.Single(m => m.Kind == "changes_requested");
        Check(proposed is { ActorKind: "agent" } && proposed.RunId == One<string>(db, $"SELECT run_id FROM outputs WHERE id='{o1.Id}'") && proposed.Text.Contains("It is a draft; nothing is accepted") && proposed.Text.Contains("Draft 1 for"),
              "the builder's handoff is a message bound to its run, shown as a draft, with the actual text");
        Check(review is { ActorKind: "agent" } && review.RunId != null && review.Text.Contains("Add a one-line summary") && review.Text.Contains("not a decision"), "the reviewer's recommendation is shown and labelled as only a recommendation");
        Check(decision is { ActorKind: "human", Actor: "You (workspace owner)" } && decision.Text.Contains("Add a summary.") && decision.RunId == proposed.RunId, "the human decision is its own message from the owner");

        // Reconnect from the last cursor while another event arrives.
        var first = Channel.Read(db, info.ProjectId, 0, 2);
        TaskStore.RunRole(db, C(), task, "builder", null, NewId); // arrives between two reads
        var second = Channel.Read(db, info.ProjectId, first.Cursor, 50);
        var seen = first.Messages.Concat(second.Messages).Select(m => m.Seq).ToList();
        var fresh = Channel.Read(db, info.ProjectId, 0, 100).Messages.Select(m => m.Seq).ToList();
        Check(first.More && seen.SequenceEqual(fresh) && seen.Distinct().Count() == seen.Count && second.Messages.Last().Kind == "output_proposed",
              "reconnecting from the last cursor shows every message once, in order, including the one that arrived in between");
        Check(Channel.Read(db, info.ProjectId, second.Cursor, 50).Messages.Count == 0 && Channel.Read(db, other, 0, 50).Messages.Single().TaskId == otherTask,
              "an up-to-date reader sees nothing new, and switching project shows that project's own messages");
        Check(Channel.Read(db, info.ProjectId, 0, 100000).Messages.Count <= Limits.MaxPage, "one read is bounded");

        // Rows from before actors were recorded stay readable.
        Sql(db, $"INSERT INTO events(command_id,task_id,kind,detail,created_at) VALUES('old1','{task}','output_proposed','{{\"id\":\"gone\"}}','1'),('old2','{task}','task_created','{{}}','1')");
        var old = Channel.Read(db, info.ProjectId, second.Cursor, 50).Messages;
        Check(old.Count == 2 && old.All(m => m is { ActorKind: "unrecorded", Actor: "Not recorded" } && m.Text.Length > 8), "historical rows without actor metadata show as not recorded, with readable text");
    }

    // AH31 and AH90 · the decision inbox and what acceptance does --------------------------------------------------------
    private static void Inbox(string dir)
    {
        var info = ProjectStore.Create(dir, "Atlas", NewId);
        string db = info.DatabasePath;
        string beta = NewId();
        Sql(db, $"INSERT INTO projects VALUES('{beta}','Beta')");
        string Make(string project, string title, bool review = true)
        {
            string id = TaskStore.CreateTask(db, C(), project, title, null, NewId).Id;
            if (review) TaskStore.UpdateTask(db, C(), id, 0, null, null, null, reviewRequired: true);
            return id;
        }
        string spec = Make(info.ProjectId, "Spec", false), draft = Make(info.ProjectId, "Draft"), qa = Make(info.ProjectId, "QA", false), draftB = Make(beta, "Draft");
        TaskStore.AddDependency(db, C(), draft, spec, 1);
        TaskStore.AddDependency(db, C(), qa, draft, 0);
        Check(Decisions.Inbox(db).Count == 0, "an empty workspace has an empty inbox");
        var a = TaskStore.RunRole(db, C(), draft, "builder", null, NewId);
        var b = TaskStore.RunRole(db, C(), draftB, "builder", null, NewId);
        var inbox = Decisions.Inbox(db);
        var mine = inbox.Single(i => i.ProjectId == info.ProjectId);
        Check(inbox.Count == 2 && inbox.Select(i => i.ProjectName).Order().SequenceEqual(["Atlas", "Beta"]) && mine is { TaskTitle: "Draft", Revision: 1 } && mine.OutputId == a.Id,
              "the inbox names the project, task and exact revision of each waiting decision across projects");
        Check(mine.Effects[0].Contains("stays open, waiting on “Spec”") && mine.Effects[1].Contains("“QA” keeps waiting on “Draft”"), "while its prerequisite is open, the effect says acceptance will not complete the task");
        TaskStore.MoveTask(db, C(), spec, "in_progress", 0);
        TaskStore.MoveTask(db, C(), spec, "done", 1);
        var ready = Decisions.Effects(db, draft);
        Check(ready[0] == "“Draft” will be marked Done." && ready[1] == "“QA” can then start.", "with its prerequisite done, the effect says Draft completes and QA can start");

        // The same decision, accepted and declined at once.
        var go = new Barrier(2);
        var results = new[] { "accepted", "changes_requested" }.Select(decision => Task.Run(() =>
        {
            go.SignalAndWait();
            try { TaskStore.DecideOutput(db, "race-" + decision, draft, a.Id, 1, 1, decision, decision == "accepted" ? null : "No", "owner", NewId); return (string?)null; }
            catch (Exception e) when (e.GetType().Name == "StoreException") { return ((WorkerError)e.GetType().GetProperty("Error")!.GetValue(e)!).Code; }
        })).ToArray();
        Task.WaitAll(results);
        var outcomes = results.Select(r => r.Result).ToList();
        Check(outcomes.Count(o => o == null) == 1 && outcomes.Count(o => o == "already_decided") == 1 && One<long>(db, $"SELECT count(*) FROM decisions WHERE output_id='{a.Id}'") == 1
              && One<long>(db, "SELECT count(*) FROM events WHERE command_id LIKE 'race-%'") == 1, "an accept and a decline at the same moment give one decision and one audit event; the loser is told it was already decided");
        var after = Decisions.Inbox(db);
        Check(after.Count == 1 && after[0].ProjectName == "Beta", "a second view reading afterwards no longer lists the decided item");
        Check(Decisions.Inbox(db).Count == 1, "reading the inbox again, as after a relaunch, gives the same count");
    }
}
