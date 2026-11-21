using System.Collections.Concurrent;

namespace LocalMind.AgentHub.Core;

public sealed record WakeReport(int Refreshed, IReadOnlyList<string> NeedsDecision);
public sealed record ShutdownReport(int Stopped, int Finished, int StillRunning);

public sealed partial class RunEngine
{
    private readonly ConcurrentDictionary<string, System.Text.StringBuilder> partials = new();
    private volatile bool sleeping;
    public bool Sleeping => sleeping;

    internal void Track(string attemptId, System.Text.StringBuilder text) => partials[attemptId] = text;
    public int ActiveCount => running.Count;

    // --- AH67 · an uncertain action: confirm, abandon or retry, once -----------------------------------------------------
    private static bool Uncertain(AttemptView a) =>
        a.SentAt != null && (a.Status is "needs_reconciliation" or "interrupted" || (a.Status == "completed" && a.Cost == null && a.PriceStatus != "local"));

    /// <summary>
    /// The owner's decision about an attempt that may or may not have done its work or been charged. Confirm records the receipt, abandon records that nothing was confirmed,
    /// retry starts a new attempt and costs again. The choice is stored once; asking again with the same choice changes nothing, and a different one is refused.
    /// </summary>
    public CommandResult ResolveAttempt(string workspace, string commandId, string attemptId, string choice, double? amount)
    {
        if (choice is not ("confirm" or "abandon" or "retry")) throw Bad("invalid_choice", $"“{choice}” is not a choice.", "Confirm, abandon or retry.");
        string retried = "";
        // The same command again (a lost acknowledgment, a restart) returns what it did the first time and starts nothing.
        if (TaskStore.CommandStatus(workspace, commandId) is { } prior0) return prior0;
        if (choice == "retry")
        {
            AttemptView a;
            using (var db = Reader.Open(workspace)) a = Attempt(db, attemptId);
            if (!Uncertain(a) || a.Status == "completed") throw Bad("not_retryable", "Only an attempt that did not finish can be retried.", "Use Start again from the run inspector.");
            {
                var again = Dispatch(workspace, "resolve-retry-" + commandId, a.TaskId, a.Role, new(AcceptPriceRisk: true, AcceptRepeatCost: true, RetryOf: attemptId, QueueIfFull: false));
                retried = again.Id;
            }
        }
        return TaskStore.Run(workspace, commandId, "attempt_resolved", db =>
        {
            var a = Attempt(db, attemptId);
            var prior = Reader.Rows(db, "SELECT choice, amount FROM reconciliations WHERE attempt_id=$a", ("$a", attemptId)).FirstOrDefault();
            // After a retry the old attempt's charge is still unknown; its receipt, when it arrives, settles it once.
            bool lateReceipt = prior != null && Reader.S(prior[0]) == "retry" && choice == "confirm" && prior[1] is null && a.Cost == null;
            if (prior != null && !lateReceipt)
            {
                if (Reader.S(prior[0]) != choice) throw Bad("already_resolved", $"You already chose to {Reader.S(prior[0])} this attempt.", "That choice is recorded once.");
                return new(attemptId, 0, a.TaskId, null, null, "already recorded");
            }
            if (!lateReceipt && !Uncertain(a)) throw Bad("not_uncertain", $"That attempt is {a.Status.Replace('_', ' ')}; there is nothing uncertain to resolve.", "Reload the run inspector.");
            string now = DateTime.UtcNow.ToString("O"), detail;
            if (choice == "confirm")
            {
                double? known = a.Cost ?? amount ?? (a.PriceStatus == "local" ? 0 : null);
                if (known is not { } cost) throw Bad("receipt_needed", "Enter the amount on the provider's receipt (0 if it charged nothing).", "Check the provider's usage page.");
                if (cost < 0 || double.IsNaN(cost)) throw Bad("invalid_amount", "A charge is zero or more.", "Enter the amount from the receipt.");
                if (TaskStore.Exec2(db, "UPDATE budget_ledger SET amount=$m WHERE attempt_id=$a AND kind='settle' AND amount IS NULL", ("$m", cost), ("$a", attemptId)) == 0
                    && TaskStore.Scalar<long>(db, "SELECT count(*) FROM budget_ledger WHERE attempt_id=$a AND kind IN ('settle','release')", ("$a", attemptId)) == 0)
                    TaskStore.Exec(db, "INSERT INTO budget_ledger VALUES($i,$p,$a,'settle',$m,$c,$at)", ("$i", newId()), ("$p", a.ProjectId), ("$a", attemptId), ("$m", cost), ("$c", a.Currency ?? "USD"), ("$at", now));
                TaskStore.Exec(db, "UPDATE attempts SET cost=$m, status=CASE WHEN status='completed' THEN status ELSE 'confirmed' END, stop_reason=$why WHERE id=$a", ("$m", cost), ("$a", attemptId), ("$why", $"Confirmed by the owner with a receipt of {cost:0.0000} {a.Currency}."));
                detail = $"confirmed with a receipt of {cost:0.0000} {a.Currency}";
            }
            else if (choice == "abandon")
            {
                if (a.Status == "completed") throw Bad("not_abandonable", "That attempt finished and produced output. Record its charge instead.", "Use Record the charge.");
                if (TaskStore.Exec2(db, "UPDATE budget_ledger SET amount=0 WHERE attempt_id=$a AND kind='settle' AND amount IS NULL", ("$a", attemptId)) == 0
                    && TaskStore.Scalar<long>(db, "SELECT count(*) FROM budget_ledger WHERE attempt_id=$a AND kind IN ('settle','release')", ("$a", attemptId)) == 0)
                    TaskStore.Exec(db, "INSERT INTO budget_ledger VALUES($i,$p,$a,'release',0,$c,$at)", ("$i", newId()), ("$p", a.ProjectId), ("$a", attemptId), ("$c", a.Currency ?? "USD"), ("$at", now));
                TaskStore.Exec(db, "UPDATE attempts SET status='abandoned', stop_reason='Abandoned by the owner: no charge and no effect were confirmed.' WHERE id=$a", ("$a", attemptId));
                detail = "abandoned";
            }
            else
            {
                // The old charge stays unknown (and reserved) until a receipt is entered; the new attempt is a separate, explicit spend.
                TaskStore.Exec(db, "UPDATE attempts SET status='abandoned', stop_reason=$why WHERE id=$a", ("$a", attemptId), ("$why", "Retried as a new attempt at the owner's choice. This attempt's charge is still unknown."));
                detail = "retried" + (retried.Length > 0 ? " as attempt " + retried[..Math.Min(8, retried.Length)] : "");
            }
            if (lateReceipt) TaskStore.Exec(db, "UPDATE reconciliations SET amount=$m, note=COALESCE(note,'') || ' receipt recorded later' WHERE attempt_id=$a", ("$m", amount), ("$a", attemptId));
            else TaskStore.Exec(db, "INSERT INTO reconciliations VALUES($a,$c,$m,$n,$at)", ("$a", attemptId), ("$c", choice), ("$m", amount), ("$n", choice == "retry" && retried.Length > 0 ? "replacement attempt " + retried : null), ("$at", now));
            return new(attemptId, 0, a.TaskId, null, null, lateReceipt ? $"original charge settled at {amount:0.0000} {a.Currency}" : detail);
        });
    }

