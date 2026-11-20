using System.Collections.ObjectModel;
using System.ComponentModel;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

/// <summary>
/// One question to the owner. The same object stays in the list for as long as the question does: new data is applied to it, so the
/// reply box bound to it keeps its text, focus, caret and selection. Its reply text is a draft kept locally as it is typed.
/// </summary>
public sealed class QuestionRow(QuestionItem item, string reply, string? workspaceKey) : INotifyPropertyChanged
{
    private string reply = reply;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<QuestionRow>? Edited;

    public QuestionItem Item { get; private set; } = item;
    /// <summary>The workspace this question and its draft belong to.</summary>
    public string? WorkspaceKey { get; } = workspaceKey;
    public bool IsOpen => Item.Open;
    public string Heading => $"{Item.TaskTitle} · {Item.ProjectName}";
    public string Asked => $"{Item.AskedBy} asks: {Item.Text}";
    public string Status => Item.Open ? "Waiting for your answer" : $"You answered: {Item.Answer}";
    public string AutomationName => $"{Item.ProjectName}, {Item.TaskTitle}: {Item.Text}";
    public override string ToString() => AutomationName;

    /// <summary>Applies fresh data. Nothing happens, and nothing is raised, when the data is the same.</summary>
    public void Update(QuestionItem fresh)
    {
        if (fresh == Item) return;
        Item = fresh;
        foreach (var n in new[] { nameof(Item), nameof(IsOpen), nameof(Heading), nameof(Asked), nameof(Status), nameof(AutomationName) }) PropertyChanged?.Invoke(this, new(n));
        if (!fresh.Open) ReplyText = "";
    }

    public string ReplyText
    {
        get => reply;
        set
        {
            if (reply == value) return;
            reply = value;
            PropertyChanged?.Invoke(this, new(nameof(ReplyText)));
            Edited?.Invoke(this);
        }
    }
}

/// <summary>The conversation around the board: a filterable channel that holds still while it streams, questions that wait for the owner, and unsent drafts.</summary>
public sealed partial class HubViewModel
{
    /// <summary>Most messages kept in the list while the reader follows the newest; older ones are dropped, never from under a selection.</summary>
    public int ChannelCap { get; set; } = 2000;

    // --- Local drafts ---------------------------------------------------------------------------------------
    /// <summary>Where unsent text is kept. Null in tests that do not need it.</summary>
    public DraftStore? Drafts { get; set; }
    private string? workspaceKey;

    private DraftFile? LoadDrafts() => Drafts != null && workspaceKey != null ? Drafts.Load(workspaceKey) : null;

    private void EditDrafts(Action<DraftFile> change) => EditDrafts(workspaceKey, change);

    /// <summary>Edits the drafts of one named workspace, whichever workspace is open when this runs.</summary>
    private void EditDrafts(string? key, Action<DraftFile> change)
    {
        if (Drafts == null || key == null) return;
        var file = Drafts.Load(key);
        change(file);
        Drafts.Save(key, file);
    }

    private async Task LoadWorkspaceKeyAsync(string file, bool isWorkspace)
    {
        workspaceKey = null;
        if (!isWorkspace) return;
        try { workspaceKey = await Task.Run(() => new ProjectReader(file).WorkspaceId()); }
        catch (Exception ex) when (IsDataError(ex)) { }
    }

    // --- The channel: filter, follow, selection --------------------------------------------------------------
    private readonly List<ChannelRow> allRows = [];
    private string filter = "all";
    private bool follow = true;
    private int unseen;
    private IReadOnlyList<ChannelRow> channelSelection = [];

    /// <summary>0 shows everything, 1 hides transient progress, 2 shows only decisions and questions.</summary>
    public int ChannelFilterIndex
    {
        get => filter switch { "all" => 0, "quiet" => 1, _ => 2 };
        set => SetFilter(value switch { 0 => "all", 1 => "quiet", _ => "attention" });
    }

    private bool Passes(ChannelRow r) => filter switch { "all" => true, "quiet" => r.Message.Class != "progress", _ => r.Message.NeedsAttention };

    /// <summary>Set by the window to the first message in view. A filter change keeps the reader's place by seq number, not by row position.</summary>
    public long ChannelAnchorSeq { get; set; }
    public event Action<long>? ChannelAnchorRestore;
    public event Action? ChannelScrollToEnd;

