using System.Collections.ObjectModel;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

public sealed record CodeTaskRow(string Id, string Label);

/// <summary>
/// The owner's code-change workflow (Day 7): assign a task to an isolated runtime in a disposable repository, read the exact patch and its check result, ask for a revision, approve that
/// revision, apply it once, and commit locally as a separate step. The ledger lives in the workspace folder (<c>code/</c>) so a restart recovers every receipt.
/// </summary>
public sealed partial class HubViewModel
{
    private CodeWork? code;
    private string? codeFor;
    private CancellationTokenSource? codeCts;
    private string codeRepo = "", codeTitle = "", codePrompt = "", codeRevisionText = "", codeCheckReg = "", claudePath = "", codexPath = "";
    private CodeTaskRow? selectedCode;
    private IReadOnlyList<CodeWork.TaskSummary> codeSummaries = [];
    public bool CodeVisible => ListVisible && center == "code";
    public string CodeRepo { get => codeRepo; set { codeRepo = value ?? ""; RaiseCommands(); } }
    public string CodeTitle { get => codeTitle; set { codeTitle = value ?? ""; RaiseCommands(); } }
    public string CodePrompt { get => codePrompt; set { codePrompt = value ?? ""; RaiseCommands(); } }
    public string CodeRevisionText { get => codeRevisionText; set { codeRevisionText = value ?? ""; RaiseCommands(); } }
    public string CodeCheckReg { get => codeCheckReg; set { codeCheckReg = value ?? ""; RaiseCommands(); } }
    private readonly Dictionary<string, (string Path, RuntimeInfo Info)> discovered = [];
    public string ClaudePath { get => claudePath; set { if (claudePath == (value ?? "")) return; claudePath = value ?? ""; PathChanged("claude"); } }
    public string CodexPath { get => codexPath; set { if (codexPath == (value ?? "")) return; codexPath = value ?? ""; PathChanged("codex"); } }
    private void PathChanged(string name)
    {
        discovered.Remove(name);
        const string stale = "The executable path changed, so the earlier description no longer applies. Select Discover runtimes to check this one. Assign and Request revision also check it before starting anything.";
        if (name == "claude") { ClaudeStatus = stale; Notify(nameof(ClaudeStatus)); } else { CodexStatus = stale; Notify(nameof(CodexStatus)); }
        NotifyNextRun();
    }
    private void NotifyNextRun() { Notify(nameof(NextRunText)); RaiseCommands(); }
    /// <summary>Which runtime the next assign or revision will start, as last checked for the paths now in the boxes.</summary>
    public string NextRunText
    {
        get
        {
            string name = codeRuntimePick == 1 ? "codex" : "claude", label = name == "codex" ? "Codex" : "Claude Code";
            if (!discovered.TryGetValue(name, out var d)) return $"Next run: {label}. Not checked for the path shown. Assign will check it first, and refuse if it is not ready.";
            var i = d.Info;
            return i.IsFixture ? $"Next run: FIXTURE {name}: a scripted test program ({i.Version}). No model is used and nothing is charged."
                 : i.Dispatchable ? $"Next run: {label} {i.Version}, in the isolation boundary. Charged to: {i.Billing}."
                 : $"Next run: {label} {i.Version} cannot start: {i.DispatchState}.";
        }
    }
    private readonly Dictionary<string, string> codeStatusBy = [];
    private string codeNotice = "Nothing assigned in this session.";
    public string FooterText => CodeVisible ? CodeFooter : StatusText;
    private string CodeFooter
    {
        get
        {
            if (codeSummaries.Count == 0) return "No code tasks in this workspace yet.";
            int done = codeSummaries.Count(x => x.Integrated);
            return $"{codeSummaries.Count} code task(s), {done} integrated" + (SelectedSummary is { } s ? $" · selected: {s.Title}: {TaskLine(s)}" : "");
        }
    }
    private static string Who(CodeWork.TaskSummary s) => s.Fixture ? $"FIXTURE {s.Runtime}" : s.Runtime;
    private static string TaskLine(CodeWork.TaskSummary s) => $"{(s.Fixture ? "FIXTURE " : "")}{s.LastRun}" + (s.Integrated ? ", integrated" : s.ApprovedForRevision ? ", approved, not applied" : "");
    public string CodeIdentityText => SelectedSummary is { } s
        ? (s.Identity.Length == 0 ? "This task has not run yet." : s.Fixture ? $"Ran on a FIXTURE: {s.Identity}. A scripted test program, not {(s.Runtime == "codex" ? "Codex" : "Claude Code")}." : $"Ran on {(s.Runtime == "codex" ? "Codex" : "Claude Code")} {s.Identity}.")
        : "";
    private int codeRuntimePick, codeCheckPick;
    public int CodeRuntimePick { get => codeRuntimePick; set { codeRuntimePick = value; NotifyNextRun(); } }
    public int CodeCheckPick { get => codeCheckPick; set { codeCheckPick = value; RaiseCommands(); } }
    public ObservableCollection<CodeTaskRow> CodeTasks { get; } = [];
    public ObservableCollection<string> CodeChecks { get; } = [];
    public CodeTaskRow? SelectedCodeTask { get => selectedCode; set { selectedCode = value; Notify(nameof(SelectedCodeTask)); Notify(nameof(CodeStatus)); Notify(nameof(CodeIdentityText)); Notify(nameof(FooterText)); RaiseCommands(); _ = LoadCodeDetailAsync(); } }
    public bool CodeRunning { get; private set; }
    public string CodeStatus => codeStatusBy.TryGetValue(selectedCode?.Id ?? "", out var t) ? t : SelectedSummary is { } s ? $"{s.Title}: {TaskLine(s)}. {s.ApprovalNote}" : codeNotice;
    public string ClaudeStatus { get; private set; } = "Not checked yet. Choose Discover.";
    public string CodexStatus { get; private set; } = "Not checked yet. Choose Discover.";
    public string CodePatchText { get; private set; } = "";
    public string CodeCheckText { get; private set; } = "";
    public string CodePreviewText { get; private set; } = "";
    public string CodeApprovalText { get; private set; } = "";
    public string CodeLedgerText { get; private set; } = "";
    public WorkspaceCommand ShowCodeCommand { get; private set; } = null!;
    public WorkspaceCommand DiscoverRuntimesCommand { get; private set; } = null!;
    public WorkspaceCommand RegisterCheckCommand { get; private set; } = null!;
    public WorkspaceCommand AssignCodeCommand { get; private set; } = null!;
    public WorkspaceCommand StopCodeCommand { get; private set; } = null!;
    public WorkspaceCommand RunCodeCheckCommand { get; private set; } = null!;
    public WorkspaceCommand ReviseCodeCommand { get; private set; } = null!;
    public WorkspaceCommand ApproveCodeCommand { get; private set; } = null!;
    public WorkspaceCommand ApplyCodeCommand { get; private set; } = null!;
    public WorkspaceCommand PublishCodeCommand { get; private set; } = null!;
    public WorkspaceCommand CancelCodeTaskCommand { get; private set; } = null!;
    public WorkspaceCommand RefreshCodeCommand { get; private set; } = null!;

