using System.Collections.ObjectModel;
using System.ComponentModel;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>One connection as the Connections view lists it.</summary>
public sealed record ConnectionRow(ConnectionInfo Info)
{
    public string Title => $"{Info.Label} · {(Info.Provider == "ollama" ? "Ollama" : "OpenRouter")}";
    public string Where => $"{Info.Endpoint} · {(Info.IsCloud ? "cloud: packets leave this computer" : "this computer")}";
    public string Key => Info.Provider == "ollama" ? "No key needed." : Info.CredentialStatus switch
    {
        "stored" => "Key stored in the operating system's credential store.",
        "rotated" => $"Key replaced {When(Info.CredentialChangedAt)}; stored in the credential store.",
        "deleted" => "Key deleted. Enter a key to use this connection.",
        _ => "No key yet. Enter one to use this connection.",
    };
    public string Probe => Info.LastProbeAt == null ? "Not probed yet." : Info.LastProbeOk == true ? $"Last successful probe {When(Info.LastProbeAt)}." : $"Last probe failed {When(Info.LastProbeAt)}: {Info.LastError}";
    public IReadOnlyList<string> ModelLines => [.. Info.Models.Select(m => $"{m.Id} — {m.Capabilities.Describe()}" + (m.Available ? "" : " — not listed at the last probe"))];
    public bool NeedsKey => Info.Provider != "ollama";
    public string AutomationName => Title;
    public override string ToString() => Title;
    private static string When(string? at) => DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime().ToString("g") : "";
}

/// <summary>A model a role can use: provider (connection) and model ID kept apart.</summary>
public sealed record ModelChoice(string ConnectionId, string ConnectionLabel, string Provider, bool IsCloud, string ModelId, ModelCapabilities Capabilities, bool Available)
{
    public string Label => $"{ModelId}  ·  {ConnectionLabel} ({(IsCloud ? "cloud" : "this computer")})";
    public override string ToString() => Label;
}

/// <summary>One role in the team editor.</summary>
public sealed class RoleRow(string role, HubViewModel hub) : INotifyPropertyChanged
{
    private ModelChoice? selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Role { get; } = role;
    public string Heading => char.ToUpperInvariant(Role[0]) + Role[1..];
    public IReadOnlyList<ModelChoice> Choices { get; set; } = [];
    public string Explanation { get; set; } = "";
    public ModelChoice? Selected
    {
        get => selected;
        set
        {
            if (Equals(selected, value)) return;
            selected = value;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
            if (value != null && !hub.LoadingTeam) _ = hub.AssignAsync(this, value);
        }
    }
    internal void Set(ModelChoice? choice, string explanation)
    {
        selected = choice;
        Explanation = explanation;
        PropertyChanged?.Invoke(this, new(nameof(Selected)));
        PropertyChanged?.Invoke(this, new(nameof(Explanation)));
        PropertyChanged?.Invoke(this, new(nameof(Choices)));
    }
}