    private int filterVersion;
    private bool evicted;
    private (int Matching, int All)? filterTotals;

    private void SetFilter(string next)
    {
        if (next == filter) return;
        long anchor = ChannelAnchorSeq;
        filter = next;
        filterTotals = null;
        // At once, from what this window holds; then the store is asked, because older matching items may have been dropped.
        channelRows.Clear();
        foreach (var r in allRows) if (Passes(r)) channelRows.Add(r);
        // The selection keeps only rows that are still listed.
        channelSelection = [.. channelSelection.Where(channelRows.Contains)];
        foreach (var n in new[] { nameof(ChannelFilterIndex), nameof(ChannelEmpty), nameof(ChannelFilterNote) }) Notify(n);
        RaiseCommands();
        RestoreAnchor(anchor);
        _ = ReloadFilteredAsync();
    }

    private void RestoreAnchor(long anchor)
    {
        if (channelRows.FirstOrDefault(r => r.Message.Seq >= anchor) is { } at) { ChannelAnchorSeq = at.Message.Seq; ChannelAnchorRestore?.Invoke(at.Message.Seq); }
    }

    /// <summary>
    /// A filtered list comes from the event store: the newest matching messages of the project, however many unrelated ones have
    /// arrived since. The list stays bounded (<see cref="ChannelCap"/>), and rows already on screen are reused so nothing flickers.
    /// </summary>
    private async Task ReloadFilteredAsync()
    {
        if (filter == "all" || !IsWorkspace || path is not string file || selected is not { } project) return;
        int v = ++filterVersion;
        string f = filter;
        FilteredPage page;
        try { page = await Task.Run(() => Channel.ReadMatching(file, project.Id, f, ChannelCap)); }
        catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { return; }
        if (v != filterVersion || f != filter || path != file || selected?.Id != project.Id) return;
        long anchor = ChannelAnchorSeq;
        var known = allRows.Concat(channelRows).GroupBy(r => r.Message.Seq).ToDictionary(g => g.Key, g => g.First());
        var rows = page.Messages.Select(m => known.TryGetValue(m.Seq, out var r) ? r : new ChannelRow(m)).ToList();
        // Rows that arrived while the read was running are newer than anything it returned.
        long newest = rows.Count == 0 ? 0 : rows[^1].Message.Seq;
        rows.AddRange(channelRows.Where(r => r.Message.Seq > newest && Passes(r)));
        filterTotals = (page.TotalMatching, page.TotalAll);
        evicted = false;
        if (!rows.Select(r => r.Message.Seq).SequenceEqual(channelRows.Select(r => r.Message.Seq)))
        {
            channelRows.Clear();
            foreach (var r in rows) channelRows.Add(r);
            channelSelection = [.. channelSelection.Where(channelRows.Contains)];
            RaiseCommands();
            RestoreAnchor(anchor);
        }
        foreach (var n in new[] { nameof(ChannelEmpty), nameof(ChannelFilterNote) }) Notify(n);
    }

    public string ChannelFilterNote => filter switch
    {
        "quiet" => $"Transient progress is hidden ({(filterTotals is { } t ? t.All - t.Matching : allRows.Count(r => r.Message.Class == "progress"))} updates).",
        "attention" => $"Showing decisions and questions only ({(filterTotals is { } u ? u.All - u.Matching : allRows.Count(r => !r.Message.NeedsAttention))} other messages hidden).",
        _ => "",
    };

    /// <summary>True while the channel keeps scrolling to the newest message. The window turns it off as soon as the reader scrolls away.</summary>
    public bool FollowLatest
    {
        get => follow;
        set
        {
            if (follow == value) return;
            follow = value;
            if (value) unseen = 0;
            Notify(); Notify(nameof(ShowFollowButton)); Notify(nameof(FollowLabel));
        }
    }
    public bool ShowFollowButton => !follow;
    public string FollowLabel => unseen > 0 ? $"Follow latest ({unseen} new)" : "Follow latest";
    public WorkspaceCommand FollowLatestCommand { get; private set; } = null!;

    /// <summary>The rows the reader has selected. Streaming never changes this.</summary>
    public IReadOnlyList<ChannelRow> ChannelSelection
    {
        get => channelSelection;
        set { channelSelection = value; RaiseCommands(); }
    }

