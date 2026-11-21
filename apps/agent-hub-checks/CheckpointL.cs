using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalMind.AgentHub.Core;

/// <summary>
/// AH48 · one versioned brief run by a single agent and by the team, over the same supplied files, rubric and model settings.
/// Scripted mode checks the harness. Real mode (--day5-bench) points the same code at local models.
/// </summary>
internal static class CheckpointL
{
    public const string BriefVersion = "release-notes-v1";
    private static readonly (string File, string Text, string Key)[] Changes = [("change-a.txt", "Added dark mode", "dark mode"), ("change-b.txt", "Fixed crash on export", "crash on export"), ("change-c.txt", "Faster startup", "faster startup")];
    private const string Outcome = "Write release notes as a bulleted list, one bullet for each supplied change. Cover every change. Start directly with the bullets.";
    private static int n;
    private static string C() => "l" + ++n;

    public sealed record Trial(string Arm, int Index, int Criteria, int Revisions, int ReviewerRuns, int FalseFindings, long ElapsedMs, int InputTokens, int OutputTokens, double KnownSpend, int UnknownCharges);

    /// <summary>Four criteria: one per supplied change, and plain bullets with no preamble.</summary>
    public static int Score(string draft)
    {
        string d = draft.ToLowerInvariant();
        int hit = Changes.Count(c => d.Contains(c.Key));
        var lines = draft.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return hit + (lines.Count > 0 && lines.All(l => l.StartsWith('-') || l.StartsWith('*') || char.IsDigit(l[0])) ? 1 : 0);
    }

    /// <summary>A reviewer's request for changes is false when it names a supplied change that the draft already contains.</summary>
    public static bool FalseFinding(string recommendation, string comment, string draft)
    {
        if (recommendation != "request_changes") return false;
        string c = comment.ToLowerInvariant(), d = draft.ToLowerInvariant();
        return Changes.Any(x => (c.Contains(x.Key) || c.Contains(x.File.ToLowerInvariant()) || c.Contains(x.File.Replace(".txt", ""))) && d.Contains(x.Key));
    }

    private static Func<ScriptedRequest, ScriptedReply> Scripted(int trial) => r =>
    {
        if (r.Model == "bench-builder:1")
        {
            bool all = r.Packet.Contains("Reviewer feedback") || trial % 4 == 3;
            return CheckpointI.Text(r, "- Added dark mode\n- Fixed crash on export" + (all ? "\n- Faster startup" : ""));
        }
        string draft = r.Packet.Contains("Output under review") ? r.Packet[r.Packet.IndexOf("Output under review")..] : "";
        draft = draft.Contains("## Supplied file") ? draft[..draft.IndexOf("## Supplied file")] : draft;
        if (!draft.ToLowerInvariant().Contains("faster startup")) return CheckpointI.Text(r, "REQUEST CHANGES\nThe notes omit change-c.txt (Faster startup).");
        return CheckpointI.Text(r, trial % 3 == 2 ? "REQUEST CHANGES\nchange-b.txt (crash on export) is missing." : "APPROVE\nAll three supplied changes are covered.");
    };

    private static async Task<CheckpointI.Bench> Arm(string folder, string? endpoint, string builder, string reviewer, Func<ScriptedRequest, ScriptedReply>? script, bool team)
    {
        var b = await CheckpointI.NewBench(folder);
        if (endpoint == null) await CheckpointI.LocalTeam(b, builder, reviewer, script);
        else
        {
            b.LocalConn = (await b.W.AddConnectionAsync(b.Db, C(), "ollama", endpoint, "Ollama on this computer")).Id;
            await b.W.ProbeConnectionAsync(b.Db, b.LocalConn);
            await b.W.AssignRoleAsync(b.Db, C(), b.Project, "builder", b.LocalConn, builder);
            await b.W.AssignRoleAsync(b.Db, C(), b.Project, "reviewer", b.LocalConn, reviewer);
        }
        await b.W.SetProjectSettingsAsync(b.Db, C(), b.Settings with { AutoReview = team, MaxSeconds = 300, MaxOutputTokens = 400 });
        return b;
    }