/// <summary>One model attempt as the run inspector shows it: who received what, which model answered, and what it cost.</summary>
public sealed record AttemptRow(AttemptView A, string ConnectionLabel)
{
    public string Title => $"{char.ToUpperInvariant(A.Role[0])}{A.Role[1..]} · {A.RequestedModel} on {ConnectionLabel}";
    public string Status => A.Status switch
    {
        "leased" => "Starting", "sending" => "Waiting for the model", "streaming" => "Answering", "completed" => "Finished", "failed" => "Failed", "interrupted" => "Interrupted",
        "stopped" => "Stopped at a limit", "cancelled" => "Cancelled", "paused" => "Paused", "abandoned" => "Abandoned (never sent)", "needs_reconciliation" => "Needs your check", _ => A.Status,
    } + (A.Revision is { } r && A.Role == "builder" ? $" · wrote revision {r}" : A.Revision is { } rv && A.Role == "reviewer" ? $" · reviewed revision {rv}" : "");
    public string Route => A.ReportedModel == null && A.Status is "leased" or "sending" ? $"Requested {A.RequestedModel}."
        : $"Requested {A.RequestedModel}; answered by {A.ReportedModel ?? "unknown"}" + (A.Provider == "openrouter" ? $" via {A.ReportedProvider ?? "an unknown provider"}" : "") + "." + (A.RouteNote is { } n ? " " + n : "");
    public string Usage => (A.InputTokens is { } i ? $"{i} tokens in, {A.OutputTokens} out" : "Usage not reported") + " · "
        + (A.Cost is { } c ? (c == 0 ? "no cost (local model)" : $"cost {c:0.000000} {A.Currency}") : A.Estimate is { } e ? $"estimated {e:0.000000} {A.Currency}, actual unknown" : "cost unknown")
        + $" · {A.Steps} step{(A.Steps == 1 ? "" : "s")}" + (A.ToolCalls > 0 ? $", {A.ToolCalls} tool call{(A.ToolCalls == 1 ? "" : "s")}" : "");
    public string Reason => A.StopReason ?? "";
    public bool HasReason => !string.IsNullOrEmpty(A.StopReason) && A.StopReason != "Finished.";
    public string Text => A.PartialText;
    public bool HasText => A.PartialText.Length > 0;
    public string Packet => A.PacketText;
    public bool Active => A.Active;
    public bool CanResume => A.Status is "paused" or "failed" or "interrupted" or "stopped" or "cancelled" or "abandoned" or "needs_reconciliation";
    /// <summary>True when a new request could be charged again: the owner must choose that explicitly.</summary>
    public bool NeedsCostChoice => A.Status is "interrupted" or "needs_reconciliation" || (A.Status is "paused" or "cancelled" && A.SentAt != null && A.Provider != "ollama");
    public bool CanResumeFree => CanResume && !NeedsCostChoice;
    public string AutomationName => $"{Title}, {Status}";
    public override string ToString() => AutomationName;
}

/// <summary>Connections, the team, dispatch with preview, run controls, and instructions to one role or everyone.</summary>
public sealed partial class HubViewModel
{
    public bool ConnectionsVisible => ListVisible && center == "connections";
    public bool TeamVisible => ListVisible && center == "team";
    public WorkspaceCommand ShowConnectionsCommand { get; private set; } = null!;
    public WorkspaceCommand ShowTeamCommand { get; private set; } = null!;
    internal bool LoadingTeam { get; private set; }

    private TeamView? team;
    public ObservableCollection<ConnectionRow> ConnectionRows { get; } = [];
    public bool ConnectionsEmpty => ConnectionRows.Count == 0;
    public ObservableCollection<RoleRow> RoleRows { get; } = [];
    private IReadOnlyList<ModelChoice> allChoices = [];

    // --- Connections ------------------------------------------------------------------------------------------
    public int NewProviderIndex { get => newProvider; set { newProvider = value; NewEndpoint = value == 0 ? "http://localhost:11434" : "https://openrouter.ai/api/v1"; Notify(); Notify(nameof(NewEndpoint)); } }
    private int newProvider;
    public string NewEndpoint { get; set; } = "http://localhost:11434";
    public string NewLabel { get; set; } = "";
    public WorkspaceCommand AddConnectionCommand { get; private set; } = null!;
    public WorkspaceCommand ProbeCommand { get; private set; } = null!;
    public WorkspaceCommand DeleteKeyCommand { get; private set; } = null!;
    public WorkspaceCommand RemoveConnectionCommand { get; private set; } = null!;
    /// <summary>Supplied by the window: asks for a key with a masked field and returns it, or null when cancelled.</summary>
    public Func<string, Task<string?>>? AskForKey { get; set; }
    public WorkspaceCommand EnterKeyCommand { get; private set; } = null!;

    // The test prompt for a local model.
    public IReadOnlyList<ModelChoice> LocalChoices => [.. allChoices.Where(c => !c.IsCloud && c.Available)];
    public ModelChoice? SmokeModel { get; set; }
    public string SmokePrompt { get; set; } = "In one sentence, what is a release note?";
    private string? smokeAttempt;
    public string SmokeText { get; private set; } = "";
    public string SmokeStatus { get; private set; } = "";
    public bool SmokeRunning { get; private set; }
    public WorkspaceCommand SmokeCommand { get; private set; } = null!;
    public WorkspaceCommand StopSmokeCommand { get; private set; } = null!;