    /// <summary>Exactly what is on screen for the selected rows, oldest first: who and when, then the text.</summary>
    public string ChannelCopyText => string.Join("\n\n", channelSelection.OrderBy(r => r.Message.Seq).Select(r => $"{r.Who} · {r.When}\n{r.Text}"));
    public Func<string, Task>? Clipboard { get; set; }
    public WorkspaceCommand CopyChannelCommand { get; private set; } = null!;

    /// <summary>Takes one message into the channel without moving anything the reader is doing.</summary>
    private void Ingest(ChannelRow row)
    {
        allRows.Add(row);
        if (!Passes(row)) return;
        channelRows.Add(row);
        if (!follow) { unseen++; Notify(nameof(FollowLabel)); }
    }

    private void AfterIngest()
    {
        Notify(nameof(ChannelFilterNote));
        // Old rows are dropped only while following, and never from under a selection.
        int batch = Math.Max(1, ChannelCap / 10);
        while (follow && allRows.Count > ChannelCap + batch && !channelSelection.Any(allRows.Take(batch).Contains))
        {
            foreach (var r in allRows.Take(batch).ToList()) { allRows.Remove(r); if (filter == "all") channelRows.Remove(r); }
            evicted = true;
        }
        // A filtered list is not trimmed by what the unfiltered one dropped; it is bounded on its own, newest kept.
        while (filter != "all" && follow && channelRows.Count > ChannelCap + batch && !channelSelection.Any(channelRows.Take(batch).Contains))
            for (int i = 0; i < batch; i++) channelRows.RemoveAt(0);
        if (evicted && filter != "all") _ = ReloadFilteredAsync();
        if (follow) ChannelScrollToEnd?.Invoke();
    }

    /// <summary>The window asks for the newest message when the channel is opened.</summary>
    public void ChannelScrollToEndRequested() => ChannelScrollToEnd?.Invoke();

    private void ResetChannelRows()
    {
        allRows.Clear(); channelRows.Clear(); channelSelection = []; unseen = 0;
    }

    // --- Questions --------------------------------------------------------------------------------------------
    private readonly ObservableCollection<QuestionRow> questionRows = [];
    public ObservableCollection<QuestionRow> QuestionRows => questionRows;
    public int OpenQuestionCount => questionRows.Count(q => q.IsOpen);
    public string QuestionsLabel => $"Questions ({OpenQuestionCount})";
    public bool QuestionsEmpty => questionRows.Count == 0;
    public bool QuestionsVisible => ListVisible && center == "questions";
    public WorkspaceCommand ShowQuestionsCommand { get; private set; } = null!;
    public WorkspaceCommand ReplyQuestionCommand { get; private set; } = null!;
    public WorkspaceCommand FixtureClaimCommand { get; private set; } = null!;
    public WorkspaceCommand FixtureAskCommand { get; private set; } = null!;

    /// <summary>
    /// Brings the list up to date by question ID. A question that is still on screen keeps its row (and so its reply box, with focus, caret and
    /// selection): data is applied to it, and an unchanged question raises nothing. Only a new, vanished or reordered question changes the list.
    /// </summary>
    private async Task LoadQuestionsAsync()
    {
        IReadOnlyList<QuestionItem> items = [];
        if (IsWorkspace && path is string file)
        {
            try { items = await Task.Run(() => Questions.All(file)); }
            catch (Exception ex) when (IsDataError(ex) || ex is StoreException) { }
        }
        string? key = workspaceKey;
        var drafts = LoadDrafts();
        var order = items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToList();
        bool changed = false;
        for (int i = questionRows.Count - 1; i >= 0; i--)
            if (questionRows[i].WorkspaceKey != key || order.All(o => o.Id != questionRows[i].Item.Id)) { questionRows.RemoveAt(i); changed = true; }
        for (int i = 0; i < order.Count; i++)
        {
            var item = order[i];
            int at = -1;
            for (int j = 0; j < questionRows.Count; j++) if (questionRows[j].Item.Id == item.Id) { at = j; break; }
            if (at >= 0)
            {
                questionRows[at].Update(item);
                if (at != i) { questionRows.Move(at, i); changed = true; }
                continue;
            }
            var row = new QuestionRow(item, item.Open ? drafts?.Replies.GetValueOrDefault(item.Id) ?? "" : "", key);
            row.Edited += SaveReply;
            questionRows.Insert(Math.Min(i, questionRows.Count), row);
            changed = true;
        }
        foreach (var n in new[] { nameof(OpenQuestionCount), nameof(QuestionsLabel), nameof(QuestionsEmpty) }) Notify(n);
        if (changed) RaiseCommands();
    }

