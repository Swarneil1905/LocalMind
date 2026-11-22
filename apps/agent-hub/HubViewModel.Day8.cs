using System.Collections.ObjectModel;
using System.Text.Json;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

public sealed record GuideStep(string Text, bool Done);

/// <summary>First launch, the offline tutorial, honest empty states, recipes, the words the window uses, appearance, and opt-in feedback (Day 8).</summary>
public sealed partial class HubViewModel
{
    // --- Welcome and the offline tutorial (AH53) ---------------------------------------------------------------------------------
    public const string DemoName = "Offline tutorial (demo content)";
    public const string TutorialTaskTitle = "Write a one-line welcome note";
    private TutorialResponder? responder;
    public TutorialResponder? Responder => responder;
    public string TutorialFolder { get; set; } = System.IO.Path.Combine(LaunchOptions.DataRoot, "tutorial");
    public bool WelcomeVisible => !IsWorkspace;
    public bool GuideVisible => ListVisible && center == "guide";
    public bool IsDemo => workspaceName == DemoName;
    public string DemoBanner => IsDemo ? "Demo content. This workspace was made by the offline tutorial; its model is a fixed text responder, not an AI. Nothing here is real work." : "";
    public const string LiveDisclosure = "Going live is a separate choice you make later. A cloud provider such as OpenRouter charges your own account at the price it lists for each model, and Agent Hub shows its estimate before every send. Sending a task to a cloud model transfers the handoff packet you can read in the preview (the task text and any files you include) to that provider; a local model keeps it on this computer. Nothing is sent, and no key is needed, until you add a connection, enter a key and confirm a send.";
    public string LiveDisclosureText => LiveDisclosure;
    public void RefreshGuideForCheck() => RefreshGuide();
    public string WelcomeText { get; } = "Agent Hub keeps a board of tasks and lets AI models draft the work while you decide what is accepted. You can try the whole loop now without an account, a key or the internet: the tutorial opens a demo workspace whose one task is answered by a fixed responder built into the app. Or open a workspace you already have.";
    public ObservableCollection<GuideStep> GuideSteps { get; } = [];
    public WorkspaceCommand StartTutorialCommand { get; private set; } = null!;
    public WorkspaceCommand ShowGuideCommand { get; private set; } = null!;
    public WorkspaceCommand SetUpLiveCommand { get; private set; } = null!;
    public string TutorialStatus { get; private set; } = "";

    private static string Cid() => Guid.NewGuid().ToString("N");

    private void SetTutorial(string text) { TutorialStatus = text; Notify(nameof(TutorialStatus)); }

    private async Task StartTutorialAsync()
    {
        string folder = TutorialFolder; string db = System.IO.Path.Combine(folder, ProjectStore.FileName), portFile = System.IO.Path.Combine(folder, "tutorial.port");
        var w = await Worker();
        if (File.Exists(db) && int.TryParse(File.Exists(portFile) ? File.ReadAllText(portFile) : "", out int saved)) { responder?.Dispose(); responder = new TutorialResponder(saved); }
        else if (!File.Exists(db))
        {
            responder?.Dispose(); responder = new TutorialResponder();
            var info = await w.CreateWorkspaceAsync(folder, DemoName);
            Directory.CreateDirectory(folder); File.WriteAllText(portFile, responder.Port.ToString());
            var made = await w.CreateTaskAsync(info.DatabasePath, Cid(), info.ProjectId, TutorialTaskTitle, "Write one short sentence welcoming a new team member.");
            await w.UpdateTaskAsync(info.DatabasePath, Cid(), made.Id, 0, reviewRequired: true); // so the draft waits for the owner's decision
            await ConnectResponderAsync(w, info.DatabasePath, info.ProjectId);
            var s = new ProjectSettings(info.ProjectId, true, null, "USD", 8, 120, 8, 2048, null, false, 2);
            await w.SetProjectSettingsAsync(info.DatabasePath, Cid(), s);
            await w.ReleaseWorkspaceAsync(info.DatabasePath);
        }
        else { responder?.Dispose(); responder = new TutorialResponder(); File.WriteAllText(portFile, responder.Port.ToString()); }
        await OpenAsync(db, DemoName);
        await LoadTeamAsync();
        // A saved tutorial may point at the port an earlier launch used. If this launch could not have it, connect the responder again.
        if (team?.Connections.FirstOrDefault(c => c.Label == TutorialResponder.Label) is { } conn && conn.Endpoint.TrimEnd('/') != responder!.Url.TrimEnd('/') && selected != null)
        {
            await w.RemoveConnectionAsync(path!, Cid(), conn.Id);
            await ConnectResponderAsync(w, path!, selected.Id);
            File.WriteAllText(portFile, responder.Port.ToString());
            await LoadTeamAsync();
        }
        SetCenter("guide"); Notify(nameof(GuideVisible)); RefreshGuide();
        SetTutorial("The tutorial workspace is open. Follow the steps below; each one turns done when the workspace shows it happened.");
    }