    // --- Team -------------------------------------------------------------------------------------------------
    public bool TeamLocalOnly { get; set; } = true;
    public string TeamBudget { get; set; } = "";
    public string TeamCurrency { get; set; } = "USD";
    public string TeamMaxSteps { get; set; } = "4";
    public string TeamMaxSeconds { get; set; } = "180";
    public string TeamMaxToolCalls { get; set; } = "3";
    public string TeamMaxOutput { get; set; } = "600";
    public string TeamOnlyProviders { get; set; } = "";
    public bool TeamAutoReview { get; set; } = true;
    public string BudgetLine => team?.Budget is { } b
        ? (b.Limit is { } l ? $"Spent {b.Spent:0.0000} and reserved {b.Reserved:0.0000} of {l:0.0000} {b.Currency}" : $"No spending limit: paid models cannot be used. Spent {b.Spent:0.0000} {b.Currency}")
          + (b.UnknownUsage > 0 ? $" · {b.UnknownUsage} attempt{(b.UnknownUsage == 1 ? "" : "s")} with unknown usage" : "") + "."
        : "";
    public IReadOnlyList<string> PriceLines => team == null ? [] : [.. team.Prices.Select(p =>
        $"{p.ModelId}: {p.InputPerMTok} in / {p.OutputPerMTok} out {p.Currency} per million tokens · {p.Source} · from {p.EffectiveAt[..10]}")];
    public IReadOnlyList<ModelChoice> CloudChoices => [.. allChoices.Where(c => c.IsCloud)];
    public ModelChoice? PriceModel { get; set; }
    public string PriceInput { get; set; } = "";
    public string PriceOutput { get; set; } = "";
    public string PriceSource { get; set; } = "";
    public WorkspaceCommand SaveTeamCommand { get; private set; } = null!;
    public WorkspaceCommand SavePriceCommand { get; private set; } = null!;

    // --- Runs for the selected task ---------------------------------------------------------------------------
    public ObservableCollection<AttemptRow> AttemptRows { get; } = [];
    public bool HasAttempts => AttemptRows.Count > 0;
    private DispatchPreview? preview;
    /// <summary>Raised when a dispatch preview opens, so the window can bring it into view and put focus on its decision.</summary>
    public event Action? PreviewShown;
    public bool HasPreview => preview != null;
    public string PreviewTitle => preview is { } p ? $"Send to the {p.Role}: {p.Model ?? "no model"}" + (p.ConnectionLabel != null ? $" on {p.ConnectionLabel} ({(p.IsCloud ? "cloud" : "this computer")})" : "") : "";
    public string PreviewDetails => preview is { } p
        ? $"About {p.PacketTokens} tokens in, up to {p.MaxOutputTokens} out. Files: {(p.FileNames.Count == 0 ? "none" : string.Join(", ", p.FileNames))}. "
          + $"Price: {p.PriceStatus}" + (p.Estimate is { } e ? $", estimated {e:0.000000} {p.Currency}" : "") + ". "
          + (p.IsCloud ? p.ConsentStatus switch { "granted" => "Consent given for this destination and these files.", "required" => "This destination needs your consent.", "files_not_covered" => "Consent does not cover every file.", "blocked_local_only" => "Blocked: the project is local-only.", _ => "" } : "Nothing leaves this computer.")
        : "";
    public string PreviewBlocker => preview?.Blocker ?? "";
    public bool HasPreviewBlocker => preview?.Blocker != null;
    public string PreviewPacket => preview?.PacketText ?? "";
    public bool PreviewNeedsConsent => preview is { ConsentStatus: "required" or "files_not_covered" };
    public bool PreviewPriceRisk => preview is { PriceStatus: "unknown" or "stale" } && preview.Blocker != null && preview.Blocker.Contains("price");
    public bool CanSendPreview => preview is { Blocker: null } || PreviewPriceRisk;
    public WorkspaceCommand PreviewBuilderCommand { get; private set; } = null!;
    public WorkspaceCommand PreviewReviewerCommand { get; private set; } = null!;
    public WorkspaceCommand SendPreviewCommand { get; private set; } = null!;
    public WorkspaceCommand ConsentAndSendCommand { get; private set; } = null!;
    public WorkspaceCommand ClosePreviewCommand { get; private set; } = null!;
    public WorkspaceCommand PauseAttemptCommand { get; private set; } = null!;
    public WorkspaceCommand CancelAttemptCommand { get; private set; } = null!;
    public WorkspaceCommand ResumeAttemptCommand { get; private set; } = null!;
    public WorkspaceCommand RetryWithCostCommand { get; private set; } = null!;

