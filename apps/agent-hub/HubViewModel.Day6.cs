using System.Collections.ObjectModel;
using System.Text.Json;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>An image attached to the message being written. The bytes are held here, and in the local draft, until the message is sent or the image is removed.</summary>
public sealed record AttachmentRow(ImageData Image, bool Missing = false)
{
    public string Title => Image.Name;
    public string Details => Missing ? "Missing: attach it again." : $"{Image.Width}×{Image.Height} · {Image.Size / 1024.0:0.#} KB · {Image.Mime} · {Image.Hash[..12]}";
    public string AutomationName => $"Attachment {Title}, {Details}";
    public override string ToString() => AutomationName;
}

public sealed record AnswerRow(DocAnswer A)
{
    public string Question => "You asked: " + A.Question;
    public string Answer => A.Answer;
    public string Basis => A.Status switch
    {
        "answered" => $"Answered by {A.Model} ({A.Destination}) from the passages below.",
        "partial" => $"Answered by {A.Model}, but it also cited {string.Join(", ", A.InvalidRefs ?? [])}, which were not offered. Trust only the passages below.",
        "invalid_citations" => $"Answered by {A.Model}, but it cited {string.Join(", ", A.InvalidRefs ?? [])}, which were not offered. Nothing here supports the answer.",
        "uncited" => $"Answered by {A.Model}, but it cited nothing. Treat it as unsupported.",
        _ => "No evidence in this project's documents. Nothing was sent to a model.",
    };
    public IReadOnlyList<Citation> Citations => A.Citations;
    public string AutomationName => $"{Question}. {Answer}";
    public override string ToString() => AutomationName;
}

public sealed record ReplayRow(ReplayEvent E)
{
    public string Text => $"#{E.Seq} · {E.Summary}";
    public override string ToString() => Text;
}

/// <summary>Day 6: composer with evidence, documents, backup and replay, and recovery of interrupted work.</summary>
public sealed partial class HubViewModel
{
    // --- The composer: multiline, with a recipient, attachments and a draft that survives (AH84, AH85, AH33, AH86) --------------------
    public ObservableCollection<AttachmentRow> Attachments { get; } = [];
    public AttachmentRow? InspectedAttachment { get; private set; }
    public bool HasAttachments => Attachments.Count > 0;
    private string composerWorkspace = "", composerProject = "";
    private bool restoring;
    private readonly Dictionary<string, string> imageConfirms = [];
    private IReadOnlyList<DispatchPreview> lastPreviews = [];
    public bool RecipientNeedsReselect { get; private set; }
    public string ComposerNote { get; private set; } = "";

    /// <summary>Who would receive the message, by role, with the model each role uses and whether it is on this computer.</summary>
    public string ComposerRecipient
    {
        get
        {
            string One(string role)
            {
                var a = team?.Assignments.FirstOrDefault(x => x.Role == role);
                if (a == null) return $"the {role} (no model assigned)";
                var c = team!.Connections.FirstOrDefault(x => x.Id == a.ConnectionId);
                return $"the {role}: {a.ModelId} on {c?.Label ?? "an unknown connection"} ({(c?.IsCloud == true ? "cloud" : "this computer")})";
            }
            var roles = RecipientSets[Math.Clamp(RecipientIndex, 0, RecipientSets.Length - 1)];
            return "To " + string.Join("; ", (roles.Contains("everyone") ? Team.Roles : roles).Select(One));
        }
    }

    private void ComposerChanged(bool save = true)
    {
        if (!restoring && save) { imageConfirms.Clear(); SaveComposer(); }
        foreach (var n in new[] { nameof(ComposerRecipient), nameof(HasAttachments), nameof(RecipientNeedsReselect), nameof(ComposerNote), nameof(ImageInspectText), nameof(HasImageInspect) }) Notify(n);
        RaiseCommands();
    }