    // --- AH103 · sleep and wake -----------------------------------------------------------------------------------------
    /// <summary>The lifecycle signal. A real suspend arrives here from the operating system; checks send it directly. Partial output of every running attempt is saved.</summary>
    public int OnSleep(string workspace)
    {
        sleeping = true;
        string path = ProjectStore.DatabaseIn(workspace);
        int n = 0;
        foreach (var (id, text) in partials)
            if (running.ContainsKey(id)) { Write(path, "UPDATE attempts SET partial_text=$x WHERE id=$i AND status IN ('sending','streaming')", ("$x", text.ToString()), ("$i", id)); n++; }
        return n;
    }

    /// <summary>
    /// After wake: attempts this engine is still running get a fresh lease; an attempt whose lease ran out while nothing was running it is settled by Recover (sent: kept for the
    /// owner's decision with its partial text; unsent: safe to start again). Nothing is sent again on its own.
    /// </summary>
    public WakeReport OnWake(string workspace)
    {
        sleeping = false;
        string path = ProjectStore.DatabaseIn(workspace);
        int refreshed = 0;
        foreach (var id in running.Keys)
        {
            Write(path, "UPDATE attempts SET lease_expires=$e WHERE id=$i AND status IN ('leased','sending','streaming')", ("$e", Later(Lease)), ("$i", id));
            refreshed++;
        }
        Recover(workspace);
        using var db = Reader.Open(workspace);
        var open = Reader.Rows(db, "SELECT id FROM attempts WHERE status IN ('needs_reconciliation','interrupted') AND id NOT IN (SELECT attempt_id FROM reconciliations)").Select(r => Reader.S(r[0])).ToList();
        return new(refreshed, open);
    }

    // --- AH104 · closing during a run ------------------------------------------------------------------------------------
    /// <summary>
    /// "stop" cancels every running attempt and waits briefly for them to end; "finish" lets them run and waits up to <paramref name="maxMs"/>. Either way the wait is bounded;
    /// whatever is still running keeps a lease that will expire, so the next launch settles it.
    /// </summary>
    public ShutdownReport Shutdown(string workspace, string policy, int maxMs)
    {
        var ids = running.Keys.ToList();
        int stopped = 0;
        if (policy == "stop")
            foreach (var id in ids)
                try { Cancel(workspace, "shutdown-" + id, id); stopped++; } catch (StoreException) { }
        var tasks = ids.Select(id => running.TryGetValue(id, out var r) ? r.Task : Task.CompletedTask).ToArray();
        Task.WaitAll(tasks.Select(t => t.ContinueWith(_ => { })).ToArray(), Math.Clamp(maxMs, 0, 600_000));
        int still = ids.Count(id => running.ContainsKey(id));
        return new(stopped, policy == "finish" ? ids.Count - still : 0, still);
    }
}