    // --- Instructions from the channel ------------------------------------------------------------------------
    public int RecipientIndex { get; set; }
    private static readonly string[][] RecipientSets = [["builder"], ["reviewer"], ["planner"], ["everyone"]];
    public string InstructionText { get; set; } = "";
    public string InstructionPreview { get; private set; } = "";
    public bool HasInstructionPreview => InstructionPreview.Length > 0;
    public string InstructionNote => selectedTask == null ? "Select a task on the board to address its team." : $"About “{selectedTask.Title}”.";
    public WorkspaceCommand PreviewInstructionCommand { get; private set; } = null!;
    public WorkspaceCommand SendInstructionCommand { get; private set; } = null!;

    private async Task<WorkerClient> Worker() => await workspaces!.Client();

    private void InitModels()
    {
        ShowConnectionsCommand = new(_ => IsWorkspace, _ => { SetCenter("connections"); return LoadTeamAsync(); });
        ShowTeamCommand = new(_ => IsWorkspace, _ => { SetCenter("team"); return LoadTeamAsync(); });
        AddConnectionCommand = new(_ => CanWrite, _ =>
        {
            string ws = path!, provider = newProvider == 0 ? "ollama" : "openrouter", endpoint = NewEndpoint.Trim(), label = NewLabel.Trim(), click = Guid.NewGuid().ToString("N");
            return Fail(() => Submit($"conn|{ws}|{click}", ws, async id => await (await Worker()).AddConnectionAsync(ws, id, provider, endpoint, label)));
        });
        ProbeCommand = new(p => CanWrite && p is ConnectionRow, async p => await Guarded(async () => { await (await Worker()).ProbeConnectionAsync(path!, ((ConnectionRow)p!).Info.Id); await LoadTeamAsync(); }));
        EnterKeyCommand = new(p => CanWrite && p is ConnectionRow { NeedsKey: true } && AskForKey != null, async p =>
        {
            var row = (ConnectionRow)p!;
            // The key is handed to the worker once and not kept here: no field, no draft, no notice ever holds it.
            if (await AskForKey!($"Paste the API key for {row.Info.Label}. It goes to the operating system's credential store; Agent Hub keeps only a reference.") is not { Length: > 0 } key) return;
            string ws = path!, conn = row.Info.Id;
            await Guarded(async () => { await (await Worker()).SetCredentialAsync(ws, Guid.NewGuid().ToString("N"), conn, key.Trim()); await LoadTeamAsync(); });
        });
        DeleteKeyCommand = new(p => CanWrite && p is ConnectionRow { NeedsKey: true, Info.CredentialStatus: "stored" or "rotated" }, async p =>
            await Guarded(async () => { await (await Worker()).SetCredentialAsync(path!, Guid.NewGuid().ToString("N"), ((ConnectionRow)p!).Info.Id, null); await LoadTeamAsync(); }));
        RemoveConnectionCommand = new(p => CanWrite && p is ConnectionRow, async p =>
            await Guarded(async () => { await (await Worker()).RemoveConnectionAsync(path!, Guid.NewGuid().ToString("N"), ((ConnectionRow)p!).Info.Id); await LoadTeamAsync(); }));
        SmokeCommand = new(_ => CanWrite && SmokeModel != null && !SmokeRunning, async _ => await Guarded(async () =>
        {
            var r = await (await Worker()).SmokeTestAsync(path!, Guid.NewGuid().ToString("N"), SmokeModel!.ConnectionId, SmokeModel.ModelId, SmokePrompt);
            smokeAttempt = r.Id;
            SmokeRunning = true;
            SmokeText = "";
            SmokeStatus = "Sending…";
            NotifySmoke();
        }));
        StopSmokeCommand = new(_ => SmokeRunning, async _ => await Guarded(async () => { await (await Worker()).CancelAttemptAsync(path!, Guid.NewGuid().ToString("N"), smokeAttempt!); await RefreshRunsAsync(); }));

        SaveTeamCommand = new(_ => CanWrite && selected != null, _ =>
        {
            if (!double.TryParse(TeamBudget, out double b) && TeamBudget.Trim().Length > 0) { ShowNotice("The budget must be a number, or empty for no paid models.", "Dismiss", DismissNoticeCommand); return Task.CompletedTask; }
            if (!int.TryParse(TeamMaxSteps, out int s) || !int.TryParse(TeamMaxSeconds, out int t) || !int.TryParse(TeamMaxToolCalls, out int k) || !int.TryParse(TeamMaxOutput, out int o))
            { ShowNotice("Each limit must be a whole number.", "Dismiss", DismissNoticeCommand); return Task.CompletedTask; }
            var only = TeamOnlyProviders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var settings = new ProjectSettings(selected!.Id, TeamLocalOnly, TeamBudget.Trim().Length == 0 ? null : b, TeamCurrency.Trim().ToUpperInvariant(), s, t, k, o, only.Length == 0 ? null : only, TeamAutoReview);
            string ws = path!, click = Guid.NewGuid().ToString("N");
            return Fail(() => Submit($"settings|{ws}|{click}", ws, async id => await (await Worker()).SetProjectSettingsAsync(ws, id, settings)));
        });
        SavePriceCommand = new(_ => CanWrite && PriceModel != null, _ =>
        {
            if (!double.TryParse(PriceInput, out double i) || !double.TryParse(PriceOutput, out double o)) { ShowNotice("Enter input and output prices per million tokens as numbers.", "Dismiss", DismissNoticeCommand); return Task.CompletedTask; }
            string ws = path!, click = Guid.NewGuid().ToString("N");
            var m = PriceModel!;
            string source = PriceSource.Trim().Length == 0 ? "entered by the owner" : PriceSource.Trim();
            return Fail(() => Submit($"price|{ws}|{click}", ws, async id => await (await Worker()).SetPriceAsync(ws, id, m.ConnectionId, m.ModelId, TeamCurrency.Trim().ToUpperInvariant(), i, o, source, DateTime.UtcNow)));
        });

        PreviewBuilderCommand = new(_ => CanEdit, _ => LoadPreviewAsync("builder"));
        PreviewReviewerCommand = new(_ => CanEdit, _ => LoadPreviewAsync("reviewer"));
        ClosePreviewCommand = new(_ => HasPreview, _ => { preview = null; NotifyPreview(); return Task.CompletedTask; });
        SendPreviewCommand = new(_ => CanEdit && CanSendPreview, _ => SendPreview(false));
        ConsentAndSendCommand = new(_ => CanEdit && PreviewNeedsConsent, _ => SendPreview(true));
        PauseAttemptCommand = new(p => CanWrite && p is AttemptRow { Active: true }, p => Control("pause", (AttemptRow)p!, false));
        CancelAttemptCommand = new(p => CanWrite && p is AttemptRow { Active: true }, p => Control("cancel", (AttemptRow)p!, false));
        ResumeAttemptCommand = new(p => CanWrite && p is AttemptRow { CanResumeFree: true }, p => Control("resume", (AttemptRow)p!, false));
        RetryWithCostCommand = new(p => CanWrite && p is AttemptRow { CanResume: true, NeedsCostChoice: true }, p => Control("resume", (AttemptRow)p!, true));

        PreviewInstructionCommand = new(_ => IsWorkspace && selectedTask != null && InstructionText.Trim().Length > 0, async _ => await Guarded(async () =>
        {
            var previews = await (await Worker()).PreviewInstructionAsync(path!, selectedTask!.Id, RecipientSets[RecipientIndex], InstructionText.Trim());
            InstructionPreview = string.Join("\n", previews.Select(p => $"{p.Role}: {p.Model} on {p.ConnectionLabel} ({(p.IsCloud ? "cloud" : "this computer")}), about {p.PacketTokens} tokens"
                + (p.Estimate is { } e ? $", estimated {e:0.000000} {p.Currency}" : ", price unknown") + (p.Blocker is { } b ? $" — blocked: {b}" : "")));
            Notify(nameof(InstructionPreview)); Notify(nameof(HasInstructionPreview));
        }));
        SendInstructionCommand = new(_ => CanWrite && selectedTask != null && InstructionText.Trim().Length > 0, _ =>
        {
            string ws = path!, task = selectedTask!.Id, text = InstructionText.Trim();
            var roles = RecipientSets[RecipientIndex];
            string click = Guid.NewGuid().ToString("N"); // a retry of this send reuses its ID, so no recipient is dispatched twice
            return Fail(async () =>
            {
                string? error = await Submit($"instruction|{ws}|{task}|{click}", ws, async id =>
                {
                    var r = await (await Worker()).SendInstructionAsync(ws, id, task, roles, text);
                    var failed = r.Attempts.Where(x => x.Error != null).ToList();
                    if (failed.Count > 0) ShowNotice(string.Join(" ", failed.Select(f => $"Not sent to the {f.Role}: {f.Error}")), "Dismiss", DismissNoticeCommand);
                    return r.Instruction;
                });
                if (error == null) { InstructionText = ""; InstructionPreview = ""; Notify(nameof(InstructionText)); Notify(nameof(InstructionPreview)); Notify(nameof(HasInstructionPreview)); }
                return error;
            });
        });

        commands = [.. commands, ShowConnectionsCommand, ShowTeamCommand, AddConnectionCommand, ProbeCommand, EnterKeyCommand, DeleteKeyCommand, RemoveConnectionCommand, SmokeCommand, StopSmokeCommand,
            SaveTeamCommand, SavePriceCommand, PreviewBuilderCommand, PreviewReviewerCommand, ClosePreviewCommand, SendPreviewCommand, ConsentAndSendCommand,
            PauseAttemptCommand, CancelAttemptCommand, ResumeAttemptCommand, RetryWithCostCommand, PreviewInstructionCommand, SendInstructionCommand];
    }