    /// <summary>Saves under the workspace and project the composer was loaded for, whichever is open now, so switching can never overwrite another draft.</summary>
    private void SaveComposer()
    {
        if (Drafts == null || composerWorkspace.Length == 0 || composerProject.Length == 0) return;
        string ws = composerWorkspace, project = composerProject;
        var role = RecipientSets[Math.Clamp(RecipientIndex, 0, RecipientSets.Length - 1)][0];
        string? task = selectedTask?.Id;
        var refs = Attachments.Where(a => !a.Missing).Select(a => new AttachmentRef(a.Image.Name, a.Image.Hash, a.Image.Mime, a.Image.Width, a.Image.Height)).ToList();
        foreach (var a in Attachments.Where(a => !a.Missing)) Drafts.SaveBlob(ws, a.Image.Hash, a.Image.Bytes);
        EditDrafts(ws, f =>
        {
            if (InstructionText.Length == 0 && refs.Count == 0) f.Composer.Remove(project);
            else f.Composer[project] = new(project, task, RecipientIndex < 0 ? "builder" : role, InstructionText, refs, RecipientNeedsReselect);
        });
    }

    /// <summary>
    /// A different workspace is opening: stop saving the composer anywhere until its own project is known, so text typed in the gap can never land in the previous workspace's draft.
    /// </summary>
    private void DetachComposer(bool force = false)
    {
        if (!force && composerWorkspace == (workspaceKey ?? "") && composerProject.Length > 0 && selected != null) return; // a refresh of the same workspace keeps what is being typed
        composerWorkspace = workspaceKey ?? "";
        composerProject = "";
        restoring = true;
        Attachments.Clear(); imageConfirms.Clear(); lastPreviews = [];
        ClearInspector();
        InstructionText = ""; RecipientNeedsReselect = false; ComposerNote = "";
        restoring = false;
        Notify(nameof(InstructionText)); ComposerChanged(save: false);
    }

    /// <summary>Puts back what was typed for this project: text, recipient and attachment references. A recipient that is no longer assigned must be chosen again.</summary>
    private void RestoreComposer()
    {
        if (workspaceKey == null || selected == null) return;
        composerWorkspace = workspaceKey;
        composerProject = selected.Id;
        var draft = LoadDrafts()?.Composer.GetValueOrDefault(selected.Id);
        restoring = true;
        Attachments.Clear();
        imageConfirms.Clear();
        lastPreviews = [];
        ClearInspector();
        InstructionText = draft?.Text ?? "";
        RecipientNeedsReselect = false;
        ComposerNote = "";
        if (draft != null)
        {
            int index = Array.FindIndex(RecipientSets, s => s[0] == draft.Recipient);
            bool assigned = draft.Recipient == "everyone" || team?.Assignments.Any(a => a.Role == draft.Recipient) == true;
            RecipientIndex = index < 0 ? 0 : index;
            if (!assigned) { RecipientNeedsReselect = true; ComposerNote = $"This draft was for the {draft.Recipient}, who has no model now. Choose who should receive it."; }
            foreach (var r in draft.Attachments)
            {
                var bytes = Drafts?.LoadBlob(workspaceKey, r.Hash);
                if (bytes != null && ProjectStore.Hash(bytes) == r.Hash) Attachments.Add(new(new(r.Name, r.Mime, bytes, r.Width, r.Height)));
                else Attachments.Add(new(new(r.Name, r.Mime, [], r.Width, r.Height), true));
            }
        }
        restoring = false;
        Notify(nameof(InstructionText)); Notify(nameof(RecipientIndex)); Notify(nameof(Attachments));
        ComposerChanged(save: false); // restoring is not an edit: nothing is written, so a draft read at the same moment is never torn
    }

    /// <summary>The inspector shows an attachment of the composer on screen and nothing else: a workspace or project change closes it, with its image, its text and its confirmations.</summary>
    private void ClearInspector()
    {
        InspectedAttachment = null; InspectedBitmap = null;
        Notify(nameof(InspectedBitmap)); Notify(nameof(ImageInspectText)); Notify(nameof(HasImageInspect));
    }

