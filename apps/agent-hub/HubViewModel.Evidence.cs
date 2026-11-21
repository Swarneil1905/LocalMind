using System.Collections.ObjectModel;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>One waiting decision as the inbox lists it.</summary>
public sealed record InboxRow(InboxItem Item)
{
    public string Heading => $"{Item.TaskTitle} · revision {Item.Revision}";
    public string Where => $"{Item.ProjectName} · {Item.TaskStatus.Replace('_', ' ')} · from {Item.Author}";
    public string Unlocks => string.Join(" ", Item.Effects);
    public string AutomationName => $"{Item.ProjectName}: {Item.TaskTitle}, revision {Item.Revision}, waiting for your decision";
    public override string ToString() => AutomationName; // what assistive technology reads for the list item
}

/// <summary>One persisted event as the team channel shows it.</summary>
public sealed record ChannelRow(ChannelMessage Message)
{
    public string Who => Message.Class == "claim" ? $"{Message.Actor} · claim, not an acceptance" : Message.ActorKind switch { "agent" => $"{Message.Actor} · agent", "human" => Message.Actor, _ => "Activity · actor not recorded" };
    /// <summary>A persisted human decision, on its own: outcome, exact revision and who decided.</summary>
    public string Badge => Message is { Class: "decision", Revision: { } rev } m ? $"{(m.Outcome == "accepted" ? "Accepted" : "Changes requested")} · revision {rev} · {m.Actor}" : "";
    public bool HasBadge => Badge.Length > 0;
    public bool IsClaim => Message.Class == "claim";
    public string When => DateTime.TryParse(Message.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime().ToString("g") : "";
    public string Text => Message.Text;
    public string Task => Message.TaskTitle;
    public string AutomationName => $"{Who}, {Message.Text}";
    public override string ToString() => AutomationName; // what assistive technology reads for the list item
}

/// <summary>The inbox, the team channel and the handoff preview, all read from the workspace.</summary>
public sealed partial class HubViewModel
{
    private string center = "board";
    public bool BoardVisible => ListVisible && center == "board";
    public bool InboxVisible => ListVisible && center == "inbox";
    public bool ChannelVisible => ListVisible && center == "channel";

    public WorkspaceCommand ShowBoardCommand { get; private set; } = null!;
    public WorkspaceCommand ShowInboxCommand { get; private set; } = null!;
    public WorkspaceCommand ShowChannelCommand { get; private set; } = null!;
    public WorkspaceCommand OpenInboxItemCommand { get; private set; } = null!;
    public WorkspaceCommand BackToInboxCommand { get; private set; } = null!;
    public WorkspaceCommand OpenChannelTaskCommand { get; private set; } = null!;
    public WorkspaceCommand PreviewHandoffCommand { get; private set; } = null!;
    public WorkspaceCommand CloseHandoffCommand { get; private set; } = null!;

    private void SetCenter(string mode)
    {
        center = mode;
        foreach (var n in new[] { nameof(BoardVisible), nameof(InboxVisible), nameof(ChannelVisible), nameof(QuestionsVisible), nameof(ConnectionsVisible), nameof(TeamVisible), nameof(PlanVisible) }) Notify(n);
        SaveLayout();
        RaiseCommands();
    }

    private void InitEvidence()
    {
        ShowBoardCommand = new(_ => true, _ => { cameFromInbox = false; SetCenter("board"); return Task.CompletedTask; });
        ShowInboxCommand = new(_ => IsWorkspace, _ => { SetCenter("inbox"); return Task.CompletedTask; });
        ShowChannelCommand = new(_ => IsWorkspace, _ => { SetCenter("channel"); return Task.CompletedTask; });
        OpenInboxItemCommand = new(p => p is InboxRow, p => OpenInboxItem((InboxRow)p!));
        BackToInboxCommand = new(_ => cameFromInbox, _ => { cameFromInbox = false; SetCenter("inbox"); Notify(nameof(CameFromInbox)); return Task.CompletedTask; });
        OpenChannelTaskCommand = new(p => p is ChannelRow, p => OpenChannelTask((ChannelRow)p!));
        PreviewHandoffCommand = new(p => IsWorkspace && selectedTask != null && selected != null && p is "builder" or "reviewer", p => PreviewHandoff((string)p!));
        CloseHandoffCommand = new(_ => HasHandoff, _ => { handoff = null; NotifyHandoff(); return Task.CompletedTask; });
        InitConversation();
        commands = [.. commands, ShowBoardCommand, ShowInboxCommand, ShowChannelCommand, OpenInboxItemCommand, BackToInboxCommand, OpenChannelTaskCommand, PreviewHandoffCommand, CloseHandoffCommand];
    }

