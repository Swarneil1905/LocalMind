using System.Collections.ObjectModel;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>Day 5: plan, graph, saved layout and views, command menu, steady board, pinned revision.</summary>
public sealed partial class HubViewModel
{
    // --- Layout per workspace (AH72, AH73) ------------------------------------------------------------------
    public LayoutStore? Layouts { get; set; }
    private SavedLayout? savedLayout;
    private double projectsWidth = 240, inspectorSetting = 280;
    private WindowPlacement? lastWindow;
    /// <summary>Raised when a saved window placement is read for the workspace that just opened.</summary>
    public event Action<WindowPlacement>? WindowRestoreRequested;

    public double ProjectsWidth => projectsWidth;
    public WorkspaceCommand WidenProjectsCommand { get; private set; } = null!;
    public WorkspaceCommand NarrowProjectsCommand { get; private set; } = null!;
    public WorkspaceCommand WidenInspectorCommand { get; private set; } = null!;
    public WorkspaceCommand NarrowInspectorCommand { get; private set; } = null!;

    private void LoadLayout()
    {
        savedLayout = Layouts != null && workspaceKey != null ? Layouts.Load(workspaceKey) : null;
        projectsWidth = savedLayout?.ProjectsWidth ?? 240;
        inspectorSetting = savedLayout?.InspectorWidth ?? 280;
        if (savedLayout?.View is { } view && view is "board" or "plan" or "inbox" or "channel" or "questions" or "team" or "connections") center = view;
        Layout(); // the restored view and widths reach the window
        Notify(nameof(ProjectsWidth));
        savedViews = savedLayout?.Views ?? [];
        NotifySavedViews();
        if (savedLayout?.Window is { } w) WindowRestoreRequested?.Invoke(w);
    }

    /// <summary>The window reports where it is, in pixels, so the next open can put it back.</summary>
    public void NoteWindow(WindowPlacement w) { lastWindow = w; SaveLayout(); }

    public void SaveLayout()
    {
        if (Layouts == null || workspaceKey == null) return;
        Layouts.Save(workspaceKey, new(projectsWidth, inspectorSetting, selected?.Id, center, lastWindow ?? savedLayout?.Window, savedViews));
    }

    private void Resize(ref double field, double delta, double min, double max)
    {
        field = Math.Clamp(field + delta, min, max);
        Notify(nameof(ProjectsWidth)); Notify(nameof(InspectorWidth));
        SaveLayout();
    }

    // --- Board filters and saved views (AH75) ---------------------------------------------------------------
    private readonly Dictionary<string, BoardFilter> filters = [];
    private List<SavedView> savedViews = [];
    public BoardFilter Filter => selected != null && filters.TryGetValue(selected.Id, out var f) ? f : new();
    public string FilterOwner { get => Filter.Owner ?? ""; set => SetBoardFilter(Filter with { Owner = string.IsNullOrWhiteSpace(value) ? null : value.Trim() }); }
    public bool FilterBlocked { get => Filter.BlockedOnly; set => SetBoardFilter(Filter with { BlockedOnly = value }); }
    public string FilterReview { get => Filter.Review; set => SetBoardFilter(Filter with { Review = value }); }
    public string FilterSearch { get => Filter.Search; set => SetBoardFilter(Filter with { Search = value ?? "" }); }
    private string savedViewName = "";
    public string SavedViewName { get => savedViewName; set { savedViewName = value ?? ""; RaiseCommands(); } }
    public IReadOnlyList<string> ReviewChoices { get; } = ["any", "needs", "accepted", "none"];
    public IReadOnlyList<SavedView> SavedViewsHere => [.. savedViews.Where(v => v.ProjectId == selected?.Id)];
    public WorkspaceCommand SaveViewCommand { get; private set; } = null!;
    public WorkspaceCommand ApplyViewCommand { get; private set; } = null!;
    public WorkspaceCommand ClearFilterCommand { get; private set; } = null!;