    private void SaveReply(QuestionRow row) => EditDrafts(row.WorkspaceKey, f =>
    {
        if (string.IsNullOrEmpty(row.ReplyText)) f.Replies.Remove(row.Item.Id); else f.Replies[row.Item.Id] = row.ReplyText;
    });

    private Task Reply(QuestionRow row)
    {
        // Everything the request needs is read now: a retry sends exactly this reply to exactly this question.
        string ws = path!, qid = row.Item.Id, text = row.ReplyText.Trim();
        string? key = row.WorkspaceKey;
        return Fail(async () =>
        {
            string? error = await Submit($"reply|{ws}|{qid}|{text}", ws, id => workspaces!.AnswerQuestionAsync(ws, id, qid, text));
            if (error == null) EditDrafts(key, f => f.Replies.Remove(qid));
            return error;
        });
    }

    private Task Say(string kind, string text)
    {
        string ws = path!, task = selectedTask!.Id, click = Guid.NewGuid().ToString("N"); // a retry of this click reuses its ID; a new click is a new message
        return Fail(() => Submit($"say|{kind}|{ws}|{task}|{click}", ws, id => workspaces!.FixtureSayAsync(ws, id, task, kind, text)));
    }

    // --- An unsent request for changes --------------------------------------------------------------------------
    // A draft belongs to one task and one output (revision). Typing on another revision starts a separate draft: an existing one is only
    // replaced, moved or removed by an explicit choice, and every change names the workspace it was made in.
    private int askCounter;
    private bool reanchor;
    private static string DraftKey(string task, string output) => task + "/" + output;

    /// <summary>Removes a draft, and the entry an earlier build filed under the task alone when it is the same draft.</summary>
    private static void ClearDraft(DraftFile f, string task, string output)
    {
        f.Reviews.Remove(DraftKey(task, output));
        if (f.Reviews.TryGetValue(task, out var legacy) && legacy.OutputId == output) f.Reviews.Remove(task);
    }

    private IEnumerable<ReviewDraft> TaskDrafts(DraftFile? file) =>
        selectedTask == null || file == null ? [] : file.Reviews.Values.Where(d => d.TaskId == selectedTask.Id);

    /// <summary>The draft for the revision on screen, read fresh from where it is kept.</summary>
    public ReviewDraft? CurrentDraft => Review is { HasOutput: true } r ? TaskDrafts(LoadDrafts()).FirstOrDefault(d => d.OutputId == r.OutputId) : null;
    /// <summary>The newest draft written for some other revision of the task on screen.</summary>
    public ReviewDraft? OlderDraft => Review is { HasOutput: true } r ? TaskDrafts(LoadDrafts()).Where(d => d.OutputId != r.OutputId).OrderByDescending(d => d.Revision).FirstOrDefault() : null;
    private ReviewDraft? shownDraft, shownOlder;