    // --- Inbox ------------------------------------------------------------------------------------------------
    private IReadOnlyList<InboxRow> inbox = [];
    private bool cameFromInbox;
    public IReadOnlyList<InboxRow> InboxRows => inbox;
    public int InboxCount => inbox.Count;
    public string InboxLabel => $"Inbox ({inbox.Count})";
    public bool InboxEmpty => inbox.Count == 0;
    public bool CameFromInbox => cameFromInbox;

    private async Task LoadInboxAsync(int request)
    {
        IReadOnlyList<InboxRow> rows = [];
        if (IsWorkspace && path is string file)
        {
            try { rows = await Task.Run(() => (IReadOnlyList<InboxRow>)[.. Decisions.Inbox(file).Select(i => new InboxRow(i))]); }
            catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { }
        }
        if (request != version && request != 0) return;
        inbox = rows;
        foreach (var n in new[] { nameof(InboxRows), nameof(InboxCount), nameof(InboxLabel), nameof(InboxEmpty) }) Notify(n);
    }

    private async Task OpenInboxItem(InboxRow row)
    {
        cameFromInbox = true;
        Notify(nameof(CameFromInbox));
        SetCenter("board");
        // The row names a revision. Open that one, even if a newer one has arrived since or the inspector was open on another revision of this task.
        openAt = (row.Item.TaskId, row.Item.Revision);
        await GoToTaskAsync(row.Item.ProjectId, row.Item.TaskId);
        await LoadReviewAsync();
    }

    /// <summary>Selects a task, switching project first when it lives in another one.</summary>
    private async Task GoToTaskAsync(string projectId, string taskId)
    {
        showInspector = true; Layout();
        if (selected?.Id == projectId && Tasks.FirstOrDefault(t => t.Id == taskId) is { } here) { Select(here); return; }
        if (Projects.FirstOrDefault(p => p.Id == projectId) is not { } project) return;
        nextSelectId = taskId;
        SelectedProject = project;
        for (int i = 0; i < 100 && selectedTask?.Id != taskId; i++) await Task.Delay(20);
    }

    // --- Team channel -----------------------------------------------------------------------------------------
    private readonly ObservableCollection<ChannelRow> channelRows = [];
    private readonly HashSet<long> channelSeen = [];
    private long channelCursor;
    private (string File, string Project)? channelScope;
    private int channelVersion;
    public ObservableCollection<ChannelRow> ChannelRows => channelRows;
    public bool ChannelEmpty => channelRows.Count == 0;

    /// <summary>Reads what is new after the cursor, a bounded page at a time. A project switch starts over; a reconnect continues where it stopped.</summary>
    private async Task LoadChannelAsync()
    {
        int v = ++channelVersion;
        if (!IsWorkspace || path is not string file || selected is not { } project)
        {
            channelScope = null; channelCursor = 0; channelSeen.Clear(); ResetChannelRows(); Notify(nameof(ChannelEmpty));
            return;
        }
        if (channelScope != (file, project.Id))
        {
            channelScope = (file, project.Id); channelCursor = 0; channelSeen.Clear(); ResetChannelRows();
        }
        try
        {
            while (true)
            {
                long after = channelCursor;
                var page = await Task.Run(() => Channel.Read(file, project.Id, after, 50));
                if (v != channelVersion) return; // a newer read owns the cursor now
                foreach (var m in page.Messages)
                    if (channelSeen.Add(m.Seq)) Ingest(new(m));
                AfterIngest();
                channelCursor = page.Cursor;
                if (!page.More) break;
            }
        }
        catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { }
        Notify(nameof(ChannelEmpty));
    }