    /// <summary>Runs a worker call and turns a refusal into a notice the owner can act on.</summary>
    private async Task Guarded(Func<Task> work)
    {
        try { await work(); }
        catch (WorkerException e) { ShowNotice(e.Message, "Dismiss", DismissNoticeCommand); }
    }

    private async Task LoadPreviewAsync(string role)
    {
        string ws = path!, task = selectedTask!.Id;
        await Guarded(async () =>
        {
            var p = await (await Worker()).PreviewDispatchAsync(ws, task, role);
            if (path == ws && selectedTask?.Id == task) { preview = p; NotifyPreview(); PreviewShown?.Invoke(); }
        });
    }

    private Task SendPreview(bool withConsent)
    {
        var p = preview!;
        string ws = path!, click = Guid.NewGuid().ToString("N");
        bool priceRisk = PreviewPriceRisk;
        return Fail(async () =>
        {
            if (withConsent)
            {
                // Consent covers exactly this destination and the files this packet admitted, nothing more.
                try { await (await Worker()).GrantConsentAsync(ws, Guid.NewGuid().ToString("N"), p.ProjectId, p.ConnectionId!, p.Model!, [.. p.FileNames]); }
                catch (WorkerException e) { return e.Message; }
            }
            string? error = await Submit($"dispatch|{ws}|{p.TaskId}|{p.Role}|{click}", ws, async id => await (await Worker()).DispatchAsync(ws, id, p.TaskId, p.Role, acceptPriceRisk: priceRisk));
            if (error == null) { preview = null; NotifyPreview(); await RefreshRunsAsync(); }
            return error;
        });
    }