    public bool HasDraft => shownDraft != null;
    public bool HasOlderDraft => shownOlder != null;
    public bool DraftIsCurrent => HasDraft;
    public string DraftNotice => shownDraft is { } d ? $"You have an unsent request for changes on revision {d.Revision}: “{Shorten(d.Text)}”. Choose Request changes to continue it." : "";
    public string OlderDraftNotice => shownOlder is { } o && Review is { HasOutput: true } r
        ? $"You have an unsent comment written for revision {o.Revision}: “{Shorten(o.Text)}”. Revision {r.Revision} is on screen, so it is not attached to it. " +
          (HasDraft ? "You also have a draft for this revision, so discard one before moving it." : $"Move it to revision {r.Revision} or discard it.")
        : "";
    /// <summary>What the Request changes dialog starts with: the draft for this exact revision, read now. Another revision's draft is never loaded into it.</summary>
    public string RequestChangesInitialText => CurrentDraft?.Text ?? "";
    public WorkspaceCommand ReanchorDraftCommand { get; private set; } = null!;
    public WorkspaceCommand DiscardDraftCommand { get; private set; } = null!;
    public WorkspaceCommand DiscardOlderDraftCommand { get; private set; } = null!;

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..120] + "…";

    private void RefreshDraft() { shownDraft = CurrentDraft; shownOlder = OlderDraft; }

    /// <summary>
    /// Called when the Request changes dialog opens. The returned action keeps what is typed, anchored to the workspace, task, output and
    /// revision frozen now, and refreshes the inspector so it shows what was saved.
    /// </summary>
    public Action<string> FreezeDraftSaver()
    {
        string? key = workspaceKey;
        string task = selectedTask!.Id, output = Review!.OutputId!;
        int revision = Review.Revision;
        return text =>
        {
            EditDrafts(key, f =>
            {
                ClearDraft(f, task, output);
                if (!string.IsNullOrWhiteSpace(text)) f.Reviews[DraftKey(task, output)] = new(task, output, revision, text);
            });
            if (key == workspaceKey && selectedTask?.Id == task) { RefreshDraft(); NotifyDraft(); }
        };
    }

    // --- The persisted decision as its own badge ----------------------------------------------------------------
    public string DecisionBadge => Review is { Decided: true } r
        ? $"{(r.Decision == "changes_requested" ? "Changes requested" : "Accepted")} · revision {r.Revision} · {(r.DecisionBy ?? r.AcceptedBy) switch { "owner" => "You (workspace owner)", { } who => who, _ => "unknown" }}"
        : "";
    public bool HasDecisionBadge => DecisionBadge.Length > 0;

    private void InitConversation()
    {
        FollowLatestCommand = new(_ => true, _ => { FollowLatest = true; ChannelScrollToEnd?.Invoke(); return Task.CompletedTask; });
        CopyChannelCommand = new(_ => channelSelection.Count > 0 && Clipboard != null, _ => Clipboard!(ChannelCopyText));
        ShowQuestionsCommand = new(_ => IsWorkspace, _ => { SetCenter("questions"); return Task.CompletedTask; });
        ReplyQuestionCommand = new(p => p is QuestionRow { IsOpen: true } r && CanWrite && r.ReplyText.Trim().Length > 0, p => Reply((QuestionRow)p!));
        FixtureClaimCommand = new(_ => CanEdit, _ => Say("claim", $"“{selectedTask!.Title}” is done."));
        FixtureAskCommand = new(_ => CanEdit, _ => Say("question", $"Which audience is “{selectedTask!.Title}” for? (question {askCounter = Math.Max(questionRows.Count, askCounter) + 1})"));
        // Moving needs the newest revision on screen and no draft of its own there: the owner never loses text by moving another over it.
        ReanchorDraftCommand = new(_ => HasOlderDraft && !HasDraft, _ => { reanchor = true; releasePlace = true; return LoadReviewAsync(); });
        DiscardDraftCommand = new(_ => HasDraft, _ => RemoveDraft(shownDraft!));
        DiscardOlderDraftCommand = new(_ => HasOlderDraft, _ => RemoveDraft(shownOlder!));
        commands = [.. commands, FollowLatestCommand, CopyChannelCommand, ShowQuestionsCommand, ReplyQuestionCommand, FixtureClaimCommand, FixtureAskCommand, ReanchorDraftCommand, DiscardDraftCommand, DiscardOlderDraftCommand];
    }

    private Task RemoveDraft(ReviewDraft draft)
    {
        EditDrafts(f => ClearDraft(f, draft.TaskId, draft.OutputId));
        RefreshDraft(); NotifyDraft();
        return Task.CompletedTask;
    }

    /// <summary>After a load that opened the newest revision: an older draft the user chose to move is attached to it, when this revision has none.</summary>
    private void ApplyReanchor()
    {
        if (!reanchor) return;
        reanchor = false;
        if (selectedTask is not { } t || Review is not { HasOutput: true, IsStale: false } r || shownOlder is not { } d || shownDraft != null) return;
        EditDrafts(f =>
        {
            ClearDraft(f, d.TaskId, d.OutputId);
            f.Reviews[DraftKey(t.Id, r.OutputId!)] = new(t.Id, r.OutputId!, r.Revision, d.Text);
        });
        RefreshDraft();
    }

    private void NotifyDraft()
    {
        foreach (var n in new[] { nameof(HasDraft), nameof(HasOlderDraft), nameof(DraftIsCurrent), nameof(DraftNotice), nameof(OlderDraftNotice), nameof(RequestChangesInitialText), nameof(DecisionBadge), nameof(HasDecisionBadge) }) Notify(n);
        RaiseCommands();
    }
}