    private async Task ConnectResponderAsync(WorkerClient w, string db, string project)
    {
        string conn = (await w.AddConnectionAsync(db, Cid(), "ollama", responder!.Url, TutorialResponder.Label)).Id;
        await w.ProbeConnectionAsync(db, conn);
        await w.AssignRoleAsync(db, Cid(), project, "builder", conn, TutorialResponder.ModelId);
    }

    /// <summary>Each step is true only when the workspace shows it: the task exists, a draft exists, it is accepted, a live connection exists.</summary>
    private void RefreshGuide()
    {
        GuideSteps.Clear();
        var t = Tasks.FirstOrDefault(x => x.Title == TutorialTaskTitle);
        bool hasTeam = team?.Assignments.Any(a => a.Role == "builder") == true;
        bool accepted = t is { Status: "done" } || t is { ReviewAccepted: true };
        bool draft = accepted || (selectedTask?.Id == t?.Id && Review is { HasOutput: true }); // an output revision exists for the selected task, not merely a task edit
        bool live = team?.Connections.Any(c => c.IsCloud) == true;
        void Add(string text, bool done) => GuideSteps.Add(new((done ? "Done · " : "Next · ") + text, done));
        if (!IsDemo) { Add("Open the tutorial from the welcome screen to follow these steps on a demo task.", false); }
        else
        {
            Add("The tutorial responder is connected as the builder (offline, no key).", hasTeam);
            Add($"Select the task “{TutorialTaskTitle}”. Arrow keys move between cards; Enter opens one.", t != null && selectedTask?.Id == t.Id || draft);
            Add("In Task details, under Models, choose Send to the builder…. It shows exactly what would be sent. Choose Send it. (The Ask builder button above it is a separate fixed demo, not this responder.)", draft);
            Add("Read the draft in the review panel. It is a draft until you decide.", draft);
            Add("Decide: Ctrl+Shift+A goes to the decision to accept the revision; Ctrl+Shift+C asks for changes.", accepted);
        }
        Add("Optional, later: go live with a cloud provider (see the disclosure below).", live);
        Notify(nameof(GuideSteps));
    }

    // --- Empty states (AH95) ----------------------------------------------------------------------------------------------------------
    private bool workerDown;
    public string BoardEmptyKind => workerDown ? "worker" : !IsWorkspace ? "" : Tasks.Count == 0 ? "empty" : !ShownTasks.Any() ? "filtered" : "";
    public bool BoardEmptyVisible => BoardEmptyKind != "";
    public bool WelcomeWorkerDown => workerDown && WelcomeVisible;
    public string BoardEmptyTitle => BoardEmptyKind switch
    {
        "worker" => "The background worker is not running",
        "empty" => "This project has no tasks yet",
        "filtered" => "No task matches this filter",
        _ => "",
    };
    public string BoardEmptyText => BoardEmptyKind switch
    {
        "worker" => "Agent Hub cannot read or save anything until its worker is back. Nothing you already saved was lost. Try again; if it keeps failing, restart Agent Hub.",
        "empty" => "Nothing has been added to this project, and nothing is hidden. Add the first task, or open the offline tutorial from the Guide to see how a task moves.",
        "filtered" => $"{Tasks.Count} task(s) are hidden by the filter you set. They have not gone anywhere.",
        _ => "",
    };
    public string BoardEmptyActionLabel => BoardEmptyKind switch { "worker" => "Try again", "empty" => "Add the first task", "filtered" => "Clear filters", _ => "" };
    public WorkspaceCommand BoardEmptyActionCommand { get; private set; } = null!;

    private void NotifyEmpty()
    {
        foreach (var n in new[] { nameof(WelcomeVisible), nameof(WelcomeWorkerDown), nameof(BoardEmptyKind), nameof(BoardEmptyVisible), nameof(BoardEmptyTitle), nameof(BoardEmptyText), nameof(BoardEmptyActionLabel), nameof(IsDemo), nameof(DemoBanner), nameof(GuideVisible) }) Notify(n);
    }

    // --- The words the window uses (AH92) ---------------------------------------------------------------------------------------------
    public IReadOnlyList<Term> VocabularyTerms => Vocabulary.Terms;

    // --- A stale card move (AH78) ------------------------------------------------------------------------------------------------------
    private (string Workspace, string Task, string Target, string Title)? staleMove;

