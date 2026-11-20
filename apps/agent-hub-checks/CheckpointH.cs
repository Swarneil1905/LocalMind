using LocalMind.AgentHub;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

/// <summary>Day 3 checkpoint C: a busy channel that stays usable, questions that wait, claims kept apart from decisions, and drafts that survive.</summary>
internal static class CheckpointH
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static T One<T>(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar() is { } v and not DBNull ? (T)Convert.ChangeType(v, typeof(T)) : default!;
    }

    private static async Task Until(Func<bool> done, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(done))] string? what = null)
    {
        for (int i = 0; i < 200 && !done(); i++) await Task.Delay(30);
        if (!done()) throw new InvalidOperationException($"timed out waiting for: {what}");
    }

    private static WorkerError Fault(Action act)
    {
        try { act(); }
        catch (Exception e) when (e.GetType().Name == "StoreException") { return (WorkerError)e.GetType().GetProperty("Error")!.GetValue(e)!; }
        throw new InvalidOperationException("expected a store error");
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static int n;
    private static string C() => "h" + ++n;

    private static (HubViewModel Hub, FakeWorker Fake) NewHub(DraftStore? drafts = null)
    {
        var fake = new FakeWorker();
        return (new HubViewModel { Workspaces = new WorkspaceService(new WorkerClient(fake)), PickFile = () => Task.FromResult<string?>(null), Drafts = drafts }, fake);
    }

    /// <summary>Development-only (the Checkpoint C walkthrough): Draft waiting for a decision after a claim, two similar questions, and a first stretch of progress.</summary>
    public static string MakeHandoffC(string folder)
    {
        var info = ProjectStore.Create(folder, "Atlas", NewId);
        string db = info.DatabasePath;
        string draft = TaskStore.CreateTask(db, C(), info.ProjectId, "Draft", "Write the launch note", NewId).Id;
        string qa = TaskStore.CreateTask(db, C(), info.ProjectId, "QA", null, NewId).Id;
        TaskStore.AddDependency(db, C(), qa, draft, 0);
        TaskStore.UpdateTask(db, C(), draft, 0, null, null, null, reviewRequired: true);
        TaskStore.RunRole(db, C(), draft, "builder", null, NewId);
        TaskStore.FixtureSay(db, C(), draft, "claim", "“Draft” is done.", NewId);
        TaskStore.RunRole(db, C(), draft, "reviewer", null, NewId);
        TaskStore.FixtureSay(db, C(), draft, "question", "Which audience is “Draft” for? (question 1)", NewId);
        TaskStore.FixtureSay(db, C(), draft, "question", "Which audience is “Draft” for? (question 2)", NewId);
        for (int i = 0; i < 40; i++) TaskStore.FixtureSay(db, C(), draft, "progress", $"Working on the launch note, step {i + 1}", NewId);
        return db;
    }

    /// <summary>Development-only: writes progress chunks into a workspace at a steady pace, like a streaming reply.</summary>
    public static async Task Stream(string workspace, string title, int count, int delayMs)
    {
        string db = Directory.Exists(workspace) ? Path.Combine(workspace, "agenthub.db") : workspace;
        string task = One<string>(db, $"SELECT id FROM tasks WHERE title='{title}' LIMIT 1");
        for (int i = 0; i < count; i++)
        {
            TaskStore.FixtureSay(db, "stream-" + Guid.NewGuid().ToString("N"), task, "progress", $"Streamed chunk {i + 1} of {count}", NewId);
            await Task.Delay(delayMs);
        }
    }

    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Store(Path.Combine(dir, "store"));
        await Claims(Path.Combine(dir, "claims"));
        await Filters(Path.Combine(dir, "filters"));
        await Streaming(Path.Combine(dir, "streaming"));
        await Questions_(Path.Combine(dir, "questions"));
        await Stable(Path.Combine(dir, "stable"));
        await Evicted(Path.Combine(dir, "evicted"));
        await Drafts(Path.Combine(dir, "drafts"));
    }

    // The store: speech, questions, answers -------------------------------------------------------------------------------------
    private static void Store(string dir)
    {
        var info = ProjectStore.Create(dir, "Atlas", NewId);
        string db = info.DatabasePath;
        string task = TaskStore.CreateTask(db, C(), info.ProjectId, "Draft", null, NewId).Id;
        Check(One<long>(db, "SELECT count(*) FROM pragma_table_info('questions')") == 11, "a new workspace has the questions table");

        var q1 = TaskStore.FixtureSay(db, C(), task, "question", "Which audience is it for?", NewId);
        var q2 = TaskStore.FixtureSay(db, C(), task, "question", "Which audience is it for?", NewId);
        Check(q1.Id != q2.Id && Questions.All(db).Count(q => q.Open) == 2, "two questions with the same words get their own durable IDs and both wait");
        Check(Fault(() => TaskStore.AnswerQuestion(db, C(), q1.Id, "Engineers", "builder (fixture)")).Code == "not_authorized", "an agent cannot answer a question");
        var first = TaskStore.AnswerQuestion(db, "reply-1", q1.Id, "Engineers", "owner");
        var again = TaskStore.AnswerQuestion(db, "reply-1", q1.Id, "Something else", "owner");
        Check(!first.Duplicate && again.Duplicate && One<string>(db, $"SELECT answer FROM questions WHERE id='{q1.Id}'") == "Engineers" && One<long>(db, "SELECT count(*) FROM events WHERE kind='question_answered'") == 1,
              "the same reply sent twice under one command ID resolves the question once");
        Check(Fault(() => TaskStore.AnswerQuestion(db, C(), q1.Id, "A second answer", "owner")).Code == "already_answered", "an answer under a new command ID is refused: the question is already resolved");
        var items = Questions.All(db);
        Check(items.Single(q => q.Id == q1.Id).Answer == "Engineers" && items.Single(q => q.Id == q2.Id).Open, "answering one question leaves the similar one waiting");
        Check(Fault(() => TaskStore.FixtureSay(db, C(), task, "shout", "x", NewId)).Code == "unknown_kind" && Fault(() => TaskStore.FixtureSay(db, C(), task, "claim", "  ", NewId)).Code == "invalid_text",
              "a fixture can only claim, report progress or ask, with real text");
    }

    // AH88 · a claim is not an acceptance ------------------------------------------------------------------------------------
    private static async Task Claims(string folder)
    {
        var (hub, _) = NewHub();
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        await hub.CreateTaskAsync("Draft", "A note");
        await hub.CreateTaskAsync("QA", "");
        var draft = hub.Tasks.Single(t => t.Title == "Draft");
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.AddPrerequisiteAsync(draft.Id);
        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Draft");
        await hub.ToggleReviewCommand.RunAsync();
        await Until(() => hub.SelectedTask?.ReviewRequired == true);
        await hub.AskBuilderCommand.RunAsync();
        await Until(() => hub.HasOutput);
        await hub.FixtureClaimCommand.RunAsync();   // the fixture says it is done, before any decision
        await hub.AskReviewerCommand.RunAsync();     // then the reviewer recommends
        await Until(() => hub.HasFeedback);
        await hub.PollAsync();
        await Until(() => hub.ChannelRows.Any(r => r.IsClaim));
        var claim = hub.ChannelRows.Single(r => r.IsClaim);
        Check(claim.Who.Contains("claim, not an acceptance") && claim.Text.Contains("is done") && claim.Message.NeedsAttention == false && !claim.HasBadge, "the fixture's \"done\" is shown as a claim and carries no decision badge");

        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "QA");
        await hub.MoveTaskCommand.RunAsync("in_progress");
        Check(hub.Notice is { } blocked && blocked.Contains("awaiting your review") && hub.Tasks.Single(t => t.Title == "QA").Status == "open", "after the claim and a recommendation to approve, QA still waits for the owner");

        hub.SelectedTask = hub.Tasks.Single(t => t.Title == "Draft");
        await Until(() => hub.CanDecide);
        await hub.AcceptOutputCommand.RunAsync();
        await Until(() => hub.Review?.AcceptedBy != null);
        string badge = hub.DecisionBadge;
        Check(badge == "Accepted · revision 1 · You (workspace owner)" && hub.HasDecisionBadge, "the persisted decision has its own badge: outcome, exact revision and who decided");
        await hub.PollAsync();
        await Until(() => hub.ChannelRows.Any(r => r.HasBadge));
        var row = hub.ChannelRows.Single(r => r.HasBadge);
        Check(row.Badge == badge && row.Message.Revision == 1 && row.Message.DecisionId != null && One<string>(db, $"SELECT id FROM decisions WHERE output_id='{row.Message.OutputId}'") == row.Message.DecisionId,
              "the channel's badge is linked to the decision record and to the revision it was about");

        // Open the badge from somewhere else, after the owner has moved on to another revision, and after a restart.
        TaskStore.RunRole(db, C(), draft.Id, "builder", null, NewId); // revision 2 arrives
        await hub.ReleaseWriteLockAsync();
        var (again, _) = NewHub();
        await again.OpenWorkspaceFolderAsync(folder);
        await Until(() => again.ChannelRows.Any(r => r.HasBadge));
        await again.OpenChannelTaskCommand.RunAsync(again.ChannelRows.Single(r => r.HasBadge));
        await Until(() => again.SelectedTask?.Id == draft.Id && again.Review?.OutputId != null);
        Check(again.Review is { Revision: 1, AcceptedBy: "owner" } && again.DecisionBadge == badge && again.Review.IsStale, "after a restart the badge opens the exact revision that was decided, not the newest one");
        await again.ReleaseWriteLockAsync();
    }

    // AH35 · filters, a burst around one decision, drafts and place -----------------------------------------------------------
    private static async Task Filters(string folder)
    {
        string drafts = Path.Combine(folder, "..", "filters-drafts");
        var (hub, _) = NewHub(new DraftStore(drafts));
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        string alpha = One<string>(db, "SELECT id FROM projects");
        string beta = NewId();
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString())) { c.Open(); var cmd = c.CreateCommand(); cmd.CommandText = $"INSERT INTO projects VALUES('{beta}','Beta')"; cmd.ExecuteNonQuery(); }
        string task = TaskStore.CreateTask(db, C(), alpha, "Draft", "A note", NewId).Id;
        string other = TaskStore.CreateTask(db, C(), beta, "Draft", "A note", NewId).Id;
        TaskStore.UpdateTask(db, C(), task, 0, null, null, null, reviewRequired: true);
        var o1 = TaskStore.RunRole(db, C(), task, "builder", null, NewId);
        for (int i = 0; i < 40; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"before {i}", NewId);
        var ask = TaskStore.FixtureSay(db, C(), task, "question", "Which audience?", NewId);
        for (int i = 0; i < 20; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"around {i}", NewId);
        TaskStore.DecideOutput(db, C(), task, o1.Id, 1, 1, "changes_requested", "Add a summary.", "owner", NewId);
        for (int i = 0; i < 40; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"after {i}", NewId);
        for (int i = 0; i < 10; i++) TaskStore.FixtureSay(db, C(), other, "progress", $"beta {i}", NewId);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.ChannelRows.Count > 100 && hub.QuestionRows.Count == 1);
        int all = hub.ChannelRows.Count;
        Check(hub.ChannelRows.All(r => r.Message.ProjectId == alpha) && !hub.ChannelRows.Any(r => r.Text.StartsWith("beta")), "the channel is scoped to the selected project");

        hub.ChannelFilterIndex = 1;
        Check(hub.ChannelRows.Count == all - 100 && !hub.ChannelRows.Any(r => r.Message.Class == "progress") && hub.ChannelRows.Any(r => r.Message.Kind == "changes_requested") && hub.ChannelFilterNote.Contains("100 updates"),
              "hiding progress removes the 100 transient updates and nothing else, and says how many are hidden");
        hub.ChannelFilterIndex = 2;
        Check(hub.ChannelRows.Count == 2 && hub.ChannelRows.Select(r => r.Message.Kind).Order().SequenceEqual(["changes_requested", "question_asked"]) && hub.ChannelFilterNote.Contains("other messages hidden"),
              "decisions and questions alone leave exactly the decision and the question, with the rest counted");

        // Place: the reader's anchor survives a filter change by sequence number.
        hub.ChannelFilterIndex = 0;
        long decisionSeq = hub.ChannelRows.Single(r => r.Message.Kind == "changes_requested").Message.Seq;
        long? restored = null;
        hub.ChannelAnchorRestore += seq => restored = seq;
        hub.ChannelAnchorSeq = decisionSeq - 5; // the reader was looking at some progress just before the decision
        hub.ChannelFilterIndex = 2;
        Check(restored == decisionSeq && hub.ChannelAnchorSeq == decisionSeq, "after the filter change the reader is put at the nearest message at or after where they were, and it is the decision");

        // Findable before and after a reconnect.
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.ChannelRows.Count == 2);
        Check(hub.ChannelFilterIndex == 2 && hub.ChannelRows.Count(r => r.Message.Kind == "changes_requested") == 1, "after a reconnect the filter holds and the decision is still found once");

        // A pending reply draft survives filter changes, a reconnect, a project switch and a restart.
        hub.ShowQuestionsCommand.Execute(null);
        var row = hub.QuestionRows.Single();
        row.ReplyText = "Engineers, mostly";
        hub.ChannelFilterIndex = 0;
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.QuestionRows.Count == 1);
        Check(hub.QuestionRows[0].ReplyText == "Engineers, mostly", "the half-typed reply is still there after a filter change and a reconnect");
        hub.SelectedProject = hub.Projects.Single(p => p.Name == "Beta");
        await Until(() => hub.ProjectHeading == "Beta" && hub.ChannelRows.All(r => r.Message.ProjectId == beta) && hub.ChannelRows.Count > 0);
        hub.SelectedProject = hub.Projects.Single(p => p.Name == "Atlas");
        await Until(() => hub.ProjectHeading == "Atlas" && hub.ChannelRows.Any(r => r.Message.Kind == "question_asked"));
        Check(hub.QuestionRows[0].ReplyText == "Engineers, mostly" && hub.ChannelRows.All(r => r.Message.ProjectId == alpha), "the reply draft and the project scope hold across a project switch");
        await hub.ReleaseWriteLockAsync();
        var (again, _) = NewHub(new DraftStore(drafts));
        await again.OpenWorkspaceFolderAsync(folder);
        await Until(() => again.QuestionRows.Count == 1 && again.ChannelRows.Any(r => r.Message.Kind == "changes_requested"));
        Check(again.QuestionRows[0].ReplyText == "Engineers, mostly" && again.ChannelFilterIndex == 0 && again.ChannelRows.Count(r => r.Message.Kind == "changes_requested") == 1, "after a restart the draft is back and the decision is still findable");

        // Jump to the relevant card.
        await again.OpenChannelTaskCommand.RunAsync(again.ChannelRows.Single(r => r.Message.Kind == "question_asked"));
        await Until(() => again.SelectedTask?.Id == task);
        Check(again.BoardVisible && again.SelectedTask?.Id == task, "Go to card from the question message selects its task");
        await again.ReleaseWriteLockAsync();
    }

    // AH87 · replies stream and nothing the reader is doing moves ---------------------------------------------------------------
    private static async Task Streaming(string folder)
    {
        var (hub, _) = NewHub();
        hub.ChannelCap = 150;
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        string task = TaskStore.CreateTask(db, C(), One<string>(db, "SELECT id FROM projects"), "Draft", null, NewId).Id;
        for (int i = 0; i < 30; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"chunk {i:000}", NewId);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.ChannelRows.Count >= 31);
        var before = hub.ChannelRows.Take(10).ToList();
        var picked = hub.ChannelRows.Skip(3).Take(2).ToList();
        hub.ChannelSelection = picked;
        hub.ChannelAnchorSeq = picked[0].Message.Seq;
        hub.FollowLatest = false; // the reader scrolled up
        string expected = $"{picked[0].Who} · {picked[0].When}\n{picked[0].Text}\n\n{picked[1].Who} · {picked[1].When}\n{picked[1].Text}";
        string? copied = null;
        hub.Clipboard = t => { copied = t; return Task.CompletedTask; };
        bool scrolled = false;
        hub.ChannelScrollToEnd += () => scrolled = true;

        // A deterministic stream: ten chunks, then a look, five times.
        int total = hub.ChannelRows.Count;
        for (int burst = 0; burst < 5; burst++)
        {
            for (int i = 0; i < 10; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"stream {burst}-{i}", NewId);
            await hub.PollAsync();
            total += 10;
            await Until(() => hub.ChannelRows.Count == total);
            Check(hub.ChannelSelection.SequenceEqual(picked) && hub.ChannelAnchorSeq == picked[0].Message.Seq && hub.ChannelRows.Take(10).SequenceEqual(before) && !scrolled && !hub.FollowLatest,
                  $"after burst {burst + 1} the selection, the reader's place and every earlier row are exactly as they were, and nothing scrolled");
        }
        Check(hub.FollowLabel == "Follow latest (50 new)" && hub.ShowFollowButton, "the reader is told how much arrived while they were reading");
        await hub.CopyChannelCommand.RunAsync();
        Check(copied == expected && copied == hub.ChannelCopyText && copied.Contains(picked[0].Text) && copied.Contains(picked[1].Text) && !copied.Contains(hub.ChannelRows[5].Text), "copying gives the selected rows' visible text, in order, and nothing else");

        await hub.FollowLatestCommand.RunAsync();
        Check(hub.FollowLatest && scrolled && hub.FollowLabel == "Follow latest", "Follow latest resumes following and asks the window to show the newest message");

        // Bounded: following with nothing selected drops the oldest rows; a selection protects its rows.
        hub.ChannelSelection = [];
        long oldest = hub.ChannelRows[0].Message.Seq;
        for (int i = 0; i < 120; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"more {i}", NewId);
        await hub.PollAsync();
        await Until(() => hub.ChannelRows.Any(r => r.Text == "more 119"));
        Check(hub.ChannelRows.Count <= hub.ChannelCap + hub.ChannelCap / 10 && hub.ChannelRows[0].Message.Seq > oldest, "the list stays bounded while following: the oldest rows are dropped");
        var kept = hub.ChannelRows[0];
        hub.ChannelSelection = [kept];
        for (int i = 0; i < 400; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"flood {i}", NewId);
        await hub.PollAsync();
        await Until(() => hub.ChannelRows.Any(r => r.Text == "flood 399"));
        Check(hub.ChannelRows.Contains(kept), "a row the reader has selected is never dropped from under them");
        await hub.ReleaseWriteLockAsync();
    }

    // AH89 · questions wait for an answer in their own place ------------------------------------------------------------------
    private static async Task Questions_(string folder)
    {
        string drafts = Path.Combine(folder, "..", "questions-drafts");
        var (hub, fake) = NewHub(new DraftStore(drafts));
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        await hub.CreateTaskAsync("Draft", "A note");
        hub.SelectedTask = hub.Tasks.Single();
        await hub.FixtureAskCommand.RunAsync();
        await hub.FixtureAskCommand.RunAsync();
        await Until(() => hub.QuestionRows.Count == 2);
        Check(hub.OpenQuestionCount == 2 && hub.QuestionsLabel == "Questions (2)" && hub.QuestionRows[0].Item.Id != hub.QuestionRows[1].Item.Id, "two similar questions sit in their own list, each with its own ID, outside the transcript");
        Check(hub.QuestionRows.Select(r => r.Item.Text).Distinct().Count() == 2 && hub.QuestionRows.All(r => r.Item.Text.Contains("Which audience")), "they read alike but stay separately actionable");

        // Reply after switching views and reopening.
        hub.ShowChannelCommand.Execute(null);
        hub.ShowBoardCommand.Execute(null);
        hub.ShowQuestionsCommand.Execute(null);
        var first = hub.QuestionRows[0];
        first.ReplyText = "Engineers";
        await hub.ReleaseWriteLockAsync();
        var (again, fake2) = NewHub(new DraftStore(drafts));
        await again.OpenWorkspaceFolderAsync(folder);
        await Until(() => again.QuestionRows.Count == 2 && again.QuestionRows.Any(r => r.ReplyText == "Engineers"));
        var row = again.QuestionRows.Single(r => r.ReplyText == "Engineers");
        string id = row.Item.Id;

        // The acknowledgement is lost, and the owner sends the same reply twice.
        fake2.AfterHandle = req => req.Op == "answerQuestion" ? throw new WorkerException(new("outcome_unknown", "The worker stopped while handling the request, so it may or may not have been applied.", "Repeat the action.")) : Task.CompletedTask;
        await again.ReplyQuestionCommand.RunAsync(row);
        fake2.AfterHandle = null;
        Check(again.Notice is { } lost && lost.Contains("may or may not") && One<long>(db, "SELECT count(*) FROM events WHERE kind='question_answered'") == 1, "the first reply was recorded although its answer was lost");
        await again.NoticeCommand!.RunAsync();   // Try again: the same command ID
        await Until(() => again.QuestionRows.Count(r => r.IsOpen) == 1);
        Check(One<long>(db, "SELECT count(*) FROM events WHERE kind='question_answered'") == 1 && One<string>(db, $"SELECT answer FROM questions WHERE id='{id}'") == "Engineers"
              && again.QuestionRows.Single(r => r.Item.Id == id).Status == "You answered: Engineers" && again.QuestionRows.Count(r => r.IsOpen) == 1 && again.OpenQuestionCount == 1,
              "the same reply sent twice resolves the question once; the similar question is still waiting");
        Check(Fault(() => TaskStore.AnswerQuestion(db, C(), id, "Again", "owner")).Code == "already_answered", "a later answer to the resolved question is refused");
        await again.ReleaseWriteLockAsync();
        var (third, _) = NewHub(new DraftStore(drafts));
        await third.OpenWorkspaceFolderAsync(folder);
        await Until(() => third.QuestionRows.Count == 2);
        Check(third.OpenQuestionCount == 1 && third.QuestionRows.Single(r => r.Item.Id == id).Item.Answer == "Engineers" && string.IsNullOrEmpty(third.QuestionRows.Single(r => r.Item.Id == id).ReplyText),
              "after another restart the answered question stays answered and the other one stays open");
        await third.ReleaseWriteLockAsync();
    }

    // AH91 · an unsent request for changes keeps its anchor ----------------------------------------------------------------------
    private static async Task Drafts(string folder)
    {
        string drafts = Path.Combine(folder, "..", "review-drafts");
        var (hub, fake) = NewHub(new DraftStore(drafts));
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        await hub.CreateTaskAsync("Draft", "A note");
        hub.SelectedTask = hub.Tasks.Single();
        string task = hub.SelectedTask!.Id;
        await hub.ToggleReviewCommand.RunAsync();
        await Until(() => hub.SelectedTask?.ReviewRequired == true);
        await hub.AskBuilderCommand.RunAsync();
        await Until(() => hub.CanDecide);

        // Type, close without sending, reopen at once: what was typed is what the dialog starts with, and the inspector shows it.
        Check(hub.RequestChangesInitialText == "" && !hub.HasDraft, "a revision with no draft opens an empty editor");
        var save = hub.FreezeDraftSaver();
        save("Please preserve my paragraph.");
        Check(hub.RequestChangesInitialText == "Please preserve my paragraph." && hub.HasDraft && hub.DraftNotice.Contains("Please preserve my paragraph."), "closing and immediately reopening gives back the text just typed, and the inspector shows it");
        save("Please preserve my paragraph. And a second one.");
        Check(hub.RequestChangesInitialText == "Please preserve my paragraph. And a second one.", "continuing to edit and reopening again keeps the latest text");
        await hub.ReleaseWriteLockAsync();
        var (back, fake2) = NewHub(new DraftStore(drafts));
        await back.OpenWorkspaceFolderAsync(folder);
        back.SelectedTask = back.Tasks.Single();
        await Until(() => back.Review?.OutputId != null && back.HasDraft);
        Check(back.RequestChangesInitialText == "Please preserve my paragraph. And a second one." && back.DraftNotice.Contains("revision 1"), "after a restart the unsent comment is back, attached to revision 1, ready to continue");

        // Revision 2 arrives. The old draft stays until the owner chooses; the dialog entry point never replaces it.
        TaskStore.RunRole(db, C(), task, "builder", null, NewId);
        await back.RefreshCommand.RunAsync();
        await Until(() => back.Review?.LatestRevision == 2);
        await back.OpenLatestRevisionCommand.RunAsync();
        await Until(() => back.Review?.Revision == 2 && back.CanDecide);
        Check(back.HasOlderDraft && !back.HasDraft && back.RequestChangesInitialText == "" && back.OlderDraftNotice.Contains("revision 1") && back.OlderDraftNotice.Contains("revision 2") && back.OlderDraft!.Revision == 1,
              "on revision 2 the revision 1 comment is offered as an older draft, and Request changes opens an empty editor rather than loading it");
        var typed = back.FreezeDraftSaver();          // the actual dialog entry point, on revision 2
        typed("A new thought for revision 2");
        Check(back.CurrentDraft is { Revision: 2, Text: "A new thought for revision 2" } && back.OlderDraft is { Revision: 1, Text: "Please preserve my paragraph. And a second one." },
              "typing on revision 2 starts a separate draft; the revision 1 text is untouched");
        Check(!back.ReanchorDraftCommand.CanExecute(null) && back.OlderDraftNotice.Contains("discard one before moving it"), "moving the old draft is not offered while it would replace newer text");
        await back.ReleaseWriteLockAsync();
        var (again, _) = NewHub(new DraftStore(drafts));
        await again.OpenWorkspaceFolderAsync(folder);
        again.SelectedTask = again.Tasks.Single();
        await Until(() => again.Review?.OutputId != null && again.HasDraft);
        Check(again.CurrentDraft!.Text == "A new thought for revision 2" && again.OlderDraft!.Text == "Please preserve my paragraph. And a second one.", "after a restart both drafts are there, each with its own revision");

        // An explicit choice: discard the newer one, then move the older one.
        await again.DiscardDraftCommand.RunAsync();
        Check(!again.HasDraft && again.HasOlderDraft && again.ReanchorDraftCommand.CanExecute(null), "discarding the newer draft leaves the old one and makes moving it possible");
        await again.ReanchorDraftCommand.RunAsync();
        await Until(() => again.HasDraft && !again.HasOlderDraft);
        Check(again.CurrentDraft is { Revision: 2, Text: "Please preserve my paragraph. And a second one." } && again.RequestChangesInitialText == "Please preserve my paragraph. And a second one.", "moving attaches the old text to revision 2, deliberately");
        TaskStore.RunRole(db, C(), task, "builder", null, NewId);   // revision 3
        await again.RefreshCommand.RunAsync();
        await Until(() => again.Review?.LatestRevision == 3);
        Check(again.HasDraft && !again.HasOlderDraft && again.CurrentDraft!.Revision == 2, "while revision 2 stays on screen, its draft is still attached to it");
        await again.OpenLatestRevisionCommand.RunAsync();
        await Until(() => again.Review?.Revision == 3 && again.HasOlderDraft);
        await again.DiscardOlderDraftCommand.RunAsync();
        Check(!again.HasOlderDraft && again.CurrentDraft == null && again.OlderDraft == null, "discarding the older draft removes it");
        await again.ReleaseWriteLockAsync();

        // A draft is saved to the workspace it was written in, even if another one is open when the save arrives.
        string other = Path.Combine(folder, "..", "other-workspace");
        var (late, _) = NewHub(new DraftStore(drafts));
        await late.OpenWorkspaceFolderAsync(folder);
        late.SelectedTask = late.Tasks.Single();
        await Until(() => late.Review?.OutputId != null);
        var lateSave = late.FreezeDraftSaver();
        ProjectStore.Create(other, "Elsewhere", NewId); // real, unique workspace IDs (the in-process fake worker numbers its IDs from 1 in every instance)
        await late.OpenWorkspaceFolderAsync(other);
        lateSave("written in Atlas");
        var store = new DraftStore(drafts);
        string atlasKey = One<string>(db, "SELECT id FROM workspace");
        string otherKey = One<string>(Path.Combine(other, "agenthub.db"), "SELECT id FROM workspace");
        Check(store.Load(atlasKey).Reviews.Values.Any(d => d.Text == "written in Atlas") && !store.Load(otherKey).Reviews.Any() && !late.HasDraft, "a draft saved after switching workspace lands in the workspace it was written in, and nowhere else");
        await late.ReleaseWriteLockAsync();

        // Request changes twice after an interrupted acknowledgement: one submitted review, and only the frozen draft is cleared.
        var (final, fake3) = NewHub(new DraftStore(drafts));
        await final.OpenWorkspaceFolderAsync(folder);
        final.SelectedTask = final.Tasks.Single();
        await Until(() => final.Review?.OutputId != null && final.CanDecide);
        var submit = final.FreezeRequestChanges();
        var keep = final.FreezeDraftSaver();
        keep("Needs a summary");
        fake3.AfterHandle = req => req.Op == "decideOutput" ? throw new WorkerException(new("outcome_unknown", "The worker stopped while handling the request, so it may or may not have been applied.", "Repeat the action.")) : Task.CompletedTask;
        string? first = await submit("Needs a summary");
        fake3.AfterHandle = null;
        string? second = await submit("Needs a summary");
        Check(first != null && first.Contains("may or may not") && second == null, "the first send's answer was lost; sending again succeeds");
        Check(One<long>(db, "SELECT count(*) FROM decisions WHERE decision='changes_requested' AND content_revision=3") == 1 && One<long>(db, "SELECT count(*) FROM reviews WHERE reviewer_role='owner' AND recommendation='request_changes'") == 1
              && One<long>(db, "SELECT count(*) FROM events WHERE kind='changes_requested' AND task_id='" + task + "'") == 1, "requesting changes twice after the interruption gives one decision, one review and one event");
        Check(final.CurrentDraft == null, "a sent comment no longer exists as a draft");
        await final.ReleaseWriteLockAsync();
    }

    // AH35/AH87 · decisions and questions stay discoverable after progress fills the channel -----------------------------------
    private static async Task Evicted(string folder)
    {
        var (hub, _) = NewHub();
        hub.ChannelCap = 20;
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        string task = TaskStore.CreateTask(db, C(), One<string>(db, "SELECT id FROM projects"), "Draft", "A note", NewId).Id;
        TaskStore.UpdateTask(db, C(), task, 0, null, null, null, reviewRequired: true);
        var o1 = TaskStore.RunRole(db, C(), task, "builder", null, NewId);
        TaskStore.FixtureSay(db, C(), task, "question", "Which audience?", NewId);
        TaskStore.DecideOutput(db, C(), task, o1.Id, 1, 1, "changes_requested", "Add a summary.", "owner", NewId);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.ChannelRows.Any(r => r.Message.Kind == "changes_requested") && hub.QuestionRows.Count == 1);
        hub.ChannelFilterIndex = 2;
        Check(hub.ChannelRows.Count == 2, "before the burst the filter shows the decision and the question");

        hub.ChannelFilterIndex = 0;
        for (int i = 0; i < 50; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"burst {i}", NewId);
        await hub.PollAsync();
        await Until(() => hub.ChannelRows.Any(r => r.Text == "burst 49"));
        Check(!hub.ChannelRows.Any(r => r.Message.Kind is "changes_requested" or "question_asked") && hub.ChannelRows.Count <= 22 && hub.OpenQuestionCount == 1,
              "the burst pushed the decision and the question out of the rows this window keeps, and the list is bounded");
        hub.ChannelFilterIndex = 2;
        await Until(() => hub.ChannelRows.Count == 2);
        Check(hub.ChannelRows.Select(r => r.Message.Kind).Order().SequenceEqual(["changes_requested", "question_asked"]) && hub.ChannelFilterNote.Contains("other messages hidden") && hub.ChannelRows.Count <= 22,
              "Decisions and questions then finds both again, read from the event store, and says how much is hidden");
        hub.ChannelFilterIndex = 1;
        await Until(() => hub.ChannelRows.Any(r => r.Message.Kind == "changes_requested"));
        Check(hub.ChannelRows.Count <= 22 && !hub.ChannelRows.Any(r => r.Message.Class == "progress") && hub.ChannelRows.Any(r => r.Message.Kind == "question_asked"), "Hide progress also reaches the old decision and question, with no progress and still bounded");

        // New progress while a filter is applied does not push them out.
        hub.ChannelFilterIndex = 2;
        await Until(() => hub.ChannelRows.Count == 2);
        for (int i = 0; i < 50; i++) TaskStore.FixtureSay(db, C(), task, "progress", $"later {i}", NewId);
        await hub.PollAsync();
        await Until(() => hub.ChannelRows.Count == 2 && hub.ChannelFilterNote.Contains("other messages hidden"));
        Check(hub.ChannelRows.Select(r => r.Message.Kind).Order().SequenceEqual(["changes_requested", "question_asked"]), "with the filter applied, later progress leaves both items in place");

        await hub.ReleaseWriteLockAsync();
        var (again, _) = NewHub();
        again.ChannelCap = 20;
        await again.OpenWorkspaceFolderAsync(folder);
        await Until(() => again.ChannelRows.Any(r => r.Text == "later 49"));
        again.ChannelFilterIndex = 2;
        await Until(() => again.ChannelRows.Count == 2);
        Check(again.ChannelRows.Select(r => r.Message.Kind).Order().SequenceEqual(["changes_requested", "question_asked"]) && again.OpenQuestionCount == 1, "after reopening, with the same small cap, the decision and the question are still reachable through the filter");
        await again.ReleaseWriteLockAsync();
    }

    // AH89 · a reply box is not rebuilt by unrelated events ---------------------------------------------------------------------
    private static async Task Stable(string folder)
    {
        var (hub, _) = NewHub(new DraftStore(Path.Combine(folder, "..", "stable-drafts")));
        await hub.CreateWorkspaceAsync("Atlas", folder);
        string db = Path.Combine(folder, "agenthub.db");
        string task = TaskStore.CreateTask(db, C(), One<string>(db, "SELECT id FROM projects"), "Draft", "A note", NewId).Id;
        var q1 = TaskStore.FixtureSay(db, C(), task, "question", "Which audience? (question 1)", NewId);
        await hub.RefreshCommand.RunAsync();
        await Until(() => hub.QuestionRows.Count == 1);
        var row = hub.QuestionRows[0];
        row.ReplyText = "Engineers, probably";
        int listChanges = 0, rowChanges = 0;
        hub.QuestionRows.CollectionChanged += (_, _) => listChanges++;
        row.PropertyChanged += (_, _) => rowChanges++;

        for (int i = 0; i < 3; i++)
        {
            TaskStore.FixtureSay(db, C(), task, "progress", $"tick {i}", NewId);
            await hub.PollAsync();
        }
        Check(ReferenceEquals(hub.QuestionRows[0], row) && hub.QuestionRows.Count == 1 && listChanges == 0 && rowChanges == 0 && row.ReplyText == "Engineers, probably",
              "unrelated progress events and polls leave the question's row, its text and the list exactly as they were: nothing is raised");

        // A genuine change is applied promptly, to the same row.
        TaskStore.AnswerQuestion(db, C(), q1.Id, "Someone else answered", "owner");
        await hub.PollAsync();
        Check(ReferenceEquals(hub.QuestionRows[0], row) && !row.IsOpen && row.Status == "You answered: Someone else answered" && hub.OpenQuestionCount == 0 && rowChanges > 0 && row.ReplyText == "",
              "when the question really is answered, the same row is updated at once");

        // A new question is added without disturbing the one being typed into.
        var q2 = TaskStore.FixtureSay(db, C(), task, "question", "Which audience? (question 2)", NewId);
        await hub.PollAsync();
        await Until(() => hub.QuestionRows.Count == 2);
        var second = hub.QuestionRows[1];
        second.ReplyText = "Typing here";
        int before = listChanges;
        TaskStore.FixtureSay(db, C(), task, "progress", "unrelated", NewId);
        await hub.PollAsync();
        Check(ReferenceEquals(hub.QuestionRows[0], row) && ReferenceEquals(hub.QuestionRows[1], second) && listChanges == before && hub.QuestionRows[1].Item.Id == q2.Id && second.ReplyText == "Typing here" && hub.OpenQuestionCount == 1,
              "a question added later joins the list in order, and later unrelated events leave both rows alone");
        await hub.ReleaseWriteLockAsync();
    }
}