    private Task Control(string what, AttemptRow row, bool acceptCost)
    {
        string ws = path!, id = row.A.Id, click = Guid.NewGuid().ToString("N");
        return Fail(async () =>
        {
            string? error = await Submit($"{what}|{ws}|{id}|{click}", ws, async cmd => what switch
            {
                "pause" => await (await Worker()).PauseAttemptAsync(ws, cmd, id),
                "cancel" => await (await Worker()).CancelAttemptAsync(ws, cmd, id),
                _ => await (await Worker()).ResumeAttemptAsync(ws, cmd, id, acceptCost),
            });
            await RefreshRunsAsync();
            return error;
        });
    }

    /// <summary>Reads connections, roles, settings and prices for the project on screen.</summary>
    private int teamVersion;

    internal async Task LoadTeamAsync()
    {
        if (!IsWorkspace || path is not string file || selected is not { } project) return;
        int v = ++teamVersion;
        TeamView view;
        try { view = await Task.Run(() => Team.Read(file, project.Id)); }
        catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { return; }
        // A read that started earlier never overwrites a newer one.
        if (v != teamVersion || path != file || selected?.Id != project.Id) return;
        team = view;
        LoadingTeam = true;
        try
        {
            ConnectionRows.Clear();
            foreach (var c in view.Connections) ConnectionRows.Add(new(c));
            allChoices = [.. view.Connections.SelectMany(c => c.Models.Select(m => new ModelChoice(c.Id, c.Label, c.Provider, c.IsCloud, m.Id, m.Capabilities, m.Available)))];
            if (RoleRows.Count == 0) foreach (var r in Team.Roles) RoleRows.Add(new(r, this));
            foreach (var row in RoleRows)
            {
                var a = view.Assignments.FirstOrDefault(x => x.Role == row.Role);
                var choice = a == null ? null : allChoices.FirstOrDefault(c => c.ConnectionId == a.ConnectionId && c.ModelId == a.ModelId);
                row.Choices = [.. allChoices.Where(c => c.Available || c == choice)];
                row.Set(choice, Explain(row.Role, choice, view));
            }
            var s = view.Settings;
            (TeamLocalOnly, TeamBudget, TeamCurrency, TeamMaxSteps, TeamMaxSeconds, TeamMaxToolCalls, TeamMaxOutput, TeamOnlyProviders, TeamAutoReview) =
                (s.LocalOnly, s.BudgetLimit?.ToString() ?? "", s.Currency, s.MaxSteps.ToString(), s.MaxSeconds.ToString(), s.MaxToolCalls.ToString(), s.MaxOutputTokens.ToString(), string.Join(", ", s.OnlyProviders ?? []), s.AutoReview);
            SmokeModel ??= LocalChoices.FirstOrDefault();
        }
        finally { LoadingTeam = false; }
        foreach (var n in new[] { nameof(ConnectionsEmpty), nameof(BudgetLine), nameof(PriceLines), nameof(CloudChoices), nameof(LocalChoices), nameof(SmokeModel), nameof(TeamLocalOnly), nameof(TeamBudget),
                                  nameof(TeamCurrency), nameof(TeamMaxSteps), nameof(TeamMaxSeconds), nameof(TeamMaxToolCalls), nameof(TeamMaxOutput), nameof(TeamOnlyProviders), nameof(TeamAutoReview) }) Notify(n);
        RaiseCommands();
    }