    private WorkspaceCommand ReloadAfterStale() => new(_ => true, async _ =>
    {
        var m = staleMove; staleMove = null;
        await RefreshCommand.RunAsync();
        if (m == null || path != m.Value.Workspace) return;
        var t = Tasks.FirstOrDefault(x => x.Id == m.Value.Task);
        string wanted = m.Value.Target.Replace('_', ' ');
        if (t == null) { ShowNotice($"“{m.Value.Title}” is no longer on the board, so your move was not applied.", "Dismiss", DismissNoticeCommand); return; }
        string here = ColumnOf(t.Status);
        if (t.Status.Replace('_', ' ') == wanted) { ShowNotice($"Someone else already moved “{m.Value.Title}” to {here}. There is nothing to reapply.", "Dismiss", DismissNoticeCommand); return; }
        ShowNotice($"Reloaded from the saved board. “{m.Value.Title}” is in {here}, because someone changed it after you picked it up. Your move to {wanted} was not applied. Do you want to apply it now?", "Reapply my move",
            new(_ => true, _ => { SelectedTask = t; return MoveAsync(m.Value.Target); }));
    });

    // --- Recipes (AH58) ----------------------------------------------------------------------------------------------------------------
    private string recipeText = "";
    public string RecipeText { get => recipeText; set { recipeText = value ?? ""; RaiseCommands(); } }
    public string RecipePreviewText { get; private set; } = "";
    private RecipePreview? recipePreview;
    public WorkspaceCommand MakeRecipeCommand { get; private set; } = null!;
    public WorkspaceCommand PreviewRecipeCommand { get; private set; } = null!;
    public WorkspaceCommand ApplyRecipeCommand { get; private set; } = null!;

    private void ShowRecipePreview(RecipePreview p)
    {
        recipePreview = p;
        var lines = new List<string>();
        lines.AddRange(p.Problems.Select(x => "Problem: " + x));
        lines.AddRange(p.Dropped.Select(x => "Removed: " + x));
        if (p.Recipe != null) lines.Add($"Recipe “{p.Recipe.Name}” (version {p.Recipe.Version}): {p.Recipe.Roles.Count} role(s), up to {p.Recipe.MaxSteps} steps, {p.Recipe.MaxSeconds} s, {p.Recipe.MaxToolCalls} tool calls, {p.Recipe.MaxOutputTokens} output tokens, auto review {(p.Recipe.AutoReview ? "on" : "off")}.");
        lines.AddRange(p.Matches.Select(m => $"{m.Role}: {m.Model}. {m.Note}"));
        if (p.Problems.Count == 0 && p.Matches.Count > 0 && p.Matches.All(m => m.ConnectionId == null)) lines.Add("Nothing can be applied yet: no role matches a connection here.");
        else if (p.Problems.Count == 0 && p.Matches.Any(m => m.ConnectionId != null)) lines.Add("You can edit the text above and preview again. Apply sets only the roles that matched, and the limits. It changes no key, price, consent or file permission.");
        RecipePreviewText = string.Join("\n", lines); Notify(nameof(RecipePreviewText)); RaiseCommands();
    }

    // --- Appearance (AH97) ---------------------------------------------------------------------------------------------------------------
    private int themeChoice, scaleChoice;
    public IReadOnlyList<string> ThemeNames => Appearance.ThemeNames;
    public IReadOnlyList<string> ScaleNames { get; } = ["100%", "125%", "150%", "200%"];
    public int ThemeChoice { get => themeChoice; set { themeChoice = value; ApplyAppearance(); } }
    public int ScaleChoice { get => scaleChoice; set { scaleChoice = value; ApplyAppearance(); } }
    public string AppearanceNote => $"Showing the {Appearance.CurrentName} theme at {Appearance.CurrentScale * 100:0}% text size." + (Motion.Reduced ? " Windows asks for reduced motion, so animation is off." : "");
    private void ApplyAppearance()
    {
        Appearance.Apply(Appearance.Resolve(Appearance.ThemeNames[Math.Clamp(themeChoice, 0, 3)]), Appearance.Scales[Math.Clamp(scaleChoice, 0, 3)]);
        Notify(nameof(AppearanceNote)); Notify(nameof(ThemeChoice)); Notify(nameof(ScaleChoice));
    }

