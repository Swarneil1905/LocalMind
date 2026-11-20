using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>The offline review journey (fixture builder and reviewer, owner decision) and the single-writer rule.</summary>
public sealed partial class HubViewModel
{
    // --- One window writes ------------------------------------------------------------------------------
    // The worker holds the lock and refuses writes from any other process, so this view model only asks and reports.
    private string? ownedWorkspace;
    public string? ReadOnlyReason { get; private set; }
    public bool HasReadOnlyReason => ReadOnlyReason != null;
    /// <summary>True for an Agent Hub workspace this window owns the writes to.</summary>
    public bool CanWrite => IsWorkspace && ReadOnlyReason == null;

    /// <summary>Lets go of the workspace so another window can take it.</summary>
    public async Task ReleaseWriteLockAsync()
    {
        string? held = ownedWorkspace;
        ownedWorkspace = null;
        if (held != null && workspaces != null)
        {
            try { await workspaces.ReleaseAsync(held); } catch (WorkerException) { }
        }
    }

    private async Task UpdateWriteOwnershipAsync(string file, string? workspace)
    {
        if (workspace == null || workspaces == null)
        {
            await ReleaseWriteLockAsync();
            ReadOnlyReason = null;
        }
        else
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
            if (ownedWorkspace != null && Path.GetDirectoryName(Path.GetFullPath(ownedWorkspace)) != folder) await ReleaseWriteLockAsync();
            try
            {
                var o = await workspaces.AcquireAsync(file);
                ownedWorkspace = o.Held ? file : null;
                ReadOnlyReason = o.Held ? null
                    : $"This workspace is open for editing in another Agent Hub window (process {o.OwnerProcessId}), so this window is read-only. Close that window, then choose File → Refresh.";
            }
            catch (WorkerException e) { ReadOnlyReason = $"{e.Message} This window is read-only."; }
        }
        Notify(nameof(ReadOnlyReason)); Notify(nameof(HasReadOnlyReason)); Notify(nameof(CanWrite)); Notify(nameof(CanEdit)); Notify(nameof(ModeLabel));
    }

    // --- Review state ---------------------------------------------------------------------------------------
    private int reviewVersion;
    private (string Name, int Revision)? policy;
    private ReviewInfo? review;
    private string? reviewFor;
    /// <summary>The review of the selected task. It is empty from the moment the selection changes until that task's own review has been read.</summary>
    public ReviewInfo? Review => reviewFor != null && reviewFor == selectedTask?.Id ? review : null;

    public Func<Task>? PromptRequestChanges { get; set; }
    public Func<Task>? PromptSetPolicy { get; set; }
    /// <summary>Raised so the window can put keyboard focus on the Accept button, where the task and revision are on screen.</summary>
    public event Action? DecisionFocusRequested;

    public WorkspaceCommand AskBuilderCommand { get; private set; } = null!;
    public WorkspaceCommand AskReviewerCommand { get; private set; } = null!;
    /// <summary>The button: records the decision on the revision on screen.</summary>
    public WorkspaceCommand AcceptOutputCommand { get; private set; } = null!;
    /// <summary>The menu and its shortcut: bring the decision surface into view and focus it. It never commits.</summary>
    public WorkspaceCommand ReviewDecisionCommand { get; private set; } = null!;
    public WorkspaceCommand RequestChangesCommand { get; private set; } = null!;
    public WorkspaceCommand ToggleReviewCommand { get; private set; } = null!;
    public WorkspaceCommand SetPolicyCommand { get; private set; } = null!;

    public bool ReviewVisible => IsWorkspace && selectedTask != null;
    public bool CanDecide => CanEdit && selectedTask!.ReviewRequired && Review is { HasOutput: true, Decided: false };
    public string DecisionSubject => CanDecide ? $"Deciding on “{selectedTask!.Title}” · revision {Review!.Revision}" : "";
    public string ReviewToggleLabel => selectedTask?.ReviewRequired == true ? "Review required: on" : "Require review";
    public string PolicyLine => policy is not var (name, rev) ? ""
        : name == "human" ? $"Policy: human acceptance (revision {rev}). Agents can recommend; only you decide."
        : $"Policy: reviewer approval (revision {rev}). A reviewer’s approval counts after it recommends approval; you can always decide.";
    public bool HasOutput => Review is { HasOutput: true };
    public string OutputHeading => Review is { HasOutput: true } r ? $"Revision {r.Revision} · {r.Author}" + (r.RunId != null ? $" · run {r.RunId[..Math.Min(8, r.RunId.Length)]}" : "") : "";
    public string OutputText => Review?.Text ?? "";
    public bool HasFeedback => Review?.Feedback != null;
    public string FeedbackText => Review is { Feedback: { } f } r
        ? $"{r.FeedbackBy} {(r.FeedbackBy == "owner" ? "asked for changes" : "recommends " + r.Recommendation!.Replace('_', ' '))}: {f}" + (r.FeedbackBy == "owner" ? "" : " (a recommendation, not a decision)") : "";
    public string StateLine
    {
        get
        {
            if (selectedTask is not { } t) return "";
            if (!t.ReviewRequired) return "This task does not need review.";
            if (Review is not { HasOutput: true } r) return "No draft yet. Ask the builder for one.";
            if (r.AcceptedBy != null)
                return $"Accepted by {r.AcceptedBy} · revision {r.Revision}" + (t.Status != "done" && t.FirstOpen is { } wait ? $". Waiting on “{wait.Title}” before Done." : "");
            if (r.Decision == "changes_requested") return $"Changes requested on revision {r.Revision}. Ask the builder for the next revision.";
            return $"Awaiting your review · revision {r.Revision}" + (t.Status == "done" ? " (the task is marked Done, but this revision is not accepted)" : "");
        }
    }

    private void InitReview()
    {
        AskBuilderCommand = new(_ => CanEdit, _ => Role("builder"));
        AskReviewerCommand = new(_ => CanEdit && Review is { HasOutput: true }, _ => Role("reviewer"));
        AcceptOutputCommand = new(_ => CanDecide, _ => Decide("accepted", null));
        ReviewDecisionCommand = new(_ => CanDecide, _ => { DecisionFocusRequested?.Invoke(); return Task.CompletedTask; });
        RequestChangesCommand = new(_ => CanDecide && PromptRequestChanges != null, _ => PromptRequestChanges!());
        ToggleReviewCommand = new(_ => CanEdit, _ =>
        {
            // Everything the request needs is read now, before anything is awaited; a retry sends exactly this.
            string ws = path!;
            var t = selectedTask!;
            return Fail(() => Submit($"review|{ws}|{t.Id}|{t.Revision}", ws,
                id => workspaces!.UpdateTaskAsync(ws, id, t.Id, t.Revision, reviewRequired: !t.ReviewRequired)));
        });
        SetPolicyCommand = new(_ => CanWrite && policy != null && PromptSetPolicy != null, _ => PromptSetPolicy!());
        commands = [.. commands, AskBuilderCommand, AskReviewerCommand, AcceptOutputCommand, ReviewDecisionCommand, RequestChangesCommand, ToggleReviewCommand, SetPolicyCommand];
    }

    /// <summary>Runs one write. A refusal offers a reload; an answer that was lost offers Try again, which resubmits the same frozen request.</summary>
    private async Task Fail(Func<Task<string?>> work)
    {
        if (await work() is not { } message) return;
        if (pending is { Uncertain: true }) ShowNotice(message, "Try again", new(_ => true, _ => Fail(work)));
        else ShowNotice(message, "Reload board", RefreshCommand);
    }

    private Task Role(string role)
    {
        string ws = path!;
        var t = selectedTask!;
        int? revision = Review?.Revision;
        return Fail(() => Submit($"role|{role}|{ws}|{t.Id}|{revision}", ws, id => workspaces!.RunRoleAsync(ws, id, t.Id, role)));
    }

    private Task Decide(string decision, string? comment)
    {
        var request = FreezeDecision(decision, comment);
        return request == null ? Task.CompletedTask : Fail(request);
    }

    /// <summary>
    /// Captures the workspace, task, output, content and policy revisions, decision and comment as they are on screen now.
    /// The returned function sends exactly that, whatever is selected or open later.
    /// </summary>
    private Func<Task<string?>>? FreezeDecision(string decision, string? comment)
    {
        if (selectedTask is not { } t || Review is not { OutputId: { } output } r || path is not { } ws) return null;
        int content = r.Revision, policyRevision = r.PolicyRevision;
        return () => Submit($"decide|{ws}|{t.Id}|{output}|{content}|{policyRevision}|{decision}|{comment}", ws,
            id => workspaces!.DecideOutputAsync(ws, id, t.Id, output, content, policyRevision, decision, comment));
    }

    /// <summary>Called when the Request changes dialog opens: the dialog's submit is bound to the task and revision shown in it, and the comment is added at Save.</summary>
    public Func<string, Task<string?>> FreezeRequestChanges()
    {
        string ws = path!;
        var t = selectedTask!;
        var r = Review!;
        return comment => Submit($"decide|{ws}|{t.Id}|{r.OutputId}|{r.Revision}|{r.PolicyRevision}|changes_requested|{comment}", ws,
            id => workspaces!.DecideOutputAsync(ws, id, t.Id, r.OutputId!, r.Revision, r.PolicyRevision, "changes_requested", comment));
    }

    public string RequestChangesLabel => selectedTask != null && Review is { HasOutput: true } r ? $"What should change in revision {r.Revision} of “{selectedTask.Title}”?" : "What should change?";

    public Func<string, Task<string?>> FreezePolicyChange()
    {
        string ws = path!;
        int revision = policy!.Value.Revision;
        return name => Submit($"policy|{ws}|{revision}|{name}", ws, id => workspaces!.SetPolicyAsync(ws, id, name, revision));
    }

    private async Task LoadReviewAsync()
    {
        int v = ++reviewVersion;
        var task = selectedTask;
        string? file = path;
        (string, int)? pol = null;
        ReviewInfo? info = null;
        if (IsWorkspace && file != null)
        {
            try
            {
                (pol, info) = await Task.Run(() =>
                {
                    var reader = new ProjectReader(file);
                    return (reader.Policy(), task == null ? null : reader.Review(task.Id));
                });
            }
            catch (Exception ex) when (IsDataError(ex)) { }
        }
        if (v != reviewVersion) return; // a newer selection or project superseded this read
        (policy, review, reviewFor) = (pol, info, task?.Id);
        NotifyReview();
    }

    private void NotifyReview()
    {
        foreach (var n in new[] { nameof(Review), nameof(ReviewVisible), nameof(CanDecide), nameof(DecisionSubject), nameof(ReviewToggleLabel), nameof(PolicyLine), nameof(HasOutput),
                                  nameof(OutputHeading), nameof(OutputText), nameof(HasFeedback), nameof(FeedbackText), nameof(StateLine), nameof(RequestChangesLabel) })
            Notify(n);
        RaiseCommands();
    }
}