    private void SetBoardFilter(BoardFilter f)
    {
        if (selected == null) return;
        filters[selected.Id] = f;
        RefreshColumns();
        NotifyFilter();
    }

    private void NotifyFilter() { foreach (var n in new[] { nameof(FilterOwner), nameof(FilterBlocked), nameof(FilterReview), nameof(FilterSearch), nameof(FilterLine), nameof(Filter) }) Notify(n); RaiseCommands(); }
    private void NotifySavedViews() { Notify(nameof(SavedViewsHere)); }
    public string FilterLine => Filter.IsEmpty ? "" : $"{ShownTasks.Count()} of {Tasks.Count} tasks match";

    // --- A steady board (AH79) ---------------------------------------------------------------------------------
    private readonly SteadyBoard steady = new();
    public SteadyBoard Steady => steady;
    public IEnumerable<TaskItem> ShownTasks => Tasks.Where(t => steady.Shown.Contains(t.Id) && Filter.Matches(t));
    public string PendingNote => steady.Pending switch { 0 => "", 1 => "1 change is waiting. Show it", var n => $"{n} changes are waiting. Show them" };
    public bool HasPending => steady.Pending > 0;
    public WorkspaceCommand RevealPendingCommand { get; private set; } = null!;
    public WorkspaceCommand ToggleSelectCommand { get; private set; } = null!;
    /// <summary>The card the window should keep in view after a reveal.</summary>
    public string? ScrollAnchor => steady.Anchor;
    public int SelectedCount => steady.Selected.Count;

    // --- Command menu (AH74) -----------------------------------------------------------------------------------
    private string menuQuery = "";
    public bool MenuOpen { get; private set; }
    public string MenuQuery { get => menuQuery; set { menuQuery = value ?? ""; Notify(); Notify(nameof(MenuEntries)); } }
    public WorkspaceCommand OpenMenuCommand { get; private set; } = null!;
    public WorkspaceCommand CloseMenuCommand { get; private set; } = null!;
    public WorkspaceCommand RunMenuEntryCommand { get; private set; } = null!;
    public string MenuNote { get; private set; } = "";

    public IReadOnlyList<MenuEntry> MenuEntries => BuildMenu(menuQuery);

    private string Unavailable(string id)
    {
        if (!IsWorkspace) return "Open a workspace first.";
        if (ReadOnlyReason != null && id is not ("board" or "plan" or "inbox" or "channel" or "questions" or "back" or "blocker")) return "This window is read-only; another window is editing.";
        if (id is "ask-builder" or "ask-reviewer" or "accept" or "changes" or "blocker" or "require-review" && selectedTask == null) return "Select a task first.";
        return id switch
        {
            "accept" or "changes" => "The selected task has no draft waiting for your decision.",
            "ask-reviewer" => "There is no draft to review yet.",
            "blocker" => "The selected task has no unresolved prerequisite.",
            "back" => "There is nowhere to go back to.",
            _ => "Not available right now.",
        };
    }