    private static async Task<Trial> Single(string folder, string? endpoint, string builder, string reviewer, int i, Func<ScriptedRequest, ScriptedReply>? script)
    {
        await using var b = await Arm(folder, endpoint, builder, reviewer, script, false);
        string task = Seed(b);
        var clock = Stopwatch.StartNew();
        var run = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id, 300_000);
        clock.Stop();
        return Tally("single agent", i, b, task, Score(run.PartialText), 0, clock.ElapsedMilliseconds);
    }

    private static async Task<Trial> Team(string folder, string? endpoint, string builder, string reviewer, int i, Func<ScriptedRequest, ScriptedReply>? script)
    {
        await using var b = await Arm(folder, endpoint, builder, reviewer, script, true);
        string task = Seed(b);
        await b.W.UpdateTaskAsync(b.Db, C(), task, 0, reviewRequired: true);
        var clock = Stopwatch.StartNew();
        int revisions = 0;
        string final = "";
        for (int round = 1; round <= 2; round++)
        {
            var built = await b.Settled((await b.W.DispatchAsync(b.Db, C(), task, "builder")).Id, 300_000);
            revisions++;
            final = built.PartialText;
            await CheckpointJ.Until(() => RunEngine.ForTask(b.Db, task).Count(a => a.Role == "reviewer" && !a.Active) == round || RunEngine.ForTask(b.Db, task).Any(a => a.Role == "reviewer" && a.Status is "failed" or "stopped"), 300_000);
            string rec = CheckpointJ.One<string>(b.Db, "SELECT recommendation FROM reviews ORDER BY rowid DESC LIMIT 1") ?? "unclear";
            if (rec == "approve_recommended" || round == 2) break;
            // The owner's scripted policy: pass the reviewer's reason on once.
            string output = CheckpointJ.One<string>(b.Db, $"SELECT id FROM outputs WHERE task_id='{task}' AND revision={round}")!;
            await b.W.DecideOutputAsync(b.Db, C(), task, output, round, 1, "changes_requested", CheckpointJ.One<string>(b.Db, "SELECT comment FROM reviews ORDER BY rowid DESC LIMIT 1"));
        }
        clock.Stop();
        return Tally("team", i, b, task, Score(final), revisions - 1, clock.ElapsedMilliseconds);
    }

    private static string Seed(CheckpointI.Bench b)
    {
        string task = b.Task("Release notes", Outcome);
        foreach (var c in Changes) b.W.AddTaskFileAsync(b.Db, C(), task, c.File, c.Text).GetAwaiter().GetResult();
        return task;
    }

    private static Trial Tally(string arm, int i, CheckpointI.Bench b, string task, int criteria, int rework, long ms)
    {
        var attempts = RunEngine.ForTask(b.Db, task);
        int falseCount = 0;
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = b.Db, Pooling = false, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString()))
        {
            db.Open();
            using var q = db.CreateCommand();
            q.CommandText = "SELECT r.recommendation, r.comment, o.text FROM reviews r JOIN outputs o ON o.id=r.output_id";
            using var rd = q.ExecuteReader();
            while (rd.Read()) if (FalseFinding(rd.GetString(0), rd.GetString(1), rd.GetString(2))) falseCount++;
        }
        return new(arm, i, criteria, rework, attempts.Count(a => a.Role == "reviewer"), falseCount, ms, attempts.Sum(a => a.InputTokens ?? 0), attempts.Sum(a => a.OutputTokens ?? 0),
            attempts.Sum(a => a.Cost ?? 0), attempts.Count(a => a.Cost == null));
    }

    public static async Task<string> Measure(string folder, string? endpoint, string builder, string reviewer, int samples)
    {
        Directory.CreateDirectory(folder);
        var trials = new List<Trial>();
        for (int i = 0; i < samples; i++)
        {
            trials.Add(await Single(Path.Combine(folder, $"single-{i}"), endpoint, builder, reviewer, i, endpoint == null ? Scripted(i) : null));
            trials.Add(await Team(Path.Combine(folder, $"team-{i}"), endpoint, builder, reviewer, i, endpoint == null ? Scripted(i) : null));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Brief {BriefVersion} · {(endpoint == null ? "scripted models" : $"real local models: builder {builder}, reviewer {reviewer}")} · samples per arm: {samples}");
        sb.AppendLine("| Arm | Criteria met (of 4, mean) | Fully met | Rework (revisions after the first, mean) | Reviewer false findings / verdicts | Elapsed s (mean) | Tokens in / out (mean) | Known spend | Unknown charges |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var g in trials.GroupBy(t => t.Arm))
        {
            var l = g.ToList();
            sb.AppendLine($"| {g.Key} | {l.Average(t => t.Criteria):0.00} | {l.Count(t => t.Criteria == 4)}/{l.Count} | {l.Average(t => t.Revisions):0.00} | {l.Sum(t => t.FalseFindings)}/{l.Sum(t => t.ReviewerRuns)} | {l.Average(t => t.ElapsedMs) / 1000:0.0} | {l.Average(t => t.InputTokens):0} / {l.Average(t => t.OutputTokens):0} | {l.Sum(t => t.KnownSpend):0.0000} | {l.Sum(t => t.UnknownCharges)} |");
        }
        sb.AppendLine(endpoint == null
            ? "Limits: the models are scripted. The builder omits one change on its first draft by design, and the reviewer gives one false finding every third trial by design, so the false-finding count measures the harness and says nothing about any model."
            : "Limits: one brief, three supplied changes, local models with zero price (spend is $0 by construction), a scripted owner who passes the reviewer's reason on once, and a small sample. Differences smaller than a trial are noise.");
        File.WriteAllText(Path.Combine(folder, "bench.md"), sb.ToString());
        File.WriteAllText(Path.Combine(folder, "bench.json"), JsonSerializer.Serialize(trials, new JsonSerializerOptions { WriteIndented = true }));
        return sb.ToString();
    }

    public static async Task Run(string dir)
    {
        string report = await Measure(Path.Combine(dir, "bench"), null, "bench-builder:1", "bench-reviewer:1", 6);
        var one = JsonSerializer.Deserialize<List<Trial>>(File.ReadAllText(Path.Combine(dir, "bench", "bench.json")))!;
        var single = one.Where(t => t.Arm == "single agent").ToList();
        var team = one.Where(t => t.Arm == "team").ToList();
        Console.WriteLine(report);
        Check(single.Count == 6 && team.Count == 6 && team.Average(t => t.Criteria) > single.Average(t => t.Criteria) && team.All(t => t.UnknownCharges == 0) && team.Sum(t => t.FalseFindings) == 2 && single.All(t => t.ReviewerRuns == 0),
              "the harness runs the same brief, files and rubric with one agent and with the team, and records criteria, rework, false reviewer findings, time, tokens and spend");
        Check(Score("- Added dark mode\n- Fixed crash on export\n- Faster startup") == 4 && Score("Sure!\n- Added dark mode") == 1 && FalseFinding("request_changes", "Faster startup is missing", "- Faster startup") && !FalseFinding("approve_recommended", "Faster startup is missing", "- Faster startup") && !FalseFinding("request_changes", "Faster startup is missing", "- Added dark mode"),
              "the rubric counts completed criteria, and a false finding is a request for a change the draft already contains");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }
}
