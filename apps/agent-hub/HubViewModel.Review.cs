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

    // The revision the user has open stays open when a newer one arrives, until they choose to move to it.
    private bool releasePlace;
    // An inbox row opens the revision it names, once.
    private (string Task, int Revision)? openAt;
    private sealed record Evidence(OutputView? View, OutputDiff? Diff, IReadOnlyList<string> Effects, IReadOnlyList<(string Name, string Status, string? Detail)> Checks, RunPacket? Packet);
    private string packetLine = "";
    public string PacketLine => packetLine;
    public bool HasPacketLine => packetLine.Length > 0;
    private Evidence? evidence;
    public WorkspaceCommand OpenLatestRevisionCommand { get; private set; } = null!;
    public bool IsStale => Review is { IsStale: true };
    public string StaleNotice => Review is { IsStale: true } r ? $"Revision {r.LatestRevision} has arrived. Revision {r.Revision} can no longer be accepted." : "";

    public bool HasChange => Review is { HasOutput: true } && evidence != null;
    public string ChangeHeading => Review is { HasOutput: true, PreviousOutputId: not null } r ? $"What changed since revision {r.Revision - 1}" : "What changed";
    public string ChangeSummary => Review is not { HasOutput: true } ? "" : Review.PreviousOutputId == null ? "This is the first revision, so there is nothing to compare it with." : evidence?.Diff?.Summary ?? "";
    public IReadOnlyList<string> ChangeLines => evidence?.Diff is { } d ? [.. d.Lines.Select(l => $"{l.Op} {l.Text}")] : [];
    public string ChecksText => evidence == null ? "" : evidence.Checks.Count == 0
        ? "No checks were run for this revision. Neither the fixtures nor the models run any here, so nothing on this screen says it works."
        : "Checks: " + string.Join("; ", evidence.Checks.Select(c => $"{c.Name} {c.Status.Replace('_', ' ')}" + (c.Detail != null ? $" ({c.Detail})" : "")));
    public string EffectText => evidence is { Effects.Count: > 0 } e && Review is { HasOutput: true, Decided: false } r ? $"If you accept revision {r.Revision}: " + string.Join(" ", e.Effects) : "";
    public string OutputNote => evidence?.View is { } v && (!v.Verified || v.Kind != "text") ? v.Note : "";
    public bool HasOutputNote => OutputNote.Length > 0;
    public bool ShowOutputText => HasOutput && OutputText.Length > 0;
    private Avalonia.Media.Imaging.Bitmap? outputImage;
    public Avalonia.Media.Imaging.Bitmap? OutputImage => outputImage;
    public bool HasOutputImage => outputImage != null;

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
    public bool CanDecide => CanEdit && selectedTask!.ReviewRequired && Review is { HasOutput: true, Decided: false, IsStale: false };
    public string DecisionSubject => CanDecide ? $"Deciding on “{selectedTask!.Title}” · revision {Review!.Revision}" : "";
    public string ReviewToggleLabel => selectedTask?.ReviewRequired == true ? "Review required: on" : "Require review";
    public string PolicyLine => policy is not var (name, rev) ? ""
        : name == "human" ? $"Policy: human acceptance (revision {rev}). Agents can recommend; only you decide."
        : $"Policy: reviewer approval (revision {rev}). A reviewer’s approval counts after it recommends approval; you can always decide.";
    public bool HasOutput => Review is { HasOutput: true };
    public string OutputHeading => Review is { HasOutput: true } r ? $"Revision {r.Revision} · {r.Author}" + (r.RunId != null ? $" · run {r.RunId[..Math.Min(8, r.RunId.Length)]}" : "") : "";
    public string OutputText => evidence?.View is { } v ? (v.Verified || v.Integrity == "malformed_data" ? v.Text ?? "" : "") : Review?.Text ?? "";
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
            if (r.IsStale) return $"Revision {r.Revision} is no longer current. Revision {r.LatestRevision} is waiting for review.";
            if (r.AcceptedBy != null)
                return $"Accepted by {r.AcceptedBy} · revision {r.Revision}" + (t.Status != "done" && t.FirstOpen is { } wait ? $". Waiting on “{wait.Title}” before Done." : "");
            if (r.Decision == "changes_requested") return $"Changes requested on revision {r.Revision}. Ask the builder for the next revision.";
            return $"Awaiting your review · revision {r.Revision}" + (t.Status == "done" ? " (the task is marked Done, but this revision is not accepted)" : "");
        }
    }

    private void InitReview()
    {
        OpenLatestRevisionCommand = new(_ => IsStale, _ => { pinnedTask = null; releasePlace = true; return LoadReviewAsync(); });
        AskBuilderCommand = new(_ => CanEdit, _ => { releasePlace = true; return Role("builder"); });
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
        commands = [.. commands, OpenLatestRevisionCommand, AskBuilderCommand, AskReviewerCommand, AcceptOutputCommand, ReviewDecisionCommand, RequestChangesCommand, ToggleReviewCommand, SetPolicyCommand];
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
        string? key = workspaceKey; // the workspace this dialog belongs to, whatever is open when it is sent
        var t = selectedTask!;
        var r = Review!;
        return async comment =>
        {
            string? error = await Submit($"decide|{ws}|{t.Id}|{r.OutputId}|{r.Revision}|{r.PolicyRevision}|changes_requested|{comment}", ws,
                id => workspaces!.DecideOutputAsync(ws, id, t.Id, r.OutputId!, r.Revision, r.PolicyRevision, "changes_requested", comment));
            // Sent: the draft has done its job. Not sent, or not known: it stays.
            if (error == null) EditDrafts(key, f => ClearDraft(f, t.Id, r.OutputId!));
            return error;
        };
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
        // Same task as the one already shown: keep the revision the user is looking at. A new task, or a deliberate move, shows the latest.
        int? keep = task != null && openAt is { } want && want.Task == task.Id ? want.Revision
            : task != null && reviewFor == task.Id && (!releasePlace || Pinned) && review is { HasOutput: true } held ? held.Revision : null; // nothing to hold on to until a revision exists
        releasePlace = false;
        (string, int)? pol = null;
        ReviewInfo? info = null;
        Evidence? found = null;
        if (IsWorkspace && file != null)
        {
            try
            {
                (pol, info, found) = await Task.Run(() =>
                {
                    var reader = new ProjectReader(file);
                    var p = reader.Policy();
                    var r = task == null ? null : reader.Review(task.Id, keep);
                    Evidence? e = null;
                    if (r is { HasOutput: true } && task != null)
                    {
                        try
                        {
                            e = new(Outputs.Inspect(file, r.OutputId!), r.PreviousOutputId == null ? null : Outputs.Compare(file, r.PreviousOutputId, r.OutputId!),
                                    Decisions.Effects(file, task.Id), reader.Checks(r.OutputId!), r.RunId == null ? null : Runs.Packet(file, r.RunId));
                        }
                        catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { }
                    }
                    return (p, r, e);
                });
            }
            catch (Exception ex) when (IsDataError(ex)) { }
        }
        if (v != reviewVersion) return; // a newer selection or project superseded this read
        (policy, review, reviewFor, evidence) = (pol, info, task?.Id, found);
        if (openAt is { } landed && landed.Task == task?.Id) openAt = null;
        packetLine = found?.Packet is { Recorded: true } rp
            ? $"Written from a packet for project {Projects.FirstOrDefault(p => p.Id == rp.ProjectId)?.Name ?? rp.ProjectId}: about {rp.Tokens} of {rp.Limit} tokens"
              + (rp.Sources.Count > 0 ? "; it cited " + string.Join(", ", rp.Sources.Select(x => $"{x.Id[..Math.Min(8, x.Id.Length)]} (revision {x.Revision})")) : "; no upstream outputs or feedback") + "."
            : "";
        outputImage = null;
        RefreshDraft();
        ApplyReanchor();
        if (found?.View is { Verified: true, Image: { } bytes })
        {
            try { outputImage = new(new MemoryStream(bytes)); }
            catch (Exception) { evidence = found with { View = found.View with { Integrity = "undecodable", Note = "The image header is valid but the file could not be decoded, so it was not shown.", Image = null } }; }
        }
        NotifyReview();
    }

    private void NotifyReview()
    {
        RefreshDraft();
        NotifyDraft();
        foreach (var n in new[] { nameof(Review), nameof(ReviewVisible), nameof(CanDecide), nameof(DecisionSubject), nameof(ReviewToggleLabel), nameof(PolicyLine), nameof(HasOutput),
                                  nameof(OutputHeading), nameof(OutputText), nameof(HasFeedback), nameof(FeedbackText), nameof(StateLine), nameof(RequestChangesLabel),
                                  nameof(IsStale), nameof(StaleNotice), nameof(HasChange), nameof(ChangeHeading), nameof(ChangeSummary), nameof(ChangeLines), nameof(ChecksText),
                                  nameof(EffectText), nameof(OutputNote), nameof(HasOutputNote), nameof(PacketLine), nameof(HasPacketLine), nameof(ShowOutputText), nameof(OutputImage), nameof(HasOutputImage), nameof(Pinned), nameof(PinNote), nameof(EvidenceNote), nameof(HasEvidenceNote), nameof(ComparisonLines) })
            Notify(n);
        RaiseCommands();
    }
}
