using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub.Core;

public sealed record DispatchOptions(bool AcceptPriceRisk = false, bool AcceptRepeatCost = false, string? Instruction = null, string? InstructionId = null, string? RetryOf = null);

/// <summary>What a dispatch would send, to whom, and what stands in the way, before anything leaves this computer.</summary>
public sealed record DispatchPreview(string Role, string ProjectId, string TaskId, string? ConnectionId, string? ConnectionLabel, string? Provider, string? Endpoint, bool IsCloud,
                                     string? Model, string PacketText, IReadOnlyList<string> FileNames, int PacketTokens, int MaxOutputTokens, string PriceStatus,
                                     double? Estimate, string Currency, string ConsentStatus, string? Blocker, BudgetStatus Budget);

public sealed record AttemptView(string Id, string RunId, string ProjectId, string TaskId, string Role, string ConnectionId, string Provider, string RequestedModel,
                                 string? ReportedModel, string? ReportedProvider, string? RouteNote, string Status, string CreatedAt, string? SentAt, string? EndedAt,
                                 string? StopReason, string? ErrorCode, string PartialText, string PacketText, int Steps, int ToolCalls, int? InputTokens, int? OutputTokens,
                                 double? Estimate, double? Cost, string? Currency, string? PriceStatus, string? OutputId, string? RetryOf, int? Revision, string? InstructionId)
{
    public bool Active => Status is "leased" or "sending" or "streaming";
}