    private static string Explain(string role, ModelChoice? c, TeamView view)
    {
        if (c == null) return $"No model. The {role} can only be played by the offline fixture.";
        var parts = new List<string> { $"Provider: {(c.Provider == "ollama" ? "Ollama" : "OpenRouter")} ({c.ConnectionLabel}). Model: {c.ModelId}.", $"Capabilities: {c.Capabilities.Describe()}." };
        if (!c.Available) parts.Add("This model was not listed at the last probe.");
        if (c.Capabilities.Text == null) parts.Add("Text output is not confirmed by the provider.");
        if (c.IsCloud) parts.Add(view.Consents.Any(k => k.ConnectionId == c.ConnectionId && k.ModelId == c.ModelId) ? "Cloud: consent given." : "Cloud: needs consent before use.");
        if (role == "reviewer" && view.Assignments.FirstOrDefault(a => a.Role == "builder") is { } b && b.ModelId == c.ModelId)
            parts.Add("Same model as the builder, so a builder's revision will not start this reviewer on its own.");
        return string.Join(" ", parts);
    }

    /// <summary>Called by a role row when the owner picks a model. A cloud model without consent asks for it first.</summary>
    internal async Task AssignAsync(RoleRow row, ModelChoice choice)
    {
        string ws = path!, project = selected!.Id;
        await Guarded(async () =>
        {
            var w = await Worker();
            try { await w.AssignRoleAsync(ws, Guid.NewGuid().ToString("N"), project, row.Role, choice.ConnectionId, choice.ModelId); }
            catch (WorkerException e) when (e.Error.Code == "consent_required")
            {
                ShowNotice($"{e.Error.Message} Use “Send to the {row.Role}…” on a task to preview the exact packet and give consent, or allow it here without files.", "Allow without files",
                    new(_ => true, async _ => await Guarded(async () =>
                    {
                        await w.GrantConsentAsync(ws, Guid.NewGuid().ToString("N"), project, choice.ConnectionId, choice.ModelId, []);
                        await w.AssignRoleAsync(ws, Guid.NewGuid().ToString("N"), project, row.Role, choice.ConnectionId, choice.ModelId);
                        ClearNotice();
                        await LoadTeamAsync();
                    })));
            }
            await LoadTeamAsync();
        });
    }

