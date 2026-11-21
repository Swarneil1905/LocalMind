using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalMind.AgentHub.Core;

public sealed record DiagAttempt(string AttemptId, string RunId, string TaskId, string Role, string Provider, string RequestedModel, string? ReportedModel, string Status, string? ErrorCode,
                                 string CreatedAt, string? SentAt, string? EndedAt, long? DurationMs, int Steps, int ToolCalls, int? InputTokens, int? OutputTokens, double? Estimate, double? Cost,
                                 string? Currency, string? PriceStatus, string? RetryOf, string CommandId, string? Resolution, string? Content);
public sealed record DiagEvent(long Seq, string At, string Kind, string TaskId, string? From, string? To, string CommandId, int Schema);
public sealed record DiagLedger(string AttemptId, string Kind, double? Amount, string Currency, string At);
public sealed record DiagCounters(int Attempts, int InputTokens, int OutputTokens, double KnownCost, int UnknownCharges, double Reserved, IReadOnlyDictionary<string, int> ByStatus);
public sealed record DiagnosticReport(string GeneratedAt, int Schema, bool IncludesContent, string Redaction, IReadOnlyList<DiagAttempt> Attempts, IReadOnlyList<DiagEvent> Events,
                                      IReadOnlyList<DiagLedger> Ledger, DiagCounters Counters);

/// <summary>
/// Takes anything that looks like a credential, a header value or a file path out of free text. This is the second line of defence: by default a report
/// carries only IDs, codes, numbers and states, so provider error text, tool output, paths and headers are never in it to begin with.
/// </summary>
public static class Redactor
{
    private static readonly (Regex Pattern, string With)[] Rules =
    [
        (new(@"(?im)^\s*(authorization|proxy-authorization|cookie|set-cookie|x-api-key|api-key|x-[a-z-]*(token|secret|key))\s*:.*$", RegexOptions.Compiled), "$1: [header removed]"),
        (new(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{6,}", RegexOptions.Compiled), "Bearer [removed]"),
        (new(@"\b(sk|pk|rk|ghp|gho|xox[bp])-[A-Za-z0-9._-]{6,}", RegexOptions.Compiled), "[key removed]"),
        (new(@"(?i)\b(api[_-]?key|secret|token|password|passwd)\s*[=:]\s*\S+", RegexOptions.Compiled), "$1=[removed]"),
        (new(@"[A-Za-z]:\\[^\s""'<>|]+", RegexOptions.Compiled), "[path removed]"),
        (new(@"(?<![\w.])/(?:home|Users|var|tmp|etc|opt|mnt|srv|root)/[^\s""'<>|]+", RegexOptions.Compiled), "[path removed]"),
        (new(@"\\\\[^\s""'<>|\\]+\\[^\s""'<>|]+", RegexOptions.Compiled), "[path removed]"),
        (new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled), "[email removed]"),
        (new(@"(?<=://)[^/\s:@]+:[^/\s@]+@", RegexOptions.Compiled), "[credentials removed]@"),
        (new(@"(?<=\?)[^\s""'#]+", RegexOptions.Compiled), "[query removed]"),
    ];

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var (p, w) in Rules) text = p.Replace(text, w);
        return text;
    }
}

public static class RunDiagnostics
{
    public const string Policy = "By default this report holds IDs, codes, states, timings and counters. It holds no project text, no provider or tool messages, no paths and no headers.";