/// <summary>
/// Runs model attempts inside the worker. Dispatch checks policy, consent, price and budget and takes a lease in one transaction;
/// the call runs in the background and writes progress to the attempt row, which every window reads. A result enters the same
/// output, review and event flow as the offline fixtures.
/// </summary>
public sealed class RunEngine(ICredentialVault vault, Func<string> newId)
{
    public static TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);
    public string Owner { get; } = "engine-" + Guid.NewGuid().ToString("N")[..8];
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, Task Task)> running = new();
    private static string Now => DateTime.UtcNow.ToString("O");
    private static string Later(TimeSpan t) => DateTime.UtcNow.Add(t).ToString("O");
    private static StoreException Bad(string code, string message, string recovery) => new(code, message, recovery);

    /// <summary>Lets a check wait for an attempt's background work to end.</summary>
    public Task Completion(string attemptId) => running.TryGetValue(attemptId, out var r) ? r.Task : Task.CompletedTask;

    public static string SystemPrompt(string role) => role switch
    {
        "builder" => "You are the builder on a small team. Produce the deliverable the task asks for, using only the material in the handoff. "
                     + "Write the deliverable itself with no preamble. If the handoff contains reviewer feedback or an instruction from the owner, apply it.",
        "reviewer" => "You are the reviewer. Check the output under review against the task's criteria and any supplied files. "
                      + "Your first line must be exactly APPROVE or REQUEST CHANGES. Then give one or two sentences with the specific reason, naming anything missing or wrong.",
        "planner" => "You are the planner. List the steps needed to complete the task, one per line, using only the handoff.",
        _ => "Answer briefly.",
    };

    internal IModelClient Client(string provider, string endpoint, string? credentialRef) => provider switch
    {
        "ollama" => new OllamaClient(endpoint),
        "openrouter" => new GatewayClient(endpoint, () => credentialRef == null ? null : vault.Read(credentialRef)),
        _ => throw Bad("unknown_provider", $"“{provider}” is not supported.", "Use Ollama or OpenRouter."),
    };

    // --- Preview ------------------------------------------------------------------------------------------------
    public DispatchPreview Preview(string workspace, string taskId, string role, string? instruction = null)
    {
        using var db = Reader.Open(workspace);
        return Check(db, taskId, role, instruction, new DispatchOptions(Instruction: instruction)).Preview;
    }

    private sealed record Checked(DispatchPreview Preview, Packet Packet, ProjectSettings Settings, string? CredentialRef, Price? Price);

    /// <summary>Everything a dispatch depends on, read in one place. A blocker is a reason it must not go ahead.</summary>
    private static Checked Check(SqliteConnection db, string taskId, string role, string? instruction, DispatchOptions o)
    {
        var task = TaskStore.Task(db, taskId);
        var settings = Team.Settings(db, task.ProjectId);
        var budget = Team.Budget(db, task.ProjectId);
        var a = Reader.Rows(db, "SELECT connection_id, model_id FROM assignments WHERE project_id=$p AND role=$r", ("$p", task.ProjectId), ("$r", role)).FirstOrDefault();
        var packet = Handoff.Build(db, task.ProjectId, taskId, role, Limits.DefaultPacketTokens, instruction);
        int tokens = Reader.Tokens(SystemPrompt(role) + packet.Text);
        var files = Handoff.FileNames(packet);
        if (a == null)
            return new(new(role, task.ProjectId, taskId, null, null, null, null, false, null, packet.Text, files, tokens, settings.MaxOutputTokens, "none", null, settings.Currency,
                "not_needed", $"No model is assigned to the {role} for this project. Choose one in Team.", budget), packet, settings, null, null);
        string connId = Reader.S(a[0]), model = Reader.S(a[1]);
        var c = Team.Connection(db, connId);
        var (price, priceStatus) = Team.PriceFor(db, connId, model, c.IsCloud);
        double? estimate = price == null ? null : tokens / 1e6 * price.InputPerMTok + settings.MaxOutputTokens / 1e6 * price.OutputPerMTok;
        string consent = !c.IsCloud ? "not_needed" : settings.LocalOnly ? "blocked_local_only"
            : Team.ActiveConsent(db, task.ProjectId, connId, model) is { } k ? (files.All(k.Files.Contains) ? "granted" : "files_not_covered") : "required";
        bool available = TaskStore.Scalar<long>(db, "SELECT count(*) FROM connection_models WHERE connection_id=$c AND model_id=$m AND available=1", ("$c", connId), ("$m", model)) > 0;
        string? blocker =
            !available ? $"{model} was not listed by {c.Label} at the last probe. Probe again or choose another model."
            : consent == "blocked_local_only" ? "This project is local-only. Nothing may be sent to a cloud model."
            : consent == "required" ? $"The project has not agreed to send work to {model} on {c.Label}. Preview it and give consent."
            : consent == "files_not_covered" ? $"The consent for {model} does not cover every file in this packet ({string.Join(", ", files)}). Give consent again for these files."
            : c.IsCloud && settings.BudgetLimit == null ? "This project has no spending limit, so it cannot use a paid model. Set a budget in Team."
            : priceStatus is "unknown" or "stale" && !o.AcceptPriceRisk ? $"The price for {model} is {priceStatus}. Record a price, or choose to go ahead without a reliable estimate."
            : estimate is { } e && budget.Remaining is { } left && e > left ? $"The estimate ({e:0.0000} {settings.Currency}) is more than the {left:0.0000} {settings.Currency} left in the budget."
            : null;
        return new(new(role, task.ProjectId, taskId, connId, c.Label, c.Provider, c.Endpoint, c.IsCloud, model, packet.Text, files, tokens, settings.MaxOutputTokens, priceStatus,
            estimate, price?.Currency ?? settings.Currency, consent, blocker, budget), packet, settings, c.CredentialRef, price);
    }

    // --- Dispatch -----------------------------------------------------------------------------------------------
    /// <summary>
    /// Takes the lease and the budget reservation in one transaction, then starts the call in the background. Two dispatches for the same
    /// task and role cannot both win: the second finds the first attempt's lease.
    /// </summary>
    public CommandResult Dispatch(string workspace, string commandId, string taskId, string role, DispatchOptions o)
    {
        Recover(workspace);
        var result = TaskStore.Run(workspace, commandId, "attempt_started", db =>
        {
            if (!Team.Roles.Contains(role)) throw Bad("unknown_role", $"“{role}” is not a role.", "Use planner, builder or reviewer.");
            var active = Reader.Rows(db, "SELECT id, status FROM attempts WHERE task_id=$t AND role=$r AND status IN ('leased','sending','streaming','needs_reconciliation')", ("$t", taskId), ("$r", role)).FirstOrDefault();
            if (active != null && Reader.S(active[1]) != "needs_reconciliation")
                throw Bad("attempt_active", $"The {role} is already working on this task (attempt {Reader.S(active[0])[..8]}).", "Wait for it, or pause or cancel it first.");
            if (active != null && !o.AcceptRepeatCost)
                throw Bad("needs_reconciliation", "An earlier attempt may already have been accepted by the provider. Starting again could cost twice.", "Check the provider, then choose to start again anyway.");
            var ck = Check(db, taskId, role, o.Instruction, o);
            if (ck.Preview.Blocker is { } why) throw Bad(ck.Preview.ConsentStatus switch { "blocked_local_only" => "local_only", "required" or "files_not_covered" => "consent_required", _ => "blocked" }, why, "Open Team to fix it.");
            var p = ck.Preview;
            if (role == "reviewer" && TaskStore.Scalar<long>(db, "SELECT count(*) FROM outputs WHERE task_id=$t", ("$t", taskId)) == 0)
                throw Bad("no_output", "There is no output to review yet.", "Ask the builder first.");
            string id = newId(), run = newId();
            TaskStore.Exec(db, "INSERT INTO runs(id,task_id,role,created_at,packet_project,packet_limit,packet_tokens,packet_text,packet_sources) VALUES($r,$t,$o,$at,$p,$l,$k,$x,$s)",
                ("$r", run), ("$t", taskId), ("$o", role), ("$at", Now), ("$p", p.ProjectId), ("$l", ck.Packet.TokenLimit), ("$k", ck.Packet.TokensUsed), ("$x", ck.Packet.Text),
                ("$s", JsonSerializer.Serialize(ck.Packet.Sections.Where(x => x.SourceId != null).Select(x => new { id = x.SourceId, revision = x.SourceRevision }))));
            TaskStore.Exec(db, """
                INSERT INTO attempts(id,command_id,run_id,project_id,task_id,role,connection_id,provider,requested_model,status,lease_owner,lease_expires,created_at,packet_text,estimate,currency,price_id,price_status,instruction_id,retry_of)
                VALUES($i,$cmd,$run,$p,$t,$role,$c,$prov,$m,'leased',$own,$exp,$at,$x,$e,$cur,$pid,$ps,$ins,$re)
                """, ("$i", id), ("$cmd", commandId), ("$run", run), ("$p", p.ProjectId), ("$t", taskId), ("$role", role), ("$c", p.ConnectionId), ("$prov", p.Provider), ("$m", p.Model),
                ("$own", Owner), ("$exp", Later(Lease)), ("$at", Now), ("$x", p.PacketText), ("$e", p.Estimate), ("$cur", p.Currency), ("$pid", ck.Price?.Id), ("$ps", p.PriceStatus),
                ("$ins", o.InstructionId), ("$re", o.RetryOf));
            if (p.Estimate is { } amount && amount > 0)
                TaskStore.Exec(db, "INSERT INTO budget_ledger VALUES($i,$p,$a,'reserve',$amt,$cur,$at)", ("$i", newId()), ("$p", p.ProjectId), ("$a", id), ("$amt", amount), ("$cur", p.Currency), ("$at", Now));
            return new(id, TaskStore.Task(db, taskId).Revision, taskId, null, null, $"{role}: {p.Model} on {p.ConnectionLabel}, {p.PacketTokens} tokens sent" + (p.IsCloud ? " to the cloud" : " locally"));
        });
        if (!result.Duplicate) Start(workspace, result.Id);
        return result;
    }

    private void Start(string workspace, string attemptId)
    {
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => Execute(workspace, attemptId, cts.Token));
        running[attemptId] = (cts, task);
        _ = task.ContinueWith(_ => { running.TryRemove(attemptId, out var _r); cts.Dispose(); }, TaskScheduler.Default);
    }

    // --- Owner controls -----------------------------------------------------------------------------------------
    public CommandResult Cancel(string workspace, string commandId, string attemptId) => Stop(workspace, commandId, attemptId, "cancelled", "Cancelled by the owner. Anything that arrives later is ignored.");

    public CommandResult Pause(string workspace, string commandId, string attemptId) => Stop(workspace, commandId, attemptId, "paused", "Paused by the owner. Resume starts a new attempt; what was produced is kept.");

    private CommandResult Stop(string workspace, string commandId, string attemptId, string status, string reason)
    {
        var r = TaskStore.Run(workspace, commandId, "attempt_" + status, db =>
        {
            var a = Attempt(db, attemptId);
            if (!a.Active) throw Bad("not_running", $"That attempt is {a.Status.Replace('_', ' ')}, not running.", "Reload the run inspector.");
            // Guarded by status: a late token or result can no longer change this attempt.
            TaskStore.Exec(db, "UPDATE attempts SET status=$s, stop_reason=$why, ended_at=$at WHERE id=$i AND status IN ('leased','sending','streaming')",
                ("$s", status), ("$why", reason), ("$at", Now), ("$i", attemptId));
            Settle(db, a, a.SentAt == null ? null : (double?)null, a.SentAt == null);
            return new(attemptId, 0, a.TaskId, null, null, reason);
        });
        if (running.TryGetValue(attemptId, out var run)) run.Cts.Cancel();
        return r;
    }

    /// <summary>Starts a new attempt for a paused or failed one. One eligible attempt at most: the lease rule and the command ID see to it.</summary>
    public CommandResult Resume(string workspace, string commandId, string attemptId, bool acceptRepeatCost)
    {
        AttemptView a;
        using (var db = Reader.Open(workspace)) a = Attempt(db, attemptId);
        if (a.Active) throw Bad("attempt_active", "That attempt is still running.", "Wait for it, or pause it first.");
        bool mayHaveCost = a.Status is "interrupted" or "needs_reconciliation" || (a.Status is "paused" or "cancelled" && a.SentAt != null && a.Provider != "ollama");
        if (mayHaveCost && !acceptRepeatCost)
            throw Bad("repeat_cost", "The earlier request may already have been charged. Another one could cost again.", "Choose to retry anyway if that is acceptable.");
        return Dispatch(workspace, commandId, a.TaskId, a.Role, new(AcceptPriceRisk: true, AcceptRepeatCost: acceptRepeatCost, RetryOf: attemptId));
    }

    /// <summary>
    /// After a crash or restart: an attempt whose lease expired before it was sent can be started again safely; one that was already sending
    /// may have been accepted by the provider, so it is kept for reconciliation with its partial output.
    /// </summary>
    public int Recover(string workspace)
    {
        string path = ProjectStore.DatabaseIn(workspace);
        using var db = ProjectStore.Connect(path, SqliteOpenMode.ReadWrite);
        db.Open();
        TaskStore.Exec(db, "BEGIN IMMEDIATE");
        try
        {
            var stale = Reader.Rows(db, "SELECT id, status FROM attempts WHERE status IN ('leased','sending','streaming') AND lease_expires < $now", ("$now", Now));
            foreach (var s in stale)
            {
                string id = Reader.S(s[0]);
                if (running.ContainsKey(id)) continue; // still ours and alive
                var a = Attempt(db, id);
                bool sent = Reader.S(s[1]) != "leased";
                TaskStore.Exec(db, "UPDATE attempts SET status=$st, stop_reason=$why, ended_at=$at WHERE id=$i", ("$st", sent ? "needs_reconciliation" : "abandoned"), ("$at", Now), ("$i", id),
                    ("$why", sent ? "The lease expired while the request was out. The provider may have accepted it, so the partial output is kept and starting again needs your choice."
                                  : "The lease expired before anything was sent. It can be started again safely."));
                Settle(db, a, null, !sent);
            }
            TaskStore.Exec(db, "COMMIT");
            return stale.Count;
        }
        catch { TaskStore.Exec(db, "ROLLBACK"); throw; }
    }

    // --- Execution ----------------------------------------------------------------------------------------------
    private async Task Execute(string workspace, string attemptId, CancellationToken cancel)
    {
        string path = ProjectStore.DatabaseIn(workspace);
        AttemptView a; ProjectSettings settings; (string Provider, string Endpoint, bool IsCloud, string? CredentialRef, string Label) conn; IReadOnlyList<(string Name, string Content)> files;
        bool offerTools;
        using (var db = Reader.Open(workspace))
        {
            a = Attempt(db, attemptId);
            settings = Team.Settings(db, a.ProjectId);
            conn = Team.Connection(db, a.ConnectionId);
            files = Team.Files(db, a.TaskId).Where(f => a.PacketText.Contains($"Supplied file “{f.Name}”")).ToList();
            var caps = Reader.Rows(db, "SELECT capabilities FROM connection_models WHERE connection_id=$c AND model_id=$m", ("$c", a.ConnectionId), ("$m", a.RequestedModel)).FirstOrDefault();
            // The file tool is offered only when there are admitted files and the model is not known to lack tools.
            offerTools = files.Count > 0 && (caps == null || JsonSerializer.Deserialize<ModelCapabilities>(Reader.S(caps[0]), Protocol.Json)?.Tools != false);
        }
        var client = Client(conn.Provider, conn.Endpoint, conn.CredentialRef);
        var messages = new List<ChatMessage> { new("system", SystemPrompt(a.Role)), new("user", a.PacketText) };
        var clock = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(settings.MaxSeconds);
        var text = new StringBuilder();
        int steps = 0, toolCalls = 0, inTok = 0, outTok = 0;
        bool usageKnown = true;
        decimal? reportedCost = null;
        string? reportedModel = null, reportedProvider = null;
        using var timeout = new CancellationTokenSource(limit);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
        string? stopReason = null;
        try
        {
            while (true)
            {
                if (steps >= settings.MaxSteps) { stopReason = $"Stopped at the {settings.MaxSteps}-step limit."; break; }
                steps++;
                Write(path, "UPDATE attempts SET status='sending', sent_at=COALESCE(sent_at,$at), steps=$s, lease_expires=$exp WHERE id=$i AND status IN ('leased','sending','streaming')",
                    ("$at", Now), ("$s", steps), ("$exp", Later(Lease)), ("$i", attemptId));
                if (!Live(path, attemptId)) return;
                var flush = Stopwatch.StartNew();
                string before = text.ToString();
                ChatResult result;
                try
                {
                    result = await client.ChatAsync(new(a.RequestedModel, messages, settings.MaxOutputTokens, offerTools, settings.OnlyProviders), piece =>
                    {
                        text.Append(piece);
                        if (flush.ElapsedMilliseconds < 200) return;
                        flush.Restart();
                        Write(path, "UPDATE attempts SET status='streaming', partial_text=$x, lease_expires=$exp WHERE id=$i AND status IN ('sending','streaming')",
                            ("$x", text.ToString()), ("$exp", Later(Lease)), ("$i", attemptId));
                    }, both.Token);
                }
                catch (ModelException e) when (e.Code == "throttled" && e.RetryAfter is { } wait && clock.Elapsed + wait < limit)
                {
                    // Refused before generation: nothing was charged. Wait as asked, inside the run's own time limit.
                    steps--;
                    Write(path, "UPDATE attempts SET stop_reason=$why WHERE id=$i", ("$why", $"Throttled; retrying after {Math.Ceiling(wait.TotalSeconds)} s as the provider asked."), ("$i", attemptId));
                    await Task.Delay(wait, both.Token);
                    continue;
                }
                if (!Live(path, attemptId)) return; // cancelled or paused while waiting: the result is ignored
                reportedModel ??= result.ReportedModel;
                reportedProvider ??= result.ReportedProvider;
                if (result.InputTokens is { } i && result.OutputTokens is { } o) { inTok += i; outTok += o; } else usageKnown = false;
                if (result.ReportedCost is { } rc) reportedCost = (reportedCost ?? 0) + rc;
                messages.Add(new("assistant", result.Text, null, result.ToolCalls.Count > 0 ? result.ToolCalls : null));
                if (result.ToolCalls.Count == 0) break;
                toolCalls += result.ToolCalls.Count;
                if (toolCalls > settings.MaxToolCalls) { stopReason = $"Stopped at the {settings.MaxToolCalls}-tool-call limit."; break; }
                foreach (var call in result.ToolCalls)
                    messages.Add(new("tool", ToolAnswer(call, files), call.Id));
                Write(path, "UPDATE attempts SET tool_calls=$k, transcript=$t WHERE id=$i", ("$k", toolCalls), ("$t", Transcript(messages)), ("$i", attemptId));
                text.Clear();
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            stopReason = $"Stopped at the {settings.MaxSeconds}-second limit.";
        }
        catch (OperationCanceledException) { return; } // the owner cancelled or paused; their command already recorded it
        catch (ModelException e)
        {
            Finish(path, attemptId, e.BeforeGeneration ? "failed" : "interrupted", e.Message
                + (e.BeforeGeneration ? " Nothing was generated, so nothing was charged." : " What arrived is kept; another request could cost again."),
                e.Code, e.Partial.Length > 0 ? e.Partial : text.ToString(), messages, steps, toolCalls, null, null, reportedModel, reportedProvider, null, e.BeforeGeneration, a);
            if (e.Code == "model_unavailable")
            {
                using var db = ProjectStore.Connect(path, SqliteOpenMode.ReadWrite);
                db.Open();
                Team.MarkUnavailable(db, a.ConnectionId, a.RequestedModel);
            }
            return;
        }
        catch (Exception e) when (e is VaultUnavailableException)
        {
            Finish(path, attemptId, "failed", e.Message, "vault_unavailable", "", messages, steps, toolCalls, null, null, null, null, null, true, a);
            return;
        }
        if (stopReason != null)
        {
            Finish(path, attemptId, "stopped", stopReason, "limit", text.ToString(), messages, steps, toolCalls, usageKnown ? inTok : null, usageKnown ? outTok : null, reportedModel, reportedProvider, reportedCost, false, a);
            return;
        }
        Complete(path, workspace, attemptId, text.ToString(), messages, steps, toolCalls, usageKnown ? inTok : null, usageKnown ? outTok : null, reportedModel, reportedProvider, reportedCost, a);
    }

    /// <summary>Only files admitted to this packet can be read. Nothing else, local or not, is reachable through the model.</summary>
    private static string ToolAnswer(ToolCall call, IReadOnlyList<(string Name, string Content)> files)
    {
        if (call.Name != "read_file") return $"There is no tool named {call.Name}.";
        string? name = null;
        try { name = JsonNode.Parse(call.Arguments)?["name"]?.GetValue<string>(); } catch (JsonException) { }
        return files.FirstOrDefault(f => f.Name == name) is { Name: not null } f ? f.Content : $"“{name}” is not one of the files supplied with this handoff, so it cannot be read.";
    }

    private static string Transcript(List<ChatMessage> messages) => JsonSerializer.Serialize(messages.Select(m => new { role = m.Role, content = m.Content.Length > 4000 ? m.Content[..4000] + "…" : m.Content, tools = m.ToolCalls?.Select(t => t.Name + " " + t.Arguments) }));

    private static string? RouteNote(AttemptView a, string? reportedModel, string? reportedProvider, IReadOnlyList<string>? only)
    {
        var notes = new List<string>();
        if (reportedModel == null) notes.Add("The provider did not report which model answered.");
        else if (!string.Equals(reportedModel, a.RequestedModel, StringComparison.OrdinalIgnoreCase)) notes.Add($"Unexpected route: {reportedModel} answered instead of {a.RequestedModel}.");
        if (a.Provider == "openrouter" && reportedProvider == null) notes.Add("The gateway did not report the upstream provider.");
        if (only is { Count: > 0 } && reportedProvider != null && !only.Contains(reportedProvider, StringComparer.OrdinalIgnoreCase)) notes.Add($"Unexpected route: {reportedProvider} is not one of the allowed providers.");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private void Complete(string path, string workspace, string attemptId, string text, List<ChatMessage> messages, int steps, int toolCalls, int? inTok, int? outTok,
                          string? reportedModel, string? reportedProvider, decimal? reportedCost, AttemptView a)
    {
        ProjectSettings settings;
        using (var db = Reader.Open(workspace)) settings = Team.Settings(db, a.ProjectId);
        string? route = RouteNote(a, reportedModel, reportedProvider, settings.OnlyProviders);
        bool unexpected = route?.Contains("Unexpected route") == true;
        string kind = a.Role == "builder" ? "output_proposed" : a.Role == "reviewer" ? "review_recorded" : "attempt_finished";
        CommandResult? done = null;
        try
        {
            done = TaskStore.Run(workspace, "finish-" + attemptId, kind, db =>
            {
                var now = Attempt(db, attemptId);
                if (!now.Active) throw Bad("late_result", "The attempt was stopped before its result arrived.", "Nothing to do.");
                string label = Team.Connection(db, a.ConnectionId).Label;
                double? cost = reportedCost is { } rc ? (double)rc : now.PriceStatus == "local" ? 0 : inTok is { } i && outTok is { } o ? Cost(db, now, i, o) : null;
                string outputId = "", detail;
                int revision;
                if (a.Role == "builder")
                {
                    int n = (TaskStore.Scalar<int?>(db, "SELECT max(revision) FROM outputs WHERE task_id=$t", ("$t", a.TaskId)) ?? 0) + 1;
                    outputId = newId();
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    TaskStore.Exec(db, "INSERT INTO outputs(id,task_id,revision,text,author_role,accepted_by,created_at,run_id,content_hash,kind,byte_size) VALUES($i,$t,$n,$x,$a,NULL,$at,$r,$h,'text',$z)",
                        ("$i", outputId), ("$t", a.TaskId), ("$n", n), ("$x", text), ("$a", $"builder · {a.RequestedModel} ({label})"), ("$at", Now), ("$r", a.RunId),
                        ("$h", ProjectStore.Hash(bytes)), ("$z", bytes.Length));
                    revision = n;
                    detail = text;
                }
                else if (a.Role == "reviewer")
                {
                    var latest = Reader.Rows(db, "SELECT id, revision FROM outputs WHERE task_id=$t ORDER BY revision DESC LIMIT 1", ("$t", a.TaskId)).First();
                    revision = Reader.I(latest[1]);
                    // The review is about the revision that was in the packet; if a newer one arrived meanwhile, the review still names the one it read.
                    var reviewed = Reader.Rows(db, "SELECT id, revision FROM outputs WHERE task_id=$t AND $x LIKE '%revision ' || revision || ' · DRAFT%' ORDER BY revision DESC LIMIT 1", ("$t", a.TaskId), ("$x", a.PacketText)).FirstOrDefault() ?? latest;
                    revision = Reader.I(reviewed[1]);
                    var (rec, comment) = Recommendation(text);
                    outputId = newId();
                    TaskStore.Exec(db, "INSERT INTO reviews(id,output_id,recommendation,comment,reviewer_role,created_at,run_id) VALUES($i,$o,$r,$c,$who,$at,$run)",
                        ("$i", outputId), ("$o", Reader.S(reviewed[0])), ("$r", rec), ("$c", comment), ("$who", $"reviewer · {a.RequestedModel} ({label})"), ("$at", Now), ("$run", a.RunId));
                    detail = $"{rec}: {comment}";
                }
                else { revision = 0; detail = text; }
                TaskStore.Exec(db, """
                    UPDATE attempts SET status='completed', ended_at=$at, partial_text=$x, transcript=$tr, steps=$s, tool_calls=$k, input_tokens=$in, output_tokens=$out, cost=$cost,
                    reported_model=$rm, reported_provider=$rp, route_note=$route, output_id=$oid, revision=$rev, stop_reason=$why WHERE id=$i
                    """, ("$at", Now), ("$x", text), ("$tr", Transcript(messages)), ("$s", steps), ("$k", toolCalls), ("$in", inTok), ("$out", outTok), ("$cost", cost), ("$rm", reportedModel),
                    ("$rp", reportedProvider), ("$route", route), ("$oid", outputId), ("$rev", revision), ("$why", inTok == null ? "Finished. The provider did not report usage, so the cost is unknown." : "Finished."), ("$i", attemptId));
                Settle(db, now, cost, false);
                return new(outputId, revision, a.TaskId, null, null, detail);
            });
        }
        catch (StoreException e) when (e.Error.Code == "late_result") { return; }
        // The builder's revision goes to the reviewer on its own, with a different model, unless the route was not the one asked for.
        if (a.Role == "builder" && done is { Duplicate: false } && settings.AutoReview && !unexpected)
        {
            try
            {
                using var db = Reader.Open(workspace);
                var rev = Reader.Rows(db, "SELECT model_id FROM assignments WHERE project_id=$p AND role='reviewer'", ("$p", a.ProjectId)).FirstOrDefault();
                if (rev != null && Reader.S(rev[0]) != a.RequestedModel)
                    Dispatch(workspace, "chain-" + attemptId, a.TaskId, "reviewer", new());
                else if (rev != null)
                    Write(path, "UPDATE attempts SET route_note=COALESCE(route_note || ' ','') || 'The reviewer was not started: it uses the same model as the builder.' WHERE id=$i", ("$i", attemptId));
            }
            catch (StoreException e)
            {
                Write(path, "UPDATE attempts SET route_note=COALESCE(route_note || ' ','') || $n WHERE id=$i", ("$n", "The reviewer was not started: " + e.Message), ("$i", attemptId));
            }
        }
    }

    internal static (string Recommendation, string Comment) Recommendation(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim().Trim('*', '#', ' ')).Where(l => l.Length > 0).ToList();
        string first = lines.FirstOrDefault() ?? "";
        string rest = string.Join(" ", lines.Skip(1));
        string Clip(string s) => s.Length > 1500 ? s[..1500] + "…" : s;
        if (first.StartsWith("APPROVE", StringComparison.OrdinalIgnoreCase)) return ("approve_recommended", Clip(rest.Length > 0 ? rest : first));
        if (first.StartsWith("REQUEST CHANGES", StringComparison.OrdinalIgnoreCase) || first.StartsWith("REQUEST_CHANGES", StringComparison.OrdinalIgnoreCase))
        {
            string inline = first.Length > 15 ? first[15..].TrimStart(':', ' ', '.', '-') : "";
            return ("request_changes", Clip((inline + " " + rest).Trim() is { Length: > 0 } c ? c : "Changes requested without a reason."));
        }
        return ("unclear", Clip("The reviewer did not start with APPROVE or REQUEST CHANGES: " + string.Join(" ", lines)));
    }

    private static double? Cost(SqliteConnection db, AttemptView a, int inTok, int outTok)
    {
        if (a.PriceStatus == "local") return 0;
        string? priceId = TaskStore.Scalar<string>(db, "SELECT price_id FROM attempts WHERE id=$a", ("$a", a.Id));
        var p = priceId == null ? null : Reader.Rows(db, "SELECT input_per_mtok, output_per_mtok FROM prices WHERE id=$i", ("$i", priceId)).FirstOrDefault();
        return p == null ? null : inTok / 1e6 * Convert.ToDouble(p[0]) + outTok / 1e6 * Convert.ToDouble(p[1]);
    }

    private void Finish(string path, string attemptId, string status, string reason, string code, string partial, List<ChatMessage> messages, int steps, int toolCalls,
                        int? inTok, int? outTok, string? reportedModel, string? reportedProvider, decimal? reportedCost, bool release, AttemptView a)
    {
        try
        {
            TaskStore.Run(path, "finish-" + attemptId, "attempt_" + status, db =>
            {
                var now = Attempt(db, attemptId);
                if (!now.Active) throw Bad("late_result", "Already stopped.", "Nothing to do.");
                double? cost = reportedCost is { } rc ? (double)rc : now.PriceStatus == "local" ? 0 : inTok is { } i && outTok is { } o ? Cost(db, now, i, o) : null;
                TaskStore.Exec(db, """
                    UPDATE attempts SET status=$st, stop_reason=$why, error_code=$code, ended_at=$at, partial_text=$x, transcript=$tr, steps=$s, tool_calls=$k,
                    input_tokens=$in, output_tokens=$out, cost=$cost, reported_model=$rm, reported_provider=$rp WHERE id=$i
                    """, ("$st", status), ("$why", reason), ("$code", code), ("$at", Now), ("$x", partial), ("$tr", Transcript(messages)), ("$s", steps), ("$k", toolCalls),
                    ("$in", inTok), ("$out", outTok), ("$cost", cost), ("$rm", reportedModel), ("$rp", reportedProvider), ("$i", attemptId));
                Settle(db, now, cost, release);
                return new(attemptId, 0, a.TaskId, null, null, reason);
            });
        }
        catch (StoreException e) when (e.Error.Code == "late_result") { }
    }

    /// <summary>
    /// Closes the attempt's reservation once: released when nothing was sent, settled at the actual cost, or settled as unknown (kept visible).
    /// The ledger's unique (attempt, kind) means a repeated event can never charge twice.
    /// </summary>
    private void Settle(SqliteConnection db, AttemptView a, double? cost, bool release)
    {
        if (TaskStore.Scalar<long>(db, "SELECT count(*) FROM budget_ledger WHERE attempt_id=$a AND kind IN ('settle','release')", ("$a", a.Id)) > 0) return;
        if (release)
        {
            TaskStore.Exec(db, "INSERT INTO budget_ledger VALUES($i,$p,$a,'release',0,$c,$at)", ("$i", newId()), ("$p", a.ProjectId), ("$a", a.Id), ("$c", a.Currency ?? "USD"), ("$at", Now));
            return;
        }
        if (a.PriceStatus == "local") { TaskStore.Exec(db, "INSERT INTO budget_ledger VALUES($i,$p,$a,'settle',0,$c,$at)", ("$i", newId()), ("$p", a.ProjectId), ("$a", a.Id), ("$c", a.Currency ?? "USD"), ("$at", Now)); return; }
        TaskStore.Exec(db, "INSERT INTO budget_ledger VALUES($i,$p,$a,'settle',$amt,$c,$at)", ("$i", newId()), ("$p", a.ProjectId), ("$a", a.Id), ("$amt", cost), ("$c", a.Currency ?? "USD"), ("$at", Now));
    }

    // --- Small helpers ------------------------------------------------------------------------------------------
    private static bool Live(string path, string attemptId)
    {
        using var db = ProjectStore.Connect(path, SqliteOpenMode.ReadOnly);
        db.Open();
        return TaskStore.Scalar<string>(db, "SELECT status FROM attempts WHERE id=$i", ("$i", attemptId)) is "leased" or "sending" or "streaming";
    }

    private static void Write(string path, string sql, params (string, object?)[] args)
    {
        using var db = ProjectStore.Connect(path, SqliteOpenMode.ReadWrite);
        db.Open();
        TaskStore.Exec(db, sql, args);
    }

    internal static AttemptView Attempt(SqliteConnection db, string id) =>
        Attempts(db, "a.id=$k", id).FirstOrDefault() ?? throw Bad("not_found", "That attempt no longer exists.", "Reload the run inspector.");

    internal static List<AttemptView> Attempts(SqliteConnection db, string where, string key) =>
        [.. Reader.Rows(db, $"""
            SELECT id,run_id,project_id,task_id,role,connection_id,provider,requested_model,reported_model,reported_provider,route_note,status,created_at,sent_at,ended_at,
                   stop_reason,error_code,partial_text,packet_text,steps,tool_calls,input_tokens,output_tokens,estimate,cost,currency,price_status,output_id,retry_of,revision,instruction_id
            FROM attempts a WHERE {where} ORDER BY created_at, id
            """, ("$k", key)).Select(r => new AttemptView(Reader.S(r[0]), Reader.S(r[1]), Reader.S(r[2]), Reader.S(r[3]), Reader.S(r[4]), Reader.S(r[5]), Reader.S(r[6]), Reader.S(r[7]),
                r[8] as string, r[9] as string, r[10] as string, Reader.S(r[11]), Reader.S(r[12]), r[13] as string, r[14] as string, r[15] as string, r[16] as string,
                Reader.S(r[17]), Reader.S(r[18]), Reader.I(r[19]), Reader.I(r[20]), r[21] is null ? null : Reader.I(r[21]), r[22] is null ? null : Reader.I(r[22]),
                r[23] is null ? null : Convert.ToDouble(r[23]), r[24] is null ? null : Convert.ToDouble(r[24]), r[25] as string, r[26] as string, r[27] as string, r[28] as string,
                r[29] is null ? null : Reader.I(r[29]), r[30] as string))];

    public static IReadOnlyList<AttemptView> ForTask(string workspace, string taskId)
    {
        using var db = Reader.Open(workspace);
        return Attempts(db, "a.task_id=$k", taskId);
    }

    public static AttemptView Get(string workspace, string attemptId)
    {
        using var db = Reader.Open(workspace);
        return Attempt(db, attemptId);
    }

    // --- Addressing one agent or the team -----------------------------------------------------------------------
    /// <summary>What an instruction would do, per recipient, before it is sent.</summary>
    public IReadOnlyList<DispatchPreview> PreviewInstruction(string workspace, string taskId, IReadOnlyList<string> roles, string text)
    {
        using var db = Reader.Open(workspace);
        return [.. Recipients(db, taskId, roles).Select(r => Check(db, taskId, r, text, new(Instruction: text)).Preview)];
    }

    private static List<string> Recipients(SqliteConnection db, string taskId, IReadOnlyList<string> roles)
    {
        var task = TaskStore.Task(db, taskId);
        var assigned = Reader.Rows(db, "SELECT role FROM assignments WHERE project_id=$p", ("$p", task.ProjectId)).Select(r => Reader.S(r[0])).ToHashSet();
        var wanted = roles.Contains("everyone") ? Team.Roles.Where(assigned.Contains).ToList() : roles.Where(Team.Roles.Contains).Distinct().ToList();
        if (wanted.Count == 0) throw Bad("no_recipient", "Nobody would receive this: no role is assigned a model.", "Assign a model in Team first.");
        return wanted;
    }

    /// <summary>
    /// Records the instruction, then starts one attempt per recipient. Each attempt's command ID is the instruction's plus the role, so sending
    /// the same instruction again starts nothing new. The text goes only into the packets of the roles it was addressed to.
    /// </summary>
    public (CommandResult Instruction, IReadOnlyList<(string Role, CommandResult? Attempt, string? Error)> Attempts) SendInstruction(string workspace, string commandId, string taskId, IReadOnlyList<string> roles, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw Bad("invalid_text", "An instruction needs 1 to 4000 characters.", "Shorten it.");
        List<string> recipients;
        using (var db = Reader.Open(workspace)) recipients = Recipients(db, taskId, roles);
        var ins = TaskStore.Run(workspace, commandId, "instruction_sent", db =>
        {
            var t = TaskStore.Task(db, taskId);
            string id = newId();
            TaskStore.Exec(db, "INSERT INTO instructions VALUES($i,$p,$t,$r,$x,$at)", ("$i", id), ("$p", t.ProjectId), ("$t", taskId), ("$r", JsonSerializer.Serialize(recipients)), ("$x", text.Trim()), ("$at", Now));
            return new(id, t.Revision, taskId, null, null, $"to {string.Join(", ", recipients)}: {text.Trim()}");
        });
        var results = new List<(string, CommandResult?, string?)>();
        foreach (var role in recipients)
        {
            try { results.Add((role, Dispatch(workspace, commandId + ":" + role, taskId, role, new(Instruction: text.Trim(), InstructionId: ins.Id)), null)); }
            catch (StoreException e) { results.Add((role, null, e.Message)); }
        }
        return (ins, results);
    }

    // --- Connections: probe, smoke prompt -----------------------------------------------------------------------
    public async Task<IReadOnlyList<DiscoveredModel>> Probe(string workspace, string connectionId, CancellationToken ct)
    {
        (string Provider, string Endpoint, bool IsCloud, string? CredentialRef, string Label) c;
        using (var db = Reader.Open(workspace)) c = Team.Connection(db, connectionId);
        try
        {
            var models = await Client(c.Provider, c.Endpoint, c.CredentialRef).ListModelsAsync(ct);
            Team.RecordProbe(workspace, connectionId, models, null);
            return models;
        }
        catch (ModelException e) { Team.RecordProbe(workspace, connectionId, null, e.Message); throw new StoreException(e.Code, e.Message, "Check the endpoint, the key or the service, then probe again."); }
        catch (VaultUnavailableException e) { Team.RecordProbe(workspace, connectionId, null, e.Message); throw new StoreException("vault_unavailable", e.Message, "Unlock the credential store, then probe again."); }
    }

    /// <summary>A small prompt to a local model from Connections, streamed into an attempt that is not tied to any task. Stop with Cancel.</summary>
    public CommandResult SmokeTest(string workspace, string commandId, string connectionId, string model, string prompt)
    {
        var r = TaskStore.Run(workspace, commandId, "smoke_test", db =>
        {
            var c = Team.Connection(db, connectionId);
            if (c.IsCloud) throw Bad("local_only", "The test prompt only runs on models on this computer.", "Use a local connection, or dispatch to a cloud model from a task with consent.");
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 2000) throw Bad("invalid_text", "A test prompt needs 1 to 2000 characters.", "Shorten it.");
            string id = newId();
            TaskStore.Exec(db, """
                INSERT INTO attempts(id,command_id,run_id,project_id,task_id,role,connection_id,provider,requested_model,status,lease_owner,lease_expires,created_at,packet_text,currency,price_status)
                VALUES($i,$cmd,$i,'','','smoke',$c,$p,$m,'leased',$own,$exp,$at,$x,'USD','local')
                """, ("$i", id), ("$cmd", commandId), ("$c", connectionId), ("$p", c.Provider), ("$m", model), ("$own", Owner), ("$exp", Later(Lease)), ("$at", Now), ("$x", prompt.Trim()));
            return new(id, 0, "", null, null, "test prompt");
        });
        if (!r.Duplicate) StartSmoke(workspace, r.Id);
        return r;
    }

    private void StartSmoke(string workspace, string attemptId)
    {
        var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            string path = ProjectStore.DatabaseIn(workspace);
            AttemptView a; (string Provider, string Endpoint, bool IsCloud, string? CredentialRef, string Label) c;
            using (var db = Reader.Open(workspace)) { a = Attempt(db, attemptId); c = Team.Connection(db, a.ConnectionId); }
            var text = new StringBuilder();
            var flush = Stopwatch.StartNew();
            Write(path, "UPDATE attempts SET status='sending', sent_at=$at WHERE id=$i AND status='leased'", ("$at", Now), ("$i", attemptId));
            try
            {
                var result = await Client(c.Provider, c.Endpoint, null).ChatAsync(new(a.RequestedModel, [new("user", a.PacketText)], 200, false, null), piece =>
                {
                    text.Append(piece);
                    if (flush.ElapsedMilliseconds < 150) return;
                    flush.Restart();
                    Write(path, "UPDATE attempts SET status='streaming', partial_text=$x, lease_expires=$exp WHERE id=$i AND status IN ('sending','streaming')", ("$x", text.ToString()), ("$exp", Later(Lease)), ("$i", attemptId));
                }, cts.Token);
                Write(path, "UPDATE attempts SET status='completed', partial_text=$x, ended_at=$at, input_tokens=$in, output_tokens=$out, reported_model=$rm, cost=0, stop_reason='Finished.' WHERE id=$i AND status IN ('sending','streaming')",
                    ("$x", result.Text), ("$at", Now), ("$in", result.InputTokens), ("$out", result.OutputTokens), ("$rm", result.ReportedModel), ("$i", attemptId));
            }
            catch (OperationCanceledException) { }
            catch (ModelException e)
            {
                Write(path, "UPDATE attempts SET status=$s, stop_reason=$why, error_code=$c, partial_text=$x, ended_at=$at WHERE id=$i AND status IN ('leased','sending','streaming')",
                    ("$s", e.BeforeGeneration ? "failed" : "interrupted"), ("$why", e.Message), ("$c", e.Code), ("$x", e.Partial.Length > 0 ? e.Partial : text.ToString()), ("$at", Now), ("$i", attemptId));
            }
        });
        running[attemptId] = (cts, task);
        _ = task.ContinueWith(_ => { running.TryRemove(attemptId, out var _r); cts.Dispose(); }, TaskScheduler.Default);
    }
}