    /// <summary>Feedback belongs to the task it is about; with none selected it is the pane's general notice.</summary>
    private void SetCode(string text, string? task = null)
    {
        string key = task ?? selectedCode?.Id ?? "";
        if (key.Length == 0) codeNotice = text; else codeStatusBy[key] = text;
        Notify(nameof(CodeStatus)); Notify(nameof(FooterText));
    }
    private CodeWork Ledger()
    {
        string ws = path ?? throw new InvalidOperationException("No workspace is open.");
        if (code == null || codeFor != ws) { code = new CodeWork(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ws)!, "code")); codeFor = ws; }
        return code;
    }
    private CodeWork.TaskSummary? SelectedSummary => selectedCode == null ? null : codeSummaries.FirstOrDefault(s => s.Id == selectedCode.Id);
    private bool CodeIdle => CanWrite && !CodeRunning;

    private async Task CodeGuard(Func<Task> work)
    {
        try { await work(); }
        catch (RuntimeRefused e) { SetCode(e.Message); ShowNotice(e.Message, "Dismiss", DismissNoticeCommand); }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { SetCode(e.Message); ShowNotice(e.Message, "Dismiss", DismissNoticeCommand); }
        finally { CodeRunning = false; Notify(nameof(CodeRunning)); RaiseCommands(); }
    }

    private CliRuntime? RuntimeFor(CodeWork w, string name)
    {
        string? chosen = name == "claude" ? ClaudePath.Trim() : CodexPath.Trim();
        var loc = CliRuntime.Locate(name, chosen);
        return loc == null ? null : new CliRuntime(name, loc.Value.Exe, loc.Value.Prefix, System.IO.Path.Combine(w.Root, "profiles"));
    }

    private string Describe(CodeWork w, string name, RuntimeInfo? i, string chosen)
    {
        string title = name == "codex" ? "Codex" : "Claude Code";
        if (i == null) return $"{title}\nNot found. {(chosen.Length > 0 ? "There is no file at the chosen path." : "It is not on PATH or in the known install locations; type the executable's path above.")}\nTo test the workflow without it, choose a fixture runtime: a scripted test program that uses no model.";
        string head = i.IsFixture ? $"FIXTURE {name}: a scripted test program, not {title}. No model is used and nothing is charged." : title;
        string found = i.Found ? (i.Identified ? $"found {i.Executable}, {i.Version}" : $"found {i.Executable}, not identified: {i.Detail}") : "not found";
        string caps = i.MissingCapabilities.Count > 0 ? $"; missing {string.Join(", ", i.MissingCapabilities)}" : i.Capabilities.Count > 0 ? "; supports " + string.Join(", ", i.Capabilities) : "";
        string text = $"{head}\nDiscovery: {found}{caps}\nSign-in (Agent Hub's private profile only): {i.SignIn}" + (i.SignInDetail.Length > 0 ? $" · {i.SignInDetail}" : "") + $"\nIsolation: {i.Isolation}\nIsolated dispatch: {i.DispatchState}. Charged to: {i.Billing}";
        if (!i.IsFixture && i.Available && !i.SignedIn && i.Isolation == "enforced")
        {
            string home = System.IO.Path.Combine(w.Root, "profiles", name, "home", name == "codex" ? ".codex" : ".claude");
            string cmd = name == "codex" ? $"$env:CODEX_HOME='{home}'; & '{i.Executable}' login" : $"$env:CLAUDE_CONFIG_DIR='{home}'; & '{i.Executable}' auth login";
            text += $"\nNext step: this is signed out, so nothing can run on it yet. Sign in to Agent Hub's private profile (your own {title} sign-in is not used or changed) by running this in PowerShell, then select Discover runtimes again:\n{cmd}\nOr, to test the workflow without signing in, choose a fixture runtime above.";
        }
        return text;
    }

    private async Task RefreshCodeAsync(bool announce = false)
    {
        var w = Ledger();
        (IReadOnlyList<CodeWork.TaskSummary> list, IReadOnlyList<CodeWork.RegisteredCheck> checks, IReadOnlyList<string> lost) = await Task.Run(() =>
        {
            var lost = w.Reconcile(false);
            return (w.Summaries(), w.Checks(), lost);
        });
        codeSummaries = list;
        if (ClaudePath.Length == 0) { ClaudePath = w.Setting("runtime.claude") ?? ""; Notify(nameof(ClaudePath)); }
        if (CodexPath.Length == 0) { CodexPath = w.Setting("runtime.codex") ?? ""; Notify(nameof(CodexPath)); }
        string keep = selectedCode?.Id ?? "";
        CodeTasks.Clear();
        foreach (var s in list) CodeTasks.Add(new(s.Id, $"{s.Title} · {Who(s)} · {s.State} · {(s.Fixture ? "FIXTURE " : "")}{s.LastRun} · {s.Check}" + (s.ApprovedForRevision ? " · approved" : "") + (s.Receipt.Length > 0 ? " · " + s.Receipt : "")));
        CodeChecks.Clear(); foreach (var c in checks) CodeChecks.Add($"{c.Name}: {c.Display}");
        if (codeCheckPick >= CodeChecks.Count) { codeCheckPick = 0; Notify(nameof(CodeCheckPick)); }
        selectedCode = CodeTasks.FirstOrDefault(t => t.Id == keep) ?? CodeTasks.LastOrDefault(); Notify(nameof(SelectedCodeTask));
        if (lost.Count > 0) SetCode("After the restart: " + string.Join(", ", lost.Select(l => l.Replace("lost:", "task folder is gone: "))), ""); else if (announce && list.Count > 0) SetCode($"{list.Count} task(s) recovered from this workspace's code ledger.", "");
        Notify(nameof(CodeStatus)); Notify(nameof(CodeIdentityText)); Notify(nameof(FooterText));
        RaiseCommands();
        await LoadCodeDetailAsync();
    }

    private async Task LoadCodeDetailAsync()
    {
        var s = SelectedSummary; var w = code;
        if (s == null || w == null) { CodePatchText = CodeCheckText = CodePreviewText = CodeApprovalText = CodeLedgerText = ""; NotifyCodeDetail(); return; }
        var d = await Task.Run(() =>
        {
            bool live = s.State == "active";
            PatchInfo? p = null; CheckResult? chk = null; IntegrationPreview? pv = null;
            try { if (live) { p = w.Patch(s.Id); chk = w.LatestCheck(s.Id); if (!s.Integrated) pv = w.Preview(s.Id, s.Repo); } } catch (InvalidOperationException) { }
            return (p, chk, pv, ev: w.Events(s.Id));
        });
        if (selectedCode?.Id != s.Id) return;
        CodePatchText = d.p == null ? "This task has no workspace now (cancelled, lost or erased). Its saved patch, if any, is in the code folder's abandoned list." : d.p.Files.Count == 0 ? "No changes yet." : $"Revision {d.p.Hash[..12]} · {d.p.Files.Count} file(s): {string.Join(", ", d.p.Files)}\n\n{(d.p.Text.Length > 20000 ? d.p.Text[..20000] + "\n… (cut at 20,000 characters)" : d.p.Text)}";
        CodeCheckText = d.chk == null ? "No check has run on this task." : $"{d.chk.Command} → exit {d.chk.Exit} on revision {d.chk.PatchHash[..12]}. {(d.chk.Fresh ? "Current for the patch shown." : "STALE: the patch changed after this ran, or the check itself changed the files. Run it again.")}\n{d.chk.Output.Trim()}";
        CodePreviewText = s.Integrated ? s.Receipt : d.pv == null ? "" : (d.pv.Applies ? "Applies cleanly to the repository as it is now." : "Does not apply cleanly: " + d.pv.Conflict) + (d.pv.OwnerChanges.Count > 0 ? $"\nYour own uncommitted changes there ({d.pv.OwnerChanges.Count}) stay untouched: {string.Join(", ", d.pv.OwnerChanges)}." : "") + (d.pv.Overlap.Count > 0 ? $"\nOverlap with this patch: {string.Join(", ", d.pv.Overlap)}." : "");
        var ap = d.ev.LastOrDefault(e => e.Kind.StartsWith("approval_") && e.Kind != "approval_requested");
        CodeApprovalText = s.ApprovalNote + (ap == null ? "" : $" Last approval {ap.Kind["approval_".Length..]}: {System.Text.Json.JsonDocument.Parse(ap.Data).RootElement.GetProperty("Action").GetString()}");
        CodeLedgerText = string.Join("\n", d.ev.TakeLast(14).Select(e => $"#{e.Seq} {e.Kind}"));
        NotifyCodeDetail();
    }

    private void NotifyCodeDetail() { foreach (var n in new[] { nameof(CodePatchText), nameof(CodeCheckText), nameof(CodePreviewText), nameof(CodeApprovalText), nameof(CodeLedgerText) }) Notify(n); }

    /// <summary>Records what a discovery found for the path now in the box, and rewrites that runtime's description to match.</summary>
    private void Remember(CodeWork w, string name, RuntimeInfo? i)
    {
        string chosen = (name == "claude" ? ClaudePath : CodexPath).Trim();
        if (i != null) discovered[name] = (chosen, i); else discovered.Remove(name);
        string text = Describe(w, name, i, chosen);
        if (name == "claude") { ClaudeStatus = text; Notify(nameof(ClaudeStatus)); } else { CodexStatus = text; Notify(nameof(CodexStatus)); }
        NotifyNextRun();
    }

    private bool TaskSelectedActive => CodeIdle && SelectedSummary is { State: "active" };

    private async Task RunRuntimeAsync(CodeWork w, CliRuntime rt, string id, string prompt)
    {
        codeCts = new(); CodeRunning = true; Notify(nameof(CodeRunning)); RaiseCommands();
        SetCode($"Running {rt.Name} on {id} inside the isolation boundary…", id);
        var rec = await rt.Run(w, id, prompt, codeCts.Token);
        SetCode($"{(rec.IsFixture ? "FIXTURE " : "")}{rt.Name}: {rec.Outcome}. Session {(rec.Session.Length > 0 ? rec.Session : "none reported")}, tools: {(rec.Tools.Count == 0 ? "none" : string.Join(", ", rec.Tools))}, {rec.Files.Count} changed file(s), revision {rec.Revision[..12]}." + (rec.Detail.Length > 0 ? " " + rec.Detail : ""));
    }

    private WorkspaceCommand[] InitDay7()
    {
        ShowCodeCommand = new(_ => IsWorkspace, async _ => { SetCenter("code"); Notify(nameof(CodeVisible)); await CodeGuard(() => RefreshCodeAsync(true)); });
        RefreshCodeCommand = new(_ => IsWorkspace, _ => CodeGuard(() => RefreshCodeAsync(true)));
        DiscoverRuntimesCommand = new(_ => IsWorkspace && !CodeRunning, _ => CodeGuard(async () =>
        {
            var w = Ledger();
            w.SetSetting("runtime.claude", ClaudePath.Trim()); w.SetSetting("runtime.codex", CodexPath.Trim());
            ClaudeStatus = "Checking inside the isolation boundary…"; CodexStatus = ClaudeStatus; Notify(nameof(ClaudeStatus)); Notify(nameof(CodexStatus));
            var (c, x) = await Task.Run(() => (RuntimeFor(w, "claude")?.Discover(true), RuntimeFor(w, "codex")?.Discover(true)));
            Remember(w, "claude", c); Remember(w, "codex", x);
        }));
        RegisterCheckCommand = new(_ => CanWrite && CodeCheckReg.Contains('=') && CodeCheckReg.Split('=', 2)[1].Trim().Length > 0, _ => CodeGuard(async () =>
        {
            var parts = CodeCheckReg.Split('=', 2); var words = parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            await Task.Run(() => Ledger().RegisterCheck(parts[0].Trim(), words[0], words.Skip(1)));
            SetCode($"Registered check '{parts[0].Trim()}': {words[0]} with {words.Length - 1} fixed argument(s). It is never run through a shell.");
            CodeCheckReg = ""; Notify(nameof(CodeCheckReg));
            await RefreshCodeAsync();
        }));
        AssignCodeCommand = new(_ => CodeIdle && CodeRepo.Trim().Length > 0 && CodeTitle.Trim().Length > 0 && CodePrompt.Trim().Length > 0, _ => CodeGuard(async () =>
        {
            var w = Ledger(); string repo = CodeRepo.Trim();
            string name = CodeRuntimePick == 1 ? "codex" : "claude";
            var rt = RuntimeFor(w, name) ?? throw new RuntimeRefused("not_found", $"{name} was not found. Type its executable path above and select Discover runtimes.");
            var info = await Task.Run(() => rt.Discover());
            Remember(w, name, info);
            if (!info.Dispatchable) throw new RuntimeRefused("not_dispatchable", $"{(info.IsFixture ? "The fixture" : name)} was not started: {info.DispatchState}." + (info.Available && !info.SignedIn && !info.IsFixture ? " Its sign-in is described under Runtimes." : ""));
            if (await Task.Run(() => CodeWork.Git(repo, ["rev-parse", "--show-toplevel"]).Exit) != 0) throw new InvalidOperationException("That folder is not a git repository.");
            string id = "code-" + Guid.NewGuid().ToString("N")[..8], title = CodeTitle.Trim(), prompt = CodePrompt.Trim();
            await Task.Run(() => { w.StartTask(repo, id); w.Assign(id, title, name, prompt); });
            CodeTitle = ""; CodePrompt = ""; Notify(nameof(CodeTitle)); Notify(nameof(CodePrompt));
            await RefreshCodeAsync(); SelectedCodeTask = CodeTasks.FirstOrDefault(t => t.Id == id);
            await RunRuntimeAsync(w, rt, id, prompt);
            await RefreshCodeAsync();
        }));
        StopCodeCommand = new(_ => CodeRunning, _ => { codeCts?.Cancel(); return Task.CompletedTask; });
        RunCodeCheckCommand = new(_ => TaskSelectedActive && CodeChecks.Count > 0, _ => CodeGuard(async () =>
        {
            var w = Ledger(); string id = SelectedSummary!.Id; string name = CodeChecks[Math.Clamp(CodeCheckPick, 0, CodeChecks.Count - 1)].Split(':')[0];
            CodeRunning = true; Notify(nameof(CodeRunning)); RaiseCommands(); SetCode($"Running check '{name}' on {id}…");
            var r = await Task.Run(() => w.RunCheck(id, name));
            SetCode($"Check '{name}': exit {r.Exit}{(r.Fresh ? "" : " (the check changed the files, so this result is already stale)")}.");
            await RefreshCodeAsync();
        }));
        ReviseCodeCommand = new(_ => TaskSelectedActive && CodeRevisionText.Trim().Length > 0, _ => CodeGuard(async () =>
        {
            var w = Ledger(); var s = SelectedSummary!; string name = CodeRuntimePick == 1 ? "codex" : "claude";
            var rt = RuntimeFor(w, name) ?? throw new RuntimeRefused("not_found", $"{name} was not found.");
            var info = await Task.Run(() => rt.Discover());
            Remember(w, name, info);
            if (!info.Dispatchable) throw new RuntimeRefused("not_dispatchable", $"{(info.IsFixture ? "The fixture" : name)} was not started: {info.DispatchState}." + (info.Available && !info.SignedIn && !info.IsFixture ? " Its sign-in is described under Runtimes." : ""));
            string prompt = CodeRevisionText.Trim(); CodeRevisionText = ""; Notify(nameof(CodeRevisionText));
            await Task.Run(() => w.Assign(s.Id, s.Title, name, prompt));
            await RunRuntimeAsync(w, rt, s.Id, prompt);
            await RefreshCodeAsync();
        }));
        ApproveCodeCommand = new(_ => TaskSelectedActive && SelectedSummary is { Integrated: false, Revision.Length: > 0, ApprovedForRevision: false } && CodePatchText.StartsWith("Revision "), _ => CodeGuard(async () =>
        {
            var w = Ledger(); var s = SelectedSummary!;
            var (ok, ap) = await Task.Run(() =>
            {
                string rev = w.Patch(s.Id).Hash;
                if (rev != s.Revision) return (false, (Approval?)null);
                var a = w.Request("integrate", s.Id, "apply " + rev, s.Repo, rev, TimeSpan.FromHours(1)); w.Decide(a.Id, true); return (true, a);
            });
            SetCode(ok ? $"Approved: apply revision {s.Revision[..12]} of '{s.Title}' to {s.Repo}, once, until {ap!.Expires.LocalDateTime:t}. Nothing has been applied yet." : "The patch changed while you were reading it, so nothing was approved. Read the new revision first.");
            await RefreshCodeAsync();
        }));
        ApplyCodeCommand = new(_ => TaskSelectedActive && SelectedSummary is { Integrated: false, ApprovedForRevision: true }, _ => CodeGuard(async () =>
        {
            var w = Ledger(); var s = SelectedSummary!;
            var r = await Task.Run(() => w.Integrate(s.Id, s.Repo, s.Revision));
            SetCode(r.Applied ? (r.Already ? "That revision was already applied; nothing changed." : $"Applied revision {s.Revision[..12]} to {s.Repo}. It is not committed.") : r.Reason switch
            {
                "no_approval" or "not_decided" => "Not applied: this revision has not been approved. Select Approve this exact revision first.",
                "declined" => "Not applied: the approval was declined. Approve this revision to continue.",
                "revoked" => "Not applied: the approval was revoked. Approve this revision again.",
                "expired" => "Not applied: the approval expired. Approve this revision again.",
                "already_used" => "Not applied: that approval was already used. Approve this revision again.",
                "revision_changed" or "action_changed" => "Not applied: the patch changed after it was approved. Read the new revision, then approve it.",
                "different_destination" => "Not applied: this task was already applied to a different repository.",
                _ => "Not applied: " + r.Reason,
            });
            await RefreshCodeAsync();
        }));
        PublishCodeCommand = new(_ => CodeIdle && SelectedSummary is { Integrated: true, Published: false, State: "active" }, _ => CodeGuard(async () =>
        {
            var w = Ledger(); var s = SelectedSummary!;
            var r = await Task.Run(() =>
            {
                string rev = w.Patch(s.Id).Hash;
                var a = w.Request("publish", s.Id, "commit " + rev, s.Repo, rev, TimeSpan.FromMinutes(10)); w.Decide(a.Id, true);
                return w.Publish(s.Id, s.Repo, $"Agent Hub: {s.Title}");
            });
            SetCode(r.Ok ? $"Committed locally as {r.Detail[..Math.Min(10, r.Detail.Length)]}: only the files of this revision. Nothing was pushed." : r.Detail.StartsWith("owner_edit") ? $"Not committed. You changed these files after the patch was applied: {r.Detail["owner_edit_since_integration: ".Length..]}. Your edits are untouched." : "Not committed: " + r.Detail);
            await RefreshCodeAsync();
        }));
        CancelCodeTaskCommand = new(_ => TaskSelectedActive, _ => CodeGuard(async () =>
        {
            var w = Ledger(); string id = SelectedSummary!.Id;
            string? saved = await Task.Run(() => w.CancelTask(id));
            SetCode("Cancelled. Its workspace and branch are removed" + (saved != null ? $"; its changes were saved as {saved}." : ".") + " Your own files were not touched.");
            await RefreshCodeAsync();
        }));
        return [ShowCodeCommand, RefreshCodeCommand, DiscoverRuntimesCommand, RegisterCheckCommand, AssignCodeCommand, StopCodeCommand, RunCodeCheckCommand, ReviseCodeCommand, ApproveCodeCommand, ApplyCodeCommand, PublishCodeCommand, CancelCodeTaskCommand];
    }
}