    /// <summary>Everything needed to trace one attempt (or all of them) from request to outcome. Content is added only when the owner asks, and is cleaned first.</summary>
    public static DiagnosticReport Build(string workspace, string? attemptId = null, bool includeContent = false)
    {
        using var db = Reader.Open(workspace);
        string where = attemptId == null ? "" : "WHERE a.id=$a";
        var args = attemptId == null ? [] : new[] { ("$a", (object?)attemptId) };
        var attempts = Reader.Rows(db, $"""
            SELECT a.id,a.run_id,a.task_id,a.role,a.provider,a.requested_model,a.reported_model,a.status,a.error_code,a.created_at,a.sent_at,a.ended_at,a.steps,a.tool_calls,
                   a.input_tokens,a.output_tokens,a.estimate,a.cost,a.currency,a.price_status,a.retry_of,a.command_id,r.choice,a.partial_text,a.packet_text
            FROM attempts a LEFT JOIN reconciliations r ON r.attempt_id=a.id {where} ORDER BY a.created_at, a.rowid
            """, args).Select(r =>
        {
            long? ms = r[10] is string s && r[11] is string e && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var from) && DateTime.TryParse(e, null, System.Globalization.DateTimeStyles.RoundtripKind, out var to) ? (long)(to - from).TotalMilliseconds : null;
            return new DiagAttempt(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.S(r[4]), Reader.S(r[5]), r[6] as string, Reader.S(r[7]), r[8] as string, Reader.S(r[9]), r[10] as string, r[11] as string,
                ms, Reader.I(r[12]), Reader.I(r[13]), r[14] is null ? null : Reader.I(r[14]), r[15] is null ? null : Reader.I(r[15]), r[16] is null ? null : Convert.ToDouble(r[16]), r[17] is null ? null : Convert.ToDouble(r[17]),
                r[18] as string, r[19] as string, r[20] as string, Reader.S(r[21]), r[22] as string,
                includeContent ? Redactor.Clean("input: " + Reader.S(r[24]) + "\noutput so far: " + Reader.S(r[23])) : null);
        }).ToList();
        var tasks = attempts.Select(a => a.TaskId).Distinct().Where(t => t.Length > 0).ToList();
        string inList = tasks.Count == 0 ? "''" : string.Join(',', tasks.Select(t => "'" + t.Replace("'", "''") + "'"));
        var events = Reader.Rows(db, $"SELECT seq, created_at, kind, task_id, from_state, to_state, command_id, event_schema FROM events WHERE task_id IN ({inList}) ORDER BY seq")
            .Select(r => new DiagEvent(Convert.ToInt64(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), r[4] as string, r[5] as string, Reader.S(r[6]), Reader.I(r[7]))).ToList();
        string ids = attempts.Count == 0 ? "''" : string.Join(',', attempts.Select(a => "'" + a.AttemptId + "'"));
        var ledger = Reader.Rows(db, $"SELECT attempt_id, kind, amount, currency, created_at FROM budget_ledger WHERE attempt_id IN ({ids}) ORDER BY created_at, rowid")
            .Select(r => new DiagLedger(Reader.S(r[0]), Reader.S(r[1]), r[2] is null ? null : Convert.ToDouble(r[2]), Reader.S(r[3]), Reader.S(r[4]))).ToList();
        var counters = new DiagCounters(attempts.Count, attempts.Sum(a => a.InputTokens ?? 0), attempts.Sum(a => a.OutputTokens ?? 0), attempts.Sum(a => a.Cost ?? 0), ledger.Count(l => l.Kind == "settle" && l.Amount == null),
            // The budget view's rule: a reservation counts until its attempt is released or settled at a known amount.
            ledger.Where(l => l.Kind == "reserve" && l.Amount != null && !ledger.Any(x => x.AttemptId == l.AttemptId && (x.Kind == "release" || (x.Kind == "settle" && x.Amount != null)))).Sum(l => l.Amount!.Value), attempts.GroupBy(a => a.Status).ToDictionary(g => g.Key, g => g.Count()));
        return new(DateTime.UtcNow.ToString("O"), ProjectStore.CurrentSchema, includeContent, includeContent ? "Content included after cleaning; " + Policy : Policy, attempts, events, ledger, counters);
    }

    public static string ToText(DiagnosticReport r) => JsonSerializer.Serialize(r, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true });

    /// <summary>Writes the report to a local file. Nothing in this class opens a network connection.</summary>
    public static string Export(string workspace, string? attemptId, bool includeContent, string target)
    {
        string text = ToText(Build(workspace, attemptId, includeContent));
        File.WriteAllText(target, text);
        return target;
    }
}