    private async Task OpenChannelTask(ChannelRow row)
    {
        SetCenter("board");
        // A decision message leads to the revision it was about, not to whatever is newest now.
        if (row.Message is { Class: "decision", Revision: { } rev }) openAt = (row.Message.TaskId, rev);
        await GoToTaskAsync(row.Message.ProjectId, row.Message.TaskId);
        if (openAt != null) await LoadReviewAsync();
    }

    // --- Handoff preview --------------------------------------------------------------------------------------
    private Packet? handoff;
    private int handoffVersion;
    public bool HasHandoff => handoff != null;

    /// <summary>Closes the preview and discards any packet still being built. Called whenever the workspace, project or task on screen changes.</summary>
    private void InvalidateHandoff()
    {
        handoffVersion++;
        if (handoff == null) return;
        handoff = null;
        NotifyHandoff();
    }
    public string HandoffSummary => handoff is not { } p ? "" :
        $"Packet for the {p.Role} · project {p.ProjectName} · about {p.TokensUsed} of {p.TokenLimit} tokens ({Packet.Estimator})" + (p.Omitted.Count > 0 ? $" · {p.Omitted.Count} item{(p.Omitted.Count == 1 ? "" : "s")} left out" : "");
    public string HandoffText => handoff?.Text ?? "";
    public string HandoffOmitted => handoff is { Omitted.Count: > 0 } p ? "Left out of the packet: " + string.Join(" ", p.Omitted) : "";
    public bool HasHandoffOmitted => HandoffOmitted.Length > 0;

    private async Task PreviewHandoff(string role)
    {
        if (path is not string file || selected is not { } project || selectedTask is not { } task) return;
        int version = ++handoffVersion;
        Packet? built = null;
        bool failed = false;
        try { built = await Task.Run(() => Handoff.Build(file, project.Id, task.Id, role)); }
        catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { failed = true; }
        // The user may have moved on while this was being built: a packet for another selection is never shown.
        if (version != handoffVersion || path != file || selected?.Id != project.Id || selectedTask?.Id != task.Id) return;
        handoff = built;
        if (failed) ShowNotice("The handoff packet could not be built from this workspace. Refresh, then try again.", "Dismiss", DismissNoticeCommand);
        NotifyHandoff();
    }

    private void NotifyHandoff()
    {
        foreach (var n in new[] { nameof(HasHandoff), nameof(HandoffSummary), nameof(HandoffText), nameof(HandoffOmitted), nameof(HasHandoffOmitted) }) Notify(n);
        RaiseCommands();
    }

    // --- Keeping an idle window current -------------------------------------------------------------------------
    private CancellationTokenSource? polling;
    private long lastEventSeq = -1;

    /// <summary>Starts looking for events written by another window. Call from the UI thread so updates arrive there.</summary>
    public void StartPolling(TimeSpan every)
    {
        StopPolling();
        var cts = polling = new CancellationTokenSource();
        _ = Loop(cts.Token);

        async Task Loop(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(every);
            try
            {
                while (await timer.WaitForNextTickAsync(ct)) await PollAsync();
            }
            catch (OperationCanceledException) { }
        }
    }

    public void StopPolling()
    {
        polling?.Cancel();
        polling = null;
    }

    /// <summary>One look at the workspace: when events past the last one seen exist, read the channel from its cursor and the inbox again.</summary>
    public async Task PollAsync()
    {
        if (!IsWorkspace || path is not string file) { lastEventSeq = -1; return; }
        long latest;
        try { latest = await Task.Run(() => Channel.LatestSeq(file)); }
        catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { return; }
        if (path != file) return;
        if (latest == lastEventSeq)
        {
            // Nothing new was recorded, but a running model writes progress to its attempt: keep that moving on screen.
            if (AnyRunning) await RefreshRunsAsync();
            return;
        }
        lastEventSeq = latest;
        await RefreshRunsAsync();
        await LoadChannelAsync();
        await LoadInboxAsync(0);
        await LoadQuestionsAsync();
        await RefreshPlanAsync(true);
    }
}