    /// <summary>Reads the selected task's attempts and the test prompt's progress. Cheap; polling calls it while anything is running.</summary>
    internal async Task RefreshRunsAsync()
    {
        if (!IsWorkspace || path is not string file) return;
        var task = selectedTask;
        IReadOnlyList<AttemptView> rows = [];
        AttemptView? smoke = null;
        Dictionary<string, string> labels = [];
        try
        {
            (rows, smoke, labels) = await Task.Run(() =>
            {
                var list = task == null ? (IReadOnlyList<AttemptView>)[] : RunEngine.ForTask(file, task.Id);
                var s = smokeAttempt == null ? null : RunEngine.Get(file, smokeAttempt);
                var names = Team.Read(file, selected?.Id ?? "").Connections.ToDictionary(c => c.Id, c => c.Label);
                return (list, s, names);
            });
        }
        catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { return; }
        if (path != file || selectedTask != task) return;
        // Rows are replaced only when something changed, so a reader's place in the list holds.
        var fresh = rows.Select(a => new AttemptRow(a, labels.GetValueOrDefault(a.ConnectionId, a.ConnectionId))).ToList();
        // An attempt the owner was watching has just finished: show what it produced. A builder's new revision becomes the one on screen.
        var ended = AttemptRows.Where(o => o.Active && fresh.Any(n => n.A.Id == o.A.Id && !n.Active)).Select(o => fresh.First(n => n.A.Id == o.A.Id)).ToList();
        if (ended.Count > 0)
        {
            if (ended.Any(e => e.A is { Role: "builder", Status: "completed" })) releasePlace = true;
            _ = OpenAsync(file, workspaceName);
        }
        if (!fresh.SequenceEqual(AttemptRows))
        {
            AttemptRows.Clear();
            foreach (var r in fresh.AsEnumerable().Reverse()) AttemptRows.Add(r);
        }
        if (smoke != null)
        {
            SmokeText = smoke.PartialText;
            SmokeRunning = smoke.Active;
            SmokeStatus = smoke.Active ? "Answering…" : smoke.Status == "completed" ? $"Finished · {smoke.OutputTokens} tokens · answered by {smoke.ReportedModel}" : $"{smoke.Status}: {smoke.StopReason}";
            NotifySmoke();
        }
        Notify(nameof(HasAttempts));
        RaiseCommands();
    }

    internal bool AnyRunning => AttemptRows.Any(r => r.Active) || SmokeRunning;

    private void NotifySmoke() { foreach (var n in new[] { nameof(SmokeText), nameof(SmokeStatus), nameof(SmokeRunning) }) Notify(n); RaiseCommands(); }

    private void NotifyPreview()
    {
        foreach (var n in new[] { nameof(HasPreview), nameof(PreviewTitle), nameof(PreviewDetails), nameof(PreviewBlocker), nameof(HasPreviewBlocker), nameof(PreviewPacket), nameof(PreviewNeedsConsent), nameof(PreviewPriceRisk), nameof(CanSendPreview) }) Notify(n);
        RaiseCommands();
    }

    public void ClearRunsForSelection()
    {
        preview = null;
        AttemptRows.Clear();
        NotifyPreview();
        Notify(nameof(HasAttempts));
        Notify(nameof(InstructionNote));
    }
}