    private IReadOnlyList<MenuEntry> BuildMenu(string query)
    {
        var keys = Shortcuts.For(OperatingSystem.IsMacOS());
        string? Key(string k) => keys.TryGetValue(k, out var g) ? g.ToString() : null;
        MenuEntry Act(string id, string title, WorkspaceCommand c, string? shortcut = null, object? p = null) =>
            new("action", title, "action", shortcut == null ? null : Key(shortcut), c.CanExecute(p), c.CanExecute(p) ? null : Unavailable(id), () => c.RunAsync(p));
        List<MenuEntry> all =
        [
            Act("new-task", "New task", NewTaskCommand, "NewTask"),
            Act("ask-builder", "Ask the builder for a draft", AskBuilderCommand, "AskBuilder"),
            Act("ask-reviewer", "Ask the reviewer to review", AskReviewerCommand, "AskReviewer"),
            Act("accept", "Go to the decision", ReviewDecisionCommand, "Accept"),
            Act("changes", "Request changes", RequestChangesCommand, "RequestChanges"),
            Act("require-review", "Require review for this task", ToggleReviewCommand, "RequireReview"),
            Act("blocker", "Go to the blocker", GoToBlockerCommand, "Blocker"),
            Act("back", "Back", BackCommand, "Back"),
            Act("board", "Show the board", ShowBoardCommand, "ShowBoard"),
            Act("plan", "Show the plan and graph", ShowPlanCommand),
            Act("inbox", "Show the inbox", ShowInboxCommand, "ShowInbox"),
            Act("channel", "Show the team channel", ShowChannelCommand, "ShowChannel"),
            Act("questions", "Show questions", ShowQuestionsCommand, "ShowQuestions"),
            Act("team", "Show team and budget", ShowTeamCommand),
            Act("connections", "Show connections", ShowConnectionsCommand),
            Act("refresh", "Refresh", RefreshCommand, "Refresh"),
        ];
        foreach (var role in Team.Roles)
            if (team?.Assignments.FirstOrDefault(a => a.Role == role) is { } a)
                all.Add(new("agent", $"The {role}: {a.ModelId}", "agent", null, true, null, () => { SetCenter("team"); return Task.CompletedTask; }));
        foreach (var t in Tasks)
            all.Add(new("task", t.Title, $"task · {t.Status}", null, true, null, () => { Select(t); return Task.CompletedTask; }));
        string q = query.Trim();
        return [.. all.Where(e => q.Length == 0 || e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Detail.Contains(q, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(e => q.Length > 0 && e.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(e => e.Kind == "action" ? 0 : e.Kind == "agent" ? 1 : 2)];
    }

    // --- Plan, graph, handoffs (AH28-30, AH80-83, AH107) ----------------------------------------------------------
    private string planBrief = "";
    public string PlanBrief { get => planBrief; set { planBrief = value ?? ""; RaiseCommands(); } }
    /// <summary>The proposal on screen: workspace, project, plan and version. The editor text, Save and Approve all belong to exactly this and nothing else.</summary>
    private sealed record PlanKey(string Workspace, string Project, string Plan, int Version);
    private PlanKey? planKey;
    private readonly Dictionary<PlanKey, string> planBuffers = [];
    private int planRead;
    private PlanView? plan;

    public string PlanEditText
    {
        get => planKey is { } k && planBuffers.TryGetValue(k, out var t) ? t : plan is { Status: "proposed" } p ? ToText(p) : "";
        set
        {
            if (planKey is not { } k) return;
            planBuffers[k] = value ?? "";
            NotifyPlanEdit();
        }
    }

    private void NotifyPlanEdit() { foreach (var n in new[] { nameof(PlanEditText), nameof(PlanDirty), nameof(PlanProblems), nameof(PlanProblemsText), nameof(PlanUnsavedNote) }) Notify(n); RaiseCommands(); }
    /// <summary>Unsaved edits to the proposal on screen. Approve waits for Save so the owner approves what is stored, not what is typed.</summary>
    public bool PlanDirty => plan is { Status: "proposed" } p && PlanEditText != ToText(p);
    /// <summary>Problems in the text as typed: a prerequisite naming no task, one title on two tasks, a task waiting for itself.</summary>
    public IReadOnlyList<string> PlanProblems => plan is { Status: "proposed" } ? Plans.Problems(Plans.Parse(PlanEditText).Tasks) : [];
    public string PlanProblemsText => PlanProblems.Count == 0 ? "" : string.Join(" ", PlanProblems);
    public string PlanUnsavedNote { get; private set; } = "";

    private void ClearPlan()
    {
        plan = null; planStatus = null; planKey = null; planRead++;
        PlanNodes.Clear();
        PlanStatusLine = "Reading this project's plan…";
        EstimateLines = []; ActualLines = []; estimate = null;
        foreach (var n in new[] { nameof(PlanStatusLine), nameof(HasProposal), nameof(HasAcceptedPlan), nameof(EstimateLines), nameof(ActualLines) }) Notify(n);
        NotifyPlanEdit();
    }
    public bool PlanVisible => ListVisible && center == "plan";
    public WorkspaceCommand ShowPlanCommand { get; private set; } = null!;
    public WorkspaceCommand ProposePlanCommand { get; private set; } = null!;
    public WorkspaceCommand SavePlanCommand { get; private set; } = null!;
    public WorkspaceCommand ApprovePlanCommand { get; private set; } = null!;
    public WorkspaceCommand DismissPlanCommand { get; private set; } = null!;
    public WorkspaceCommand EstimatePlanCommand { get; private set; } = null!;
    public WorkspaceCommand StartPlanCommand { get; private set; } = null!;
    public WorkspaceCommand ActivateNodeCommand { get; private set; } = null!;
    public WorkspaceCommand OpenNodeBlockerCommand { get; private set; } = null!;
    public WorkspaceCommand ReconcileCommand { get; private set; } = null!;
    public ObservableCollection<PlanNodeRow> PlanNodes { get; } = [];
    public PlanNodeRow? SelectedNode { get; set; }
    public string PlanStatusLine { get; private set; } = "No plan yet. Write a brief and ask the planner for one.";
    public IReadOnlyList<string> EstimateLines { get; private set; } = [];
    public IReadOnlyList<string> ActualLines { get; private set; } = [];
    public bool HasProposal => plan is { Status: "proposed" };
    public bool HasAcceptedPlan => plan is { Status: "accepted" };
    private PlanEstimate? estimate;
    private readonly HandoffAnimator animator = new();
    public HandoffAnimator Animator => animator;
    private HashSet<string> pulsing = [];
    private CancellationTokenSource? pulseCts;
    public bool ReducedMotion { get => animator.Reduced; set { animator.SetReduced(value); pulsing.Clear(); pulseCts?.Cancel(); RebuildNodes(); Notify(); } }
    public bool AnimationPaused { get => animator.Paused; set { animator.Paused = value; Notify(); } }
    private PlanStatus? planStatus;

    private static string ToText(PlanView p) => string.Join('\n', p.Tasks.Select(t =>
        $"{t.Title} | {t.Owner} | {t.Criteria}" + (!string.IsNullOrWhiteSpace(t.After) ? " | after: " + t.After : p.Edges.Where(e => e.Key == t.Key).Select(e => p.Tasks.First(x => x.Key == e.Requires).Title).ToList() is { Count: > 0 } after ? " | after: " + string.Join("; ", after) : "")));

    private async Task RefreshPlanAsync(bool live)
    {
        if (!IsWorkspace || path is not string file || selected is not { } project) return;
        int token = ++planRead; // a read that started earlier never applies after a later one, or after a project switch
        try
        {
            var client = await Worker();
            var plans = await client.ReadPlansAsync(file, project.Id);
            if (token != planRead || path != file || selected?.Id != project.Id) return;
            var shown = plans.FirstOrDefault(p => p.Status == "proposed") ?? plans.FirstOrDefault(p => p.Status == "accepted");
            var status = shown is { Status: "accepted" } ? await client.PlanStatusAsync(file, shown.Id) : null;
            if (token != planRead || path != file || selected?.Id != project.Id) return;
            plan = shown;
            planStatus = status;
            var key = plan is { Status: "proposed" } np ? new PlanKey(file, project.Id, np.Id, np.Version) : null;
            // Edits typed for an older version of this same proposal are kept, not replaced and not applied.
            PlanUnsavedNote = key != null && planBuffers.Any(b => b.Key.Workspace == file && b.Key.Plan == key.Plan && b.Key.Version < key.Version && b.Value != ToText(plan!))
                ? "Your unsaved text for an earlier version of this proposal was kept aside. This is the stored version." : "";
            planKey = key;
            if (planStatus != null)
            {
                if (live)
                {
                    var events = await client.PlanHandoffsAsync(file, project.Id, animator.Seen);
                    if (animator.Live(events) is { } h) Pulse(h);
                }
                else animator.Baseline(planStatus.Seq);
            }
            PlanStatusLine = plan == null ? "No plan yet. Write a brief and ask the planner for one."
                : plan.Status == "proposed" ? $"Proposal, version {plan.Version}: edit it, then approve or dismiss. Nothing runs until you approve and start it."
                : $"Approved plan, version {plan.Version}: {plan.Tasks.Count} tasks." + (planStatus!.Overlaps.Count > 0 ? $" {planStatus.Overlaps.Count} pair(s) of attempts ran at the same time." : "");
            RebuildNodes();
            foreach (var n in new[] { nameof(PlanStatusLine), nameof(HasProposal), nameof(HasAcceptedPlan) }) Notify(n);
            NotifyPlanEdit();
        }
        catch (WorkerException) { }
    }

    private void Pulse(HandoffEvent h)
    {
        pulsing = [.. h.Targets];
        pulseCts?.Cancel();
        var cts = pulseCts = new CancellationTokenSource();
        RebuildNodes();
        var ui = SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default;
        _ = Task.Delay(1200, cts.Token).ContinueWith(t => { if (!t.IsCanceled) { animator.Finish(); pulsing.Clear(); RebuildNodes(); } }, ui);
    }

    private void RebuildNodes()
    {
        PlanNodes.Clear();
        if (planStatus == null || plan == null) { Notify(nameof(PlanNodes)); return; }
        var order = new List<string>();
        foreach (var n in plan.Tasks) foreach (var k in Plans.Traversal(planStatus.Edges, n.Key)) if (!order.Contains(k)) order.Add(k);
        foreach (var key in order)
        {
            var node = planStatus.Nodes.First(n => n.Key == key);
            PlanNodes.Add(new(node, string.Join(", ", node.Needs), string.Join(", ", node.Feeds), pulsing.Contains(node.TaskId) && !animator.Reduced));
        }
        Notify(nameof(PlanNodes));
    }

    private void InitDay5()
    {
        WidenProjectsCommand = new(_ => true, _ => { Resize(ref projectsWidth, 40, 160, 400); return Task.CompletedTask; });
        NarrowProjectsCommand = new(_ => true, _ => { Resize(ref projectsWidth, -40, 160, 400); return Task.CompletedTask; });
        WidenInspectorCommand = new(_ => true, _ => { Resize(ref inspectorSetting, 40, 240, 480); return Task.CompletedTask; });
        NarrowInspectorCommand = new(_ => true, _ => { Resize(ref inspectorSetting, -40, 240, 480); return Task.CompletedTask; });

        SaveViewCommand = new(_ => selected != null && !Filter.IsEmpty && SavedViewName.Trim().Length > 0, _ =>
        {
            var f = Filter;
            savedViews.RemoveAll(v => v.ProjectId == selected!.Id && v.Name == SavedViewName.Trim());
            savedViews.Add(new(SavedViewName.Trim(), selected!.Id, f.Owner, f.BlockedOnly, f.Review)); // the search text is private to the live filter and is not saved
            SavedViewName = ""; Notify(nameof(SavedViewName)); NotifySavedViews(); SaveLayout();
            return Task.CompletedTask;
        });
        ApplyViewCommand = new(p => p is SavedView v && v.ProjectId == selected?.Id, p =>
        {
            var v = (SavedView)p!;
            SetBoardFilter(Filter with { Owner = v.Owner, BlockedOnly = v.BlockedOnly, Review = v.Review });
            return Task.CompletedTask;
        });
        ClearFilterCommand = new(_ => !Filter.IsEmpty, _ => { SetBoardFilter(new()); return Task.CompletedTask; });
        RevealPendingCommand = new(_ => steady.Pending > 0, _ => { steady.Reveal(); RefreshColumns(); Notify(nameof(PendingNote)); Notify(nameof(HasPending)); Notify(nameof(ScrollAnchor)); return Task.CompletedTask; });
        ToggleSelectCommand = new(p => p is TaskItem, p =>
        {
            var t = (TaskItem)p!;
            if (!steady.Selected.Remove(t.Id)) steady.Selected.Add(t.Id);
            steady.Focus = t.Id;
            Notify(nameof(SelectedCount));
            return Task.CompletedTask;
        });

        OpenMenuCommand = new(_ => true, _ => { MenuOpen = true; menuQuery = ""; Notify(nameof(MenuOpen)); Notify(nameof(MenuQuery)); Notify(nameof(MenuEntries)); return Task.CompletedTask; });
        CloseMenuCommand = new(_ => MenuOpen, _ => { MenuOpen = false; MenuNote = ""; Notify(nameof(MenuOpen)); Notify(nameof(MenuNote)); return Task.CompletedTask; });
        RunMenuEntryCommand = new(p => p is MenuEntry, async p =>
        {
            var e = (MenuEntry)p!;
            if (!e.Enabled) { MenuNote = e.DisabledReason ?? ""; Notify(nameof(MenuNote)); return; }
            MenuOpen = false; MenuNote = ""; Notify(nameof(MenuOpen)); Notify(nameof(MenuNote));
            await e.Run();
        });

        ShowPlanCommand = new(_ => IsWorkspace, async _ => { SetCenter("plan"); Notify(nameof(PlanVisible)); await RefreshPlanAsync(false); });
        ProposePlanCommand = new(_ => CanWrite && selected != null && PlanBrief.Trim().Length > 0, async _ =>
        {
            string ws = path!, brief = PlanBrief.Trim(), projectId = selected!.Id, click = Guid.NewGuid().ToString("N");
            await Fail(async () =>
            {
                var tasks = plan is { Status: "proposed" } && PlanEditText.Trim().Length > 0 && false ? null : (PlanTask[]?)null;
                string? error = await Submit($"plan|{ws}|{click}", ws, async id => await (await Worker()).ProposePlanAsync(ws, id, projectId, brief, tasks, null));
                if (error == null) await RefreshPlanAsync(false);
                return error;
            });
        });
        SavePlanCommand = new(_ => CanWrite && planKey != null && plan is { Status: "proposed" } && PlanDirty, async _ =>
        {
            var p = plan!; var key = planKey!; string ws = path!, click = Guid.NewGuid().ToString("N");
            var (tasks, edges) = Plans.Parse(PlanEditText); // frozen now: what is typed later is not what this sends
            await Fail(async () =>
            {
                string? error = await Submit($"planedit|{ws}|{p.Id}|{p.Version}|{click}", ws, async id => await (await Worker()).EditPlanAsync(ws, id, p.Id, p.Version, [.. tasks], [.. edges]));
                if (error == null) { planBuffers.Remove(key); await RefreshPlanAsync(false); }
                return error;
            });
        });
        ApprovePlanCommand = new(_ => CanWrite && planKey != null && plan is { Status: "proposed" } p0 && !PlanDirty && Plans.Problems(p0.Tasks).Count == 0, async _ =>
        {
            var p = plan!; string ws = path!, click = Guid.NewGuid().ToString("N");
            await Fail(async () =>
            {
                string? error = await Submit($"planok|{ws}|{p.Id}|{p.Version}", ws, async id => await (await Worker()).ApprovePlanAsync(ws, id, p.Id, p.Version));
                if (error == null) { await ReloadAfterPlanAsync(); }
                return error;
            });
        });
        DismissPlanCommand = new(_ => CanWrite && plan is { Status: "proposed" }, async _ =>
        {
            var p = plan!; string ws = path!;
            await Fail(async () =>
            {
                string? error = await Submit($"plandismiss|{ws}|{p.Id}|{p.Version}", ws, async id => await (await Worker()).DismissPlanAsync(ws, id, p.Id, p.Version));
                if (error == null) await RefreshPlanAsync(false);
                return error;
            });
        });
        EstimatePlanCommand = new(_ => IsWorkspace && plan is { Status: "accepted" }, async _ => await Guarded(async () =>
        {
            estimate = await (await Worker()).PlanEstimateAsync(path!, plan!.Id);
            var actuals = await (await Worker()).PlanActualsAsync(path!, plan.Id);
            EstimateLines = EstimateText(estimate);
            ActualLines = [.. actuals.Select(a => $"{a.Role} · {a.Model}: estimated {(a.Estimate is { } e ? e.ToString("0.000000") : "unknown")}, used {(a.InputTokens is { } i ? $"{i} in / {a.OutputTokens} out" : "usage not reported")}, cost {(a.Cost is { } c ? c.ToString("0.000000") : "unknown")} ({a.Status.Replace('_', ' ')})")];
            Notify(nameof(EstimateLines)); Notify(nameof(ActualLines));
            RaiseCommands();
        }));
        StartPlanCommand = new(_ => CanWrite && plan is { Status: "accepted" } && estimate != null, async _ =>
        {
            var e = estimate!; string ws = path!, planId = plan!.Id, click = Guid.NewGuid().ToString("N");
            await Guarded(async () =>
            {
                var r = await (await Worker()).StartPlanAsync(ws, "start-" + click, planId, e.Tasks.Where(t => t.Ready && t.Binding != null).ToDictionary(t => t.TaskId, t => t.Binding!));
                if (r.Skipped.Any(s => !s.Reason.StartsWith("Already") && !s.Reason.StartsWith("Waiting")))
                    ShowNotice(string.Join(" ", r.Skipped.Where(s => !s.Reason.StartsWith("Already") && !s.Reason.StartsWith("Waiting")).Select(s => s.Reason)), "Dismiss", DismissNoticeCommand);
                estimate = null;
                await RefreshRunsAsync();
                await RefreshPlanAsync(false);
            });
        });
        ActivateNodeCommand = new(p => (p as PlanNodeRow ?? SelectedNode) != null, p =>
        {
            var row = p as PlanNodeRow ?? SelectedNode!;
            if (Tasks.FirstOrDefault(t => t.Id == row.Node.TaskId) is { } task) { arrivedViaBlocker = false; Select(task); }
            return Task.CompletedTask;
        });
        OpenNodeBlockerCommand = new(p => (p as PlanNodeRow ?? SelectedNode)?.Node.Blockers.Count > 0, p =>
        {
            var row = p as PlanNodeRow ?? SelectedNode!;
            if (Tasks.FirstOrDefault(t => t.Id == row.Node.TaskId) is { } source) { history.Add(source.Id); if (history.Count > MaxHistory) history.RemoveAt(0); }
            if (Tasks.FirstOrDefault(t => t.Id == row.Node.Blockers[0].TaskId) is { } target) { arrivedViaBlocker = true; Select(target); }
            RaiseCommands();
            return Task.CompletedTask;
        });
        PinRevisionCommand = new(_ => Review is { HasOutput: true } && !Pinned, _ => { pinnedTask = selectedTask!.Id; NotifyReview(); return Task.CompletedTask; });
        UnpinRevisionCommand = new(_ => Pinned, _ => { pinnedTask = null; releasePlace = true; ComparisonLines = []; return LoadReviewAsync(); });
        ComparePinnedCommand = new(_ => Review is { IsStale: true }, async _ => await Guarded(async () =>
        {
            string file = path!, task = selectedTask!.Id, pinnedId = Review!.OutputId!;
            var latest = await Task.Run(() => new ProjectReader(file).Review(task));
            if (latest?.OutputId == null) return;
            var diff = await Task.Run(() => Outputs.Compare(file, pinnedId, latest.OutputId));
            ComparisonLines = [$"Revision {Review.Revision} → revision {latest.Revision}", .. diff.Lines.Select(l => $"{l.Op} {l.Text}")];
            Notify(nameof(ComparisonLines));
        }));
        ReconcileCommand = new(_ => false, _ => Task.CompletedTask); // shortcut: reconciling an unknown charge is a worker operation and a client method; no window control yet
        commands = [.. commands, WidenProjectsCommand, NarrowProjectsCommand, WidenInspectorCommand, NarrowInspectorCommand, SaveViewCommand, ApplyViewCommand, ClearFilterCommand, RevealPendingCommand,
            ToggleSelectCommand, OpenMenuCommand, CloseMenuCommand, RunMenuEntryCommand, ShowPlanCommand, ProposePlanCommand, SavePlanCommand, ApprovePlanCommand, DismissPlanCommand,
            EstimatePlanCommand, StartPlanCommand, ActivateNodeCommand, OpenNodeBlockerCommand, ComparePinnedCommand, PinRevisionCommand, UnpinRevisionCommand];
    }

    private async Task ReloadAfterPlanAsync()
    {
        if (path is string file) await OpenAsync(file, workspaceName);
        await RefreshPlanAsync(false);
    }

    private static IReadOnlyList<string> EstimateText(PlanEstimate e)
    {
        List<string> lines = [];
        foreach (var r in e.Roles)
            lines.Add($"{char.ToUpper(r.Role[0])}{r.Role[1..]} · {r.Model ?? "no model assigned"}: " + (r.Estimate is { } x ? $"{x:0.000000} {e.Currency}" : "price unknown") + $" for {r.Tasks} task(s)" + (r.Note != null ? $". {r.Note}" : ""));
        lines.Add(e.TotalComplete ? $"Total {e.Total:0.000000} {e.Currency}" : $"Known so far {e.Total:0.000000} {e.Currency}; the rest has no price");
        if (e.UnknownPricing.Count > 0) lines.Add("Unknown pricing: " + string.Join("; ", e.UnknownPricing));
        lines.Add($"Unresolved charges: {e.UnresolvedCharges + e.UnknownUsage}. " + (e.CapLimit is { } l ? $"Cap {l:0.000000} {e.Currency}, {e.RemainingCap:0.000000} left." : "No spending cap."));
        return lines;
    }

    // --- Following a blocker, and pinning a revision (AH82, AH83) --------------------------------------------------
    private bool arrivedViaBlocker;
    /// <summary>Said when the owner followed a blocker and the prerequisite has produced nothing to open.</summary>
    public string EvidenceNote => arrivedViaBlocker && selectedTask is { } t && Review is { HasOutput: false } ? $"“{t.Title}” has not produced an output yet, so there is no revision to open. It is {t.Status}." : "";
    public bool HasEvidenceNote => EvidenceNote.Length > 0;
    public IReadOnlyList<string> ComparisonLines { get; private set; } = [];
    private string? pinnedTask;
    public bool Pinned => pinnedTask != null && pinnedTask == selectedTask?.Id;
    public WorkspaceCommand PinRevisionCommand { get; private set; } = null!;
    public WorkspaceCommand UnpinRevisionCommand { get; private set; } = null!;
    public WorkspaceCommand ComparePinnedCommand { get; private set; } = null!;

    /// <summary>Shown beside the pinned text when a newer revision exists: a comparison on request, never a switch.</summary>
    public string PinNote => Pinned && Review is { IsStale: true } r ? $"You pinned revision {r.Revision}. Revision {r.LatestRevision} has been delivered; compare it, or open it when you are ready." : Pinned ? $"Pinned: revision {Review?.Revision}." : "";
}