    public async Task<string?> AttachImageAsync(string name, byte[] bytes)
    {
        try
        {
            if (Attachments.Count >= Images.MaxImages) return $"At most {Images.MaxImages} images can go in one message.";
            var image = Images.Read(name, bytes);
            if (Attachments.Any(a => a.Image.Hash == image.Hash)) return "That image is already attached.";
            Attachments.Add(new(image));
            instructionBindings = null;
            ComposerChanged();
            return null;
        }
        catch (StoreException e) { ShowNotice(e.Message + " " + e.Error.Recovery, "Dismiss", DismissNoticeCommand); return e.Message; }
    }

    public Task<string?> AttachFileAsync(string path)
    {
        try { return AttachImageAsync(Path.GetFileName(path), File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Task.FromResult<string?>("That file could not be read."); }
    }

    public WorkspaceCommand RemoveAttachmentCommand { get; private set; } = null!;
    public WorkspaceCommand InspectAttachmentCommand { get; private set; } = null!;
    public WorkspaceCommand ConfirmImagesCommand { get; private set; } = null!;
    public WorkspaceCommand AttachFileCommand { get; private set; } = null!;

    public string ImageInspectText
    {
        get
        {
            if (InspectedAttachment is not { } a) return "";
            var where = lastPreviews.Count > 0 ? string.Join("; ", lastPreviews.Select(p => $"{p.Role}: {p.Model} on {p.ConnectionLabel} ({(p.IsCloud ? "cloud" : "this computer")}) — " + p.ImageStatus switch { "ok" => "will receive it", "needs_confirmation" => "needs your confirmation", "no_vision" => "cannot accept images", _ => "" })) : ComposerRecipient + ". Preview the message to see exactly who would receive it.";
            return $"{a.Image.Summary}. Full hash {a.Image.Hash}. {where}";
        }
    }
    public bool HasImageInspect => InspectedAttachment != null;
    public Avalonia.Media.Imaging.Bitmap? InspectedBitmap { get; private set; }

    // --- Documents (AH34, AH110) -----------------------------------------------------------------------------------------------
    public bool DocsVisible => ListVisible && center == "docs";
    public ObservableCollection<DocumentInfo> DocRows { get; } = [];
    public ObservableCollection<AnswerRow> AnswerRows { get; } = [];
    // Typing here must re-evaluate the buttons that depend on it, or they stay disabled for anyone not using the keyboard path the checks use.
    private DocumentInfo? selectedDoc; private string newDocName = "", newDocText = "", askText = "";
    public DocumentInfo? SelectedDoc { get => selectedDoc; set { selectedDoc = value; RaiseCommands(); } }
    public string NewDocName { get => newDocName; set { newDocName = value ?? ""; RaiseCommands(); } }
    public string NewDocText { get => newDocText; set { newDocText = value ?? ""; RaiseCommands(); } }
    public string AskText { get => askText; set { askText = value ?? ""; RaiseCommands(); } }
    public int AskRole { get; set; }
    public string PassageText { get; private set; } = "";
    public string DocsNote { get; private set; } = "";
    public string ForgetReportText { get; private set; } = "";
    public WorkspaceCommand ShowDocsCommand { get; private set; } = null!;
    public WorkspaceCommand AddDocCommand { get; private set; } = null!;
    public WorkspaceCommand EditDocCommand { get; private set; } = null!;
    public WorkspaceCommand RemoveDocCommand { get; private set; } = null!;
    public WorkspaceCommand ForgetDocCommand { get; private set; } = null!;
    public WorkspaceCommand AskDocsCommand { get; private set; } = null!;
    public WorkspaceCommand OpenCitationCommand { get; private set; } = null!;

    private async Task RefreshDocsAsync()
    {
        if (!IsWorkspace || path is not string file || selected is not { } project) return;
        try
        {
            var client = await Worker();
            var docs = await client.ReadDocumentsAsync(file, project.Id);
            var answers = await client.ReadAnswersAsync(file, project.Id);
            if (path != file || selected?.Id != project.Id) return;
            DocRows.Clear(); foreach (var d in docs) DocRows.Add(d);
            AnswerRows.Clear(); foreach (var a in answers.AsEnumerable().Reverse()) AnswerRows.Add(new(a));
            Notify(nameof(DocRows)); Notify(nameof(AnswerRows));
        }
        catch (WorkerException) { }
    }

    // --- Backup, restore, erase, diagnostics (AH47, AH51, AH112) -----------------------------------------------------------------
    public bool DataVisible => ListVisible && center == "data";
    public string DataStatus { get; private set; } = "Nothing exported or restored in this session.";
    public string DiagnosticsPreview { get; private set; } = "";
    public bool DiagnosticsIncludeContent { get; set; }
    public Func<string, Task<bool>>? ConfirmErase { get; set; }
    public WorkspaceCommand ShowDataCommand { get; private set; } = null!;
    public WorkspaceCommand ExportWorkspaceCommand { get; private set; } = null!;
    public WorkspaceCommand RestoreWorkspaceCommand { get; private set; } = null!;
    public WorkspaceCommand EraseWorkspaceCommand { get; private set; } = null!;
    public WorkspaceCommand PreviewDiagnosticsCommand { get; private set; } = null!;
    public WorkspaceCommand SaveDiagnosticsCommand { get; private set; } = null!;
    private void SetData(string text) { DataStatus = text; Notify(nameof(DataStatus)); }

    // --- Read-only replay (AH59) ---------------------------------------------------------------------------------------------
    public bool Replaying { get; private set; }
    public bool ReplayVisible => ListVisible && center == "replay";
    public ObservableCollection<ReplayRow> ReplayRows { get; } = [];
    public int ReplayPosition { get; set; }
    public IReadOnlyList<string> ReplayLines { get; private set; } = [];
    public string ReplayHeading { get; private set; } = "";
    public WorkspaceCommand EnterReplayCommand { get; private set; } = null!;
    public WorkspaceCommand LeaveReplayCommand { get; private set; } = null!;
    public WorkspaceCommand ReplayStepCommand { get; private set; } = null!;

    private async Task ShowReplayAtAsync(int index)
    {
        if (ReplayRows.Count == 0 || path is not string file || selected is not { } project) return;
        index = Math.Clamp(index, 0, ReplayRows.Count - 1);
        ReplayPosition = index;
        var e = ReplayRows[index].E;
        var state = await (await Worker()).ReplayStateAsync(file, project.Id, e.Seq);
        ReplayHeading = state.StopReason != null && e.Seq >= (state.StoppedAt ?? long.MaxValue) ? $"Replay stops after event #{state.Seq} of {state.Latest}. {state.StopReason}" : $"After event #{state.Seq} of {state.Latest}: {e.Summary} ({e.At})";
        ReplayLines = [.. state.Tasks.Select(t => $"{t.Title} · {t.Status.Replace('_', ' ')} · {t.Revisions} revision(s)" + (t.AcceptedRevision is { } r ? $" · accepted revision {r}" : "")),
            .. state.Notes, .. (state.StopReason is { } why ? new[] { why } : Array.Empty<string>())];
        Notify(nameof(ReplayPosition)); Notify(nameof(ReplayHeading)); Notify(nameof(ReplayLines));
    }

    // --- Uncertain actions, wake, close (AH67, AH103, AH104) -------------------------------------------------------------------------------
    public string ReceiptText { get; set; } = "";
    public WorkspaceCommand ConfirmAttemptCommand { get; private set; } = null!;
    public WorkspaceCommand AbandonAttemptCommand { get; private set; } = null!;
    public WorkspaceCommand RetryUncertainCommand { get; private set; } = null!;
    public WorkspaceCommand RecordChargeCommand { get; private set; } = null!;

    private Task Resolve(AttemptRow row, string choice)
    {
        string ws = path!, id = row.A.Id, click = Guid.NewGuid().ToString("N");
        double? amount = double.TryParse(ReceiptText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        return Fail(async () =>
        {
            string? error = await Submit($"resolve|{ws}|{id}|{choice}|{click}", ws, async cmd => await (await Worker()).ResolveAttemptAsync(ws, cmd, id, choice, amount));
            await RefreshRunsAsync();
            return error;
        });
    }

    /// <summary>Sleep and wake signals. A real suspend reaches these from the operating system; checks call them directly.</summary>
    public async Task SleepAsync()
    {
        if (!IsWorkspace || path is not string file || !CanWrite) return;
        try { await (await Worker()).LifecycleAsync(file, "sleep"); } catch (WorkerException) { }
    }

    public async Task WakeAsync()
    {
        if (!IsWorkspace || path is not string file || !CanWrite) return;
        try
        {
            // A write whose answer was lost across the sleep: ask the worker whether it was applied, and say so, rather than guessing or resending.
            if (pending is { Uncertain: true } p && p.Workspace == file)
            {
                bool applied = await workspaces!.CommandStatusAsync(p.Workspace, p.CommandId) != null;
                if (applied) { pending = null; ClearNotice(); await OpenAsync(file, workspaceName); }
                else ShowNotice("The computer woke up, and your last change was not recorded. Nothing was resent. Choose Try again if you still want it.", "Dismiss", DismissNoticeCommand);
            }
            if (lostMove is { } lm && lm.Workspace == file)
            {
                if (await workspaces!.CommandStatusAsync(lm.Workspace, lm.CommandId) != null) { lostMove = null; ClearNotice(); await OpenAsync(file, workspaceName); }
                else ShowNotice("The computer woke up, and your last move was not recorded. Nothing was resent. Choose Try again if you still want it.", "Dismiss", DismissNoticeCommand);
            }
            var report = await (await Worker()).LifecycleAsync(file, "wake");
            await RefreshRunsAsync();
            if (report.NeedsDecision.Count > 0)
                ShowNotice($"{report.NeedsDecision.Count} attempt(s) were cut off while the computer slept. Their partial output is kept. Open the run inspector to confirm, abandon or retry each one; nothing was sent again.", "Dismiss", DismissNoticeCommand);
        }
        catch (WorkerException e) { ShowNotice(e.Message, "Dismiss", DismissNoticeCommand); }
    }

    /// <summary>Choices when the window closes with runs in progress: hide it, let the runs finish in the background and then exit, or stop them and exit.</summary>
    public static IReadOnlyList<(string Value, string Label)> CloseChoices { get; } =
    [
        ("hide", "Keep the window open, minimised: the runs continue and you can come back to it"),
        ("finish", "Let the runs finish in the background, then close Agent Hub on its own"),
        ("stop", "Stop the runs now and close; what they produced so far is kept"),
    ];

    public bool HasActiveRuns => AnyRunning;

    /// <summary>Moves the replay to one event, from the timeline list.</summary>
    public void SeekReplay(int index) { if (Replaying && index >= 0 && index != ReplayPosition) _ = Guarded(() => ShowReplayAtAsync(index)); }

    /// <summary>Applies the owner's choice. Stop and finish are bounded; whatever is still running afterwards keeps a lease that expires, so the next launch settles it.</summary>
    public async Task<string> ClosePolicyAsync(string choice, int maxMs = 8000)
    {
        if (path is not string file || !CanWrite) return "close";
        try
        {
            if (choice == "stop") { await (await Worker()).ShutdownAsync(file, "stop", Math.Min(maxMs, 5000)); await RefreshRunsAsync(); await ReleaseWriteLockAsync(); return "close"; }
            if (choice == "finish") return "finish";
        }
        catch (WorkerException) { }
        return "hide";
    }

    // --- Initialisation ---------------------------------------------------------------------------------------------------------------
    private void InitDay6()
    {
        RemoveAttachmentCommand = new(p => p is AttachmentRow, p =>
        {
            var row = (AttachmentRow)p!;
            Attachments.Remove(row);
            if (InspectedAttachment == row) { InspectedAttachment = null; InspectedBitmap = null; Notify(nameof(InspectedBitmap)); }
            instructionBindings = null; lastPreviews = [];
            ComposerChanged();
            return Task.CompletedTask;
        });
        InspectAttachmentCommand = new(p => p is AttachmentRow, p =>
        {
            InspectedAttachment = (AttachmentRow)p!;
            try { InspectedBitmap = InspectedAttachment.Missing ? null : new Avalonia.Media.Imaging.Bitmap(new MemoryStream(InspectedAttachment.Image.Bytes)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { InspectedBitmap = null; }
            Notify(nameof(InspectedBitmap)); ComposerChanged();
            return Task.CompletedTask;
        });
        ConfirmImagesCommand = new(_ => lastPreviews.Any(p => p.ImageStatus == "needs_confirmation"), async _ =>
        {
            foreach (var p in lastPreviews.Where(p => p.ImageStatus == "needs_confirmation")) imageConfirms[p.Role] = p.ImageToken;
            await PreviewInstructionCommand.RunAsync();
        });
        AttachFileCommand = new(_ => PickFile != null && IsWorkspace, async _ => { if (await PickFile!() is string f) await AttachFileAsync(f); });

        ShowDocsCommand = new(_ => IsWorkspace, async _ => { SetCenter("docs"); Notify(nameof(DocsVisible)); await RefreshDocsAsync(); });
        AddDocCommand = new(_ => CanWrite && selected != null && NewDocName.Trim().Length > 0 && NewDocText.Trim().Length > 0, async _ =>
        {
            string ws = path!, project = selected!.Id, name = NewDocName.Trim(), text = NewDocText, click = Guid.NewGuid().ToString("N");
            await Fail(async () =>
            {
                string? error = await Submit($"doc|{ws}|{click}", ws, async id => await (await Worker()).AddDocumentAsync(ws, id, project, name, text));
                if (error == null) { NewDocName = ""; NewDocText = ""; Notify(nameof(NewDocName)); Notify(nameof(NewDocText)); await RefreshDocsAsync(); }
                return error;
            });
        });
        EditDocCommand = new(_ => CanWrite && SelectedDoc is { State: "current", Kind: "text" } && NewDocText.Trim().Length > 0, async _ =>
        {
            string ws = path!, doc = SelectedDoc!.Id, text = NewDocText, click = Guid.NewGuid().ToString("N");
            await Fail(async () =>
            {
                string? error = await Submit($"docedit|{ws}|{click}", ws, async id => await (await Worker()).EditDocumentAsync(ws, id, doc, text));
                if (error == null) { NewDocText = ""; Notify(nameof(NewDocText)); await RefreshDocsAsync(); }
                return error;
            });
        });
        RemoveDocCommand = new(_ => CanWrite && SelectedDoc is { State: "current" }, async _ =>
        {
            string ws = path!, doc = SelectedDoc!.Id, click = Guid.NewGuid().ToString("N");
            await Fail(async () =>
            {
                string? error = await Submit($"docrm|{ws}|{click}", ws, async id => await (await Worker()).RemoveDocumentAsync(ws, id, doc));
                if (error == null) await RefreshDocsAsync();
                return error;
            });
        });
        ForgetDocCommand = new(_ => CanWrite && SelectedDoc is { State: not "forgotten" }, async _ => await Guarded(async () =>
        {
            var r = await (await Worker()).ForgetSourceAsync(path!, Guid.NewGuid().ToString("N"), SelectedDoc!.Id);
            ForgetReportText = $"Forgot “{r.Report.Name}”: {r.Report.RevisionsErased} revision(s), {r.Report.IndexRowsErased} index entries, {(r.Report.ThumbnailErased ? "its thumbnail, " : "")}{r.Report.PacketsScrubbed} reusable packet(s) and {r.Report.AnswersScrubbed} answer(s) cleaned. "
                + string.Join(" ", r.Report.TransmittedCopies) + " What remains: " + string.Join(" ", r.Report.Remaining);
            Notify(nameof(ForgetReportText));
            await RefreshDocsAsync();
        }));
        AskDocsCommand = new(_ => CanWrite && selected != null && AskText.Trim().Length > 0, async _ =>
        {
            string ws = path!, project = selected!.Id, q = AskText.Trim(), role = new[] { "reviewer", "builder", "planner" }[Math.Clamp(AskRole, 0, 2)], click = Guid.NewGuid().ToString("N");
            await Fail(async () =>
            {
                string? error = await Submit($"ask|{ws}|{click}", ws, async id => await (await Worker()).AskDocumentsAsync(ws, id, project, q, role));
                if (error == null) { AskText = ""; Notify(nameof(AskText)); await RefreshDocsAsync(); }
                return error;
            });
        });
        OpenCitationCommand = new(p => p is Citation, async p => await Guarded(async () =>
        {
            var c = (Citation)p!;
            var v = await (await Worker()).OpenPassageAsync(path!, c.DocId, c.Revision, c.Start, c.Length);
            PassageText = v.State == "forgotten" ? v.Note : $"{v.DocName}, revision {v.Revision}. {v.Note}\n…{v.Before}【{v.Text}】{v.After}…";
            Notify(nameof(PassageText));
        }));

        ShowDataCommand = new(_ => IsWorkspace, _ => { SetCenter("data"); Notify(nameof(DataVisible)); return Task.CompletedTask; });
        ExportWorkspaceCommand = new(_ => CanWrite && PickFolder != null, async _ => await Guarded(async () =>
        {
            if (await PickFolder!() is not string parent) return;
            string target = Path.Combine(parent, "agenthub-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var m = await (await Worker()).ExportWorkspaceAsync(path!, target);
            SetData($"Exported {m.Tasks} tasks, {m.Decisions} decisions and {m.Outputs} outputs to {target}. {m.Files.Count} files are listed with their hashes. {m.Excluded}");
        }));
        RestoreWorkspaceCommand = new(_ => Workspaces != null && PickFolder != null, async _ => await Guarded(async () =>
        {
            if (await PickFolder!() is not string package || await PickFolder!() is not string parent) return;
            string target = Path.Combine(parent, "restored-" + DateTime.Now.ToString("HHmmss"));
            var r = await (await Worker()).RestoreWorkspaceAsync(package, target);
            SetData($"Restored {r.Tasks} tasks and {r.Decisions} decisions to {target} under a new workspace ID. {r.Note} Open it with File → Open workspace.");
        }));
        EraseWorkspaceCommand = new(_ => CanWrite && ConfirmErase != null && !AnyRunning, async _ =>
        {
            string ws = path!; string? key = workspaceKey; // what the owner is asked about, fixed before the question
            if (!await ConfirmErase!($"Erase “{workspaceName ?? Path.GetFileName(Path.GetDirectoryName(ws))}”: its database, stored outputs, keys, and this window's saved drafts and attachment copies. Type ERASE to confirm.")) return;
            if (path != ws || workspaceKey != key) { ShowNotice("A different workspace is open now, so nothing was erased. Open the one you meant, then erase it.", "Dismiss", DismissNoticeCommand); return; }
            await Guarded(async () =>
            {
                var r = await (await Worker()).EraseWorkspaceAsync(ws);
                (IReadOnlyList<string> Removed, IReadOnlyList<string> Failed) local = key != null && Drafts != null ? Drafts.Erase(key) : ([], []);
                bool layout = key == null || Layouts?.Delete(key) != false;
                SetData("Erased: " + string.Join(", ", r.Report.Removed) + ". This window's drafts and attachment copies for it: " + (local.Removed.Count == 0 && local.Failed.Count == 0 ? "none were kept" : $"{local.Removed.Count} removed")
                    + (local.Failed.Count > 0 ? $"; COULD NOT REMOVE {string.Join(", ", local.Failed)}: delete them yourself from {DraftStore.DefaultFolder}" : "") + (layout ? "" : "; its saved layout could not be removed")
                    + ". Copies this cannot reach: " + string.Join(" ", r.Report.ExternalCopies));
                StopPolling();
                path = null; Projects = []; Tasks = []; SetSelection(null); Notify(nameof(Projects)); Notify(nameof(IsWorkspace)); StatusText = "That workspace was erased.";
            });
        });
        PreviewDiagnosticsCommand = new(_ => IsWorkspace, async _ => await Guarded(async () =>
        {
            DiagnosticsPreview = await (await Worker()).RunDiagnosticsAsync(path!, AttemptRows.FirstOrDefault(r => r.A.Status is not "completed")?.A.Id ?? AttemptRows.FirstOrDefault()?.A.Id, DiagnosticsIncludeContent);
            Notify(nameof(DiagnosticsPreview));
        }));
        SaveDiagnosticsCommand = new(_ => IsWorkspace && DiagnosticsPreview.Length > 0, async _ => await Guarded(async () =>
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path!))!, "diagnostics");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "run-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            await (await Worker()).ExportRunDiagnosticsAsync(path!, AttemptRows.FirstOrDefault()?.A.Id, DiagnosticsIncludeContent, target);
            SetData($"Diagnostics saved to {target}. Nothing was sent anywhere.");
        }));

        EnterReplayCommand = new(_ => IsWorkspace && !Replaying && selected != null, async _ => await Guarded(async () =>
        {
            var events = await (await Worker()).ReplayTimelineAsync(path!, selected!.Id);
            ReplayRows.Clear(); foreach (var e in events) ReplayRows.Add(new(e));
            Replaying = true;
            SetCenter("replay");
            Notify(nameof(Replaying)); Notify(nameof(CanWrite)); Notify(nameof(CanEdit)); Notify(nameof(ReplayVisible)); Notify(nameof(ReplayRows));
            await ShowReplayAtAsync(ReplayRows.Count - 1);
            RaiseCommands();
        }));
        LeaveReplayCommand = new(_ => Replaying, _ =>
        {
            Replaying = false; ReplayRows.Clear(); ReplayLines = []; ReplayHeading = "";
            SetCenter("board");
            foreach (var n in new[] { nameof(Replaying), nameof(CanWrite), nameof(CanEdit), nameof(ReplayLines), nameof(ReplayHeading) }) Notify(n);
            RaiseCommands();
            return Task.CompletedTask;
        });
        ReplayStepCommand = new(_ => Replaying && ReplayRows.Count > 0, async p => await Guarded(async () => await ShowReplayAtAsync(p is int step ? ReplayPosition + step : p is string s && int.TryParse(s, out var n) ? ReplayPosition + n : ReplayPosition)));

        ConfirmAttemptCommand = new(p => CanWrite && p is AttemptRow { CanResolve: true }, p => Resolve((AttemptRow)p!, "confirm"));
        AbandonAttemptCommand = new(p => CanWrite && p is AttemptRow { CanAbandon: true }, p => Resolve((AttemptRow)p!, "abandon"));
        RetryUncertainCommand = new(p => CanWrite && p is AttemptRow { CanAbandon: true }, p => Resolve((AttemptRow)p!, "retry"));
        RecordChargeCommand = new(p => CanWrite && p is AttemptRow { CanRecordCharge: true }, async p =>
        {
            var row = (AttemptRow)p!;
            if (!double.TryParse(ReceiptText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount)) { ShowNotice("Enter the amount from the provider's receipt as a number.", "Dismiss", DismissNoticeCommand); return; }
            string ws = path!, id = row.A.Id, click = Guid.NewGuid().ToString("N");
            await Fail(async () =>
            {
                string? error = await Submit($"charge|{ws}|{id}|{amount}|{click}", ws, async cmd => await (await Worker()).ReconcileAttemptAsync(ws, cmd, id, amount));
                await RefreshRunsAsync(); await LoadTeamAsync();
                return error;
            });
        });
        commands = [.. commands, RemoveAttachmentCommand, InspectAttachmentCommand, ConfirmImagesCommand, AttachFileCommand, ShowDocsCommand, AddDocCommand, EditDocCommand, RemoveDocCommand, ForgetDocCommand, AskDocsCommand,
            OpenCitationCommand, ShowDataCommand, ExportWorkspaceCommand, RestoreWorkspaceCommand, EraseWorkspaceCommand, PreviewDiagnosticsCommand, SaveDiagnosticsCommand, EnterReplayCommand, LeaveReplayCommand,
            ReplayStepCommand, ConfirmAttemptCommand, AbandonAttemptCommand, RetryUncertainCommand, RecordChargeCommand, .. InitDay7(), .. InitDay8()];
    }
}