    // --- Feedback (AH117) -------------------------------------------------------------------------------------------------------------------
    private string betaText = "", betaRun = "";
    private bool betaConsent;
    public string BetaText { get => betaText; set { betaText = value ?? ""; NotifyBeta(); } }
    public string BetaRunId { get => betaRun; set { betaRun = value ?? ""; NotifyBeta(); } }
    public bool BetaConsent { get => betaConsent; set { betaConsent = value; NotifyBeta(); } }
    public string BetaFolder { get; set; } = System.IO.Path.Combine(LaunchOptions.DataRoot, "feedback");
    public string BetaStatus { get; private set; } = "";
    public static string BuildVersion => typeof(HubViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
    /// <summary>The run this report is about: the newest attempt of the selected task, or whatever the tester typed.</summary>
    private string SuggestedRun => AttemptRows.LastOrDefault()?.A.Id ?? "";
    public string BetaPreview => Feedback.Render(Feedback.Build(BuildVersion, string.IsNullOrWhiteSpace(betaRun) ? SuggestedRun : betaRun, betaText, betaConsent, DiagnosticsPreview, DateTime.UtcNow));
    public WorkspaceCommand SaveBetaReportCommand { get; private set; } = null!;
    private void NotifyBeta() { Notify(nameof(BetaPreview)); RaiseCommands(); }

    // --- Commands ----------------------------------------------------------------------------------------------------------------------------
    private WorkspaceCommand[] InitDay8()
    {
        StartTutorialCommand = new(_ => workspaces != null, _ => Guarded(StartTutorialAsync));
        ShowGuideCommand = new(_ => true, async _ => { if (IsWorkspace) { await LoadTeamAsync(); SetCenter("guide"); } RefreshGuide(); NotifyEmpty(); });
        SetUpLiveCommand = new(_ => IsWorkspace, _ => ShowConnectionsCommand.RunAsync());
        BoardEmptyActionCommand = new(_ => BoardEmptyVisible, _ => BoardEmptyKind switch
        {
            "worker" => Guarded(async () => { workerDown = false; NotifyEmpty(); await Worker(); /* throws again, and the state returns, if it is still not there */ if (path is string f) await OpenAsync(f, workspaceName); }),
            "empty" => NewTaskCommand.RunAsync(),
            "filtered" => ClearFilterCommand.RunAsync(),
            _ => Task.CompletedTask,
        });
        MakeRecipeCommand = new(_ => IsWorkspace, _ => Guarded(async () =>
        {
            await LoadTeamAsync();
            if (team == null) return;
            RecipeText = Recipes.Export(workspaceName ?? "My team", team); Notify(nameof(RecipeText));
            ShowRecipePreview(Recipes.Preview(RecipeText, team));
        }));
        PreviewRecipeCommand = new(_ => IsWorkspace && RecipeText.Trim().Length > 0, _ => Guarded(async () => { await LoadTeamAsync(); if (team != null) ShowRecipePreview(Recipes.Preview(RecipeText, team)); }));
        ApplyRecipeCommand = new(_ => CanWrite && recipePreview is { Recipe: not null, Problems.Count: 0 } p && p.Matches.Any(m => m.ConnectionId != null), _ => Guarded(async () =>
        {
            var p = recipePreview!; var r = p.Recipe!; string ws = path!, project = selected!.Id; var w = await Worker();
            foreach (var m in p.Matches.Where(m => m.ConnectionId != null)) await w.AssignRoleAsync(ws, Cid(), project, m.Role, m.ConnectionId!, m.Model);
            var cur = team!.Settings;
            await w.SetProjectSettingsAsync(ws, Cid(), cur with { MaxSteps = r.MaxSteps, MaxSeconds = r.MaxSeconds, MaxToolCalls = r.MaxToolCalls, MaxOutputTokens = r.MaxOutputTokens, AutoReview = r.AutoReview, MaxConcurrency = r.MaxConcurrency });
            await LoadTeamAsync();
            RecipePreviewText = $"Applied {p.Matches.Count(m => m.ConnectionId != null)} role(s) and the limits. {p.Matches.Count(m => m.ConnectionId == null)} role(s) were left as they were.";
            Notify(nameof(RecipePreviewText));
        }));
        SaveBetaReportCommand = new(_ => betaText.Trim().Length > 0, _ =>
        {
            try
            {
                Directory.CreateDirectory(BetaFolder);
                string file = System.IO.Path.Combine(BetaFolder, $"feedback-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(file, BetaPreview);
                BetaStatus = $"Saved to {file}. Nothing was sent. Read it first; if you agree, attach that file to your message to the team.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { BetaStatus = $"The report could not be saved ({e.Message.TrimEnd('.')}). Choose another folder or free some space, then try again. Your text is still here."; }
            Notify(nameof(BetaStatus)); return Task.CompletedTask;
        });
        return [StartTutorialCommand, ShowGuideCommand, SetUpLiveCommand, BoardEmptyActionCommand, MakeRecipeCommand, PreviewRecipeCommand, ApplyRecipeCommand, SaveBetaReportCommand];
    }
}
