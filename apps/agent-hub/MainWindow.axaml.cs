using Avalonia.Input.Platform;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

public partial class MainWindow : Window
{
    private readonly HubViewModel hub = new();
    private Control? lastFocus;
    private ListBox[] columns = [];
    private bool syncing;
    private ListBox ColumnWith(TaskItem task) => columns.FirstOrDefault(l => l.ItemsSource is IEnumerable<TaskItem> items && items.Contains(task)) ?? columns[0];

    public MainWindow()
    {
        InitializeComponent();
        hub.PickFile = PickFileAsync;
        hub.PickFolder = PickFolderAsync;
        hub.Workspaces = new WorkspaceService(ConnectAsync);
        hub.ShowNewWorkspace = () => new NewWorkspaceDialog(hub.CreateWorkspaceAsync, PickFolderAsync).ShowDialog<bool>(this).ContinueWith(_ => RestoreFocus(), TaskScheduler.FromCurrentSynchronizationContext());
        Closed += async (_, _) => { hub.StopPolling(); await hub.ReleaseWriteLockAsync(); if (worker != null) await worker.DisposeAsync(); };
        DataContext = hub;
        // Another window may be writing: look for new events every couple of seconds, without any action from the user.
        hub.StartPolling(TimeSpan.FromSeconds(1));
        hub.Drafts = new DraftStore(DraftStore.DefaultFolder);
        hub.Layouts = new LayoutStore(LayoutStore.DefaultFolder);
        hub.Clipboard = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        WireChannel();
        hub.PreviewShown += () => Dispatcher.UIThread.Post(() => { SendPreviewButton.BringIntoView(); SendPreviewButton.Focus(); }, DispatcherPriority.Loaded);
        var keys = Shortcuts.For(OperatingSystem.IsMacOS());
        Bind(NewItem, hub.NewWorkspaceCommand, keys["New"]);
        Bind(OpenWorkspaceItem, hub.OpenWorkspaceCommand, keys["OpenWorkspace"]);
        Bind(OpenItem, hub.OpenCommand, keys["Open"]);
        ImportItem.InputGesture = null;
        Bind(RefreshItem, hub.RefreshCommand, keys["Refresh"]);
        Bind(NewTaskItem, hub.NewTaskCommand, keys["NewTask"]);
        Bind(AskBuilderItem, hub.AskBuilderCommand, keys["AskBuilder"]);
        Bind(AskReviewerItem, hub.AskReviewerCommand, keys["AskReviewer"]);
        Bind(AcceptItem, hub.ReviewDecisionCommand, keys["Accept"]);
        Bind(RequestChangesItem, hub.RequestChangesCommand, keys["RequestChanges"]);
        Bind(RequireReviewItem, hub.ToggleReviewCommand, keys["RequireReview"]);
        PolicyItem.InputGesture = null;
        Bind(AssignItem, hub.AssignCommand, keys["Assign"]);
        Bind(AddPrereqItem, hub.AddPrerequisiteCommand, keys["AddPrerequisite"]);
        RemovePrereqItem.InputGesture = null;
        Bind(MoveOpenItem, hub.MoveTaskCommand, keys["MoveOpen"], "open");
        Bind(MoveActiveItem, hub.MoveTaskCommand, keys["MoveActive"], "in_progress");
        Bind(MoveDoneItem, hub.MoveTaskCommand, keys["MoveDone"], "done");
        Bind(MoveCancelledItem, hub.MoveTaskCommand, keys["MoveCancelled"], "cancelled");
        Bind(ProjectsItem, hub.ToggleProjectsCommand, keys["Projects"]);
        Bind(DetailsItem, hub.ToggleInspectorCommand, keys["Details"]);
        Bind(BlockerItem, hub.GoToBlockerCommand, keys["Blocker"]);
        Bind(BackItem, hub.BackCommand, keys["Back"]);
        BackInboxItem.InputGesture = null;
        Bind(BoardItem, hub.ShowBoardCommand, keys["ShowBoard"]);
        Bind(InboxItem, hub.ShowInboxCommand, keys["ShowInbox"]);
        Bind(ChannelItem, hub.ShowChannelCommand, keys["ShowChannel"]);
        Bind(QuestionsItem, hub.ShowQuestionsCommand, keys["ShowQuestions"]);
        ConnectionsItem.InputGesture = null;
        TeamItem.InputGesture = null;
        PlanItem.InputGesture = null;
        Bind(MenuItemGo, hub.OpenMenuCommand, keys["Menu"]);
        hub.WindowRestoreRequested += ApplyPlacement;
        hub.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HubViewModel.MenuOpen)) OnMenu(); };
        Closing += (_, e) =>
        {
            SavePlacement();
            if (closeApproved || !hub.HasActiveRuns) return;
            e.Cancel = true;
            _ = AskCloseAsync();
        };
        hub.PlaceCapture += CapturePlace;
        hub.PlaceRestore += RestorePlace;
        hub.ConfirmErase = async text =>
        {
            bool ok = false;
            var dialog = new PromptDialog("Erase workspace", text, "", null, null, "Erase", (v, _) => { if (v.Trim() != "ERASE") return Task.FromResult<string?>("Type ERASE exactly."); ok = true; return Task.FromResult<string?>(null); });
            await dialog.ShowDialog<bool>(this);
            RestoreFocus();
            return ok;
        };
        ComposerBox.AddHandler(DragDrop.DropEvent, OnComposerDrop);
        ComposerBox.AddHandler(KeyDownEvent, OnComposerKey, RoutingStrategies.Tunnel);
        var placementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        placementTimer.Tick += (_, _) => { placementTimer.Stop(); SavePlacement(); };
        PositionChanged += (_, _) => { placementTimer.Stop(); placementTimer.Start(); };
        SizeChanged += (_, _) => { placementTimer.Stop(); placementTimer.Start(); };
        hub.AskForKey = async prompt =>
        {
            string? key = null;
            var dialog = new PromptDialog("Enter an API key", prompt, "", null, null, "Store key", (value, _) => { key = value; return Task.FromResult<string?>(null); });
            dialog.MaskFirstField();
            await dialog.ShowDialog<bool>(this);
            RestoreFocus();
            return key;
        };
        hub.TaskRevealed += Reveal;
        columns = [OpenList, ActiveList, DoneList, CancelledList];
        foreach (var list in columns)
        {
            list.AddHandler(Controls.TaskCard.BlockerRequestedEvent, GoToBlocker);
            list.SelectionChanged += (_, _) => OnColumnSelection(list);
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control && list.SelectedItem is TaskItem t) { hub.ToggleSelectCommand.Execute(t); e.Handled = true; }
            };
        }
        // Tasks live in four lists but there is one selection: the view model's.
        hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(HubViewModel.SelectedTask) or nameof(HubViewModel.Tasks))) return;
            bool inList = lastFocus is ListBoxItem;
            SyncSelection();
            if (inList && e.PropertyName == nameof(HubViewModel.SelectedTask)) RestoreFocus();
        };
        hub.PromptRequestChanges = () =>
        {
            var submit = hub.FreezeRequestChanges(); // bound to the task and revision shown now
            var keep = hub.FreezeDraftSaver();       // what is typed is kept, anchored to that same revision
            var dialog = new PromptDialog("Request changes", hub.RequestChangesLabel, hub.RequestChangesInitialText, null, null, "Request changes", (comment, _) => submit(comment));
            dialog.FirstChanged += keep;
            return dialog.ShowDialog<bool>(this).ContinueWith(_ => RestoreFocus(), TaskScheduler.FromCurrentSynchronizationContext());
        };
        hub.DecisionFocusRequested += () => Dispatcher.UIThread.Post(() => { AcceptButton.IsVisible = true; AcceptButton.Focus(); }, DispatcherPriority.Loaded);
        hub.PromptSetPolicy = () =>
        {
            var submit = hub.FreezePolicyChange();
            return new PromptDialog("Review policy", "Who decides on an agent's work?", "", null,
                [("human", "Human acceptance: only you decide; agents recommend"), ("reviewer", "Reviewer approval: a reviewer's approval counts")], "Use this policy",
                (name, _) => submit(name)).ShowDialog<bool>(this).ContinueWith(_ => RestoreFocus(), TaskScheduler.FromCurrentSynchronizationContext());
        };
        hub.PromptNewTask = () => new PromptDialog("New task", "Title", "", "Requested outcome (optional)", null, "Create task",
            (title, outcome) => hub.CreateTaskAsync(title, outcome)).ShowDialog<bool>(this).ContinueWith(_ => RestoreFocus(), TaskScheduler.FromCurrentSynchronizationContext());
        hub.PromptAssign = () => new PromptDialog("Assign owner or role", "Owner or role (leave empty to unassign)", hub.SelectedTask?.Owner ?? "", null, null, "Assign",
            (owner, _) => hub.AssignAsync(owner)).ShowDialog<bool>(this).ContinueWith(_ => RestoreFocus(), TaskScheduler.FromCurrentSynchronizationContext());
        hub.PromptAddPrerequisite = () => new PromptDialog("Add prerequisite", "This task cannot start until this one is done:", "", null, hub.PrerequisiteCandidates(), "Add",
            (id, _) => hub.AddPrerequisiteAsync(id)).ShowDialog<bool>(this).ContinueWith(_ => RestoreFocus(), TaskScheduler.FromCurrentSynchronizationContext());
        hub.PromptRemovePrerequisite = () => new PromptDialog("Remove prerequisite", "Stop waiting for:", "", null, hub.CurrentPrerequisites(), "Remove",
            (id, _) => hub.RemovePrerequisiteAsync(id)).ShowDialog<bool>(this).ContinueWith(_ => RestoreFocus(), TaskScheduler.FromCurrentSynchronizationContext());
        SizeChanged += (_, e) => hub.SetWidth(e.NewSize.Width);
        // Closing a menu (Escape or after an action) hands focus back to where the user was working.
        AddHandler(GotFocusEvent, (_, e) => { if (e.Source is Control c and not (MenuItem or Menu)) lastFocus = c; });
        MainMenu.Closed += (_, _) => Dispatcher.UIThread.Post(() => { if (!MainMenu.IsOpen) RestoreFocus(); }, DispatcherPriority.Input);
        // Escape from an open menu leaves the menu bar highlighted; hand focus back to the workspace.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is MenuItem)
                Dispatcher.UIThread.Post(() => { MainMenu.Close(); RestoreFocus(); }, DispatcherPriority.Background);
        }, handledEventsToo: true);
        if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() is string file)
            Opened += async (_, _) => await hub.OpenPathAsync(file);
    }

    // The same gesture object drives the hint and the binding, so they cannot disagree.
    private void Bind(MenuItem item, ICommand command, KeyGesture gesture, object? parameter = null)
    {
        item.InputGesture = gesture;
        KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = command, CommandParameter = parameter! });
    }

    private void OnColumnSelection(ListBox list)
    {
        if (syncing || list.SelectedItem is not TaskItem picked) return;
        hub.Steady.Anchor = hub.Steady.Focus = picked.Id; // the card being worked on keeps its place while others arrive
        hub.SelectedTask = picked;
        SyncSelection();
    }

    private void SyncSelection()
    {
        syncing = true;
        try
        {
            var task = hub.SelectedTask;
            foreach (var list in columns)
                list.SelectedItem = task != null && list.ItemsSource is IEnumerable<TaskItem> items && items.Contains(task) ? task : null;
        }
        finally { syncing = false; }
    }

    // Cards are rebuilt after every write, so focus goes back to the selected card once the new ones exist.
    // --- Evidence in a message, recovery, closing ---------------------------------------------------------------------------
    private bool closeApproved;
    private readonly Dictionary<string, double[]> places = [];

    private void RemoveAttachment(object? sender, RoutedEventArgs e) => hub.RemoveAttachmentCommand.Execute((sender as Control)?.DataContext);
    private void InspectAttachment(object? sender, RoutedEventArgs e) => hub.InspectAttachmentCommand.Execute((sender as Control)?.DataContext);
    private void ConfirmAttempt(object? sender, RoutedEventArgs e) => hub.ConfirmAttemptCommand.Execute((sender as Control)?.DataContext);
    private void AbandonAttempt(object? sender, RoutedEventArgs e) => hub.AbandonAttemptCommand.Execute((sender as Control)?.DataContext);
    private void RetryUncertain(object? sender, RoutedEventArgs e) => hub.RetryUncertainCommand.Execute((sender as Control)?.DataContext);
    private void RecordCharge(object? sender, RoutedEventArgs e) => hub.RecordChargeCommand.Execute((sender as Control)?.DataContext);
    private void OpenCitation(object? sender, RoutedEventArgs e) => hub.OpenCitationCommand.Execute((sender as Control)?.DataContext);
    private void ReplayPrevious(object? sender, RoutedEventArgs e) => hub.ReplayStepCommand.Execute(-1);
    private void ReplayNext(object? sender, RoutedEventArgs e) => hub.ReplayStepCommand.Execute(1);
    private void ReplaySelected(object? sender, SelectionChangedEventArgs e) { if (ReplayList.SelectedIndex >= 0) hub.SeekReplay(ReplayList.SelectedIndex); }

    private async void OnComposerDrop(object? sender, DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
            if (item.TryGetLocalPath() is { } path) await hub.AttachFileAsync(path);
    }

    /// <summary>Ctrl+V with an image on the clipboard attaches it; text pastes as usual, line breaks and all.</summary>
    private async void OnComposerKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || e.KeyModifiers != KeyModifiers.Control || TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        using var bmp = await clip.TryGetBitmapAsync();
        if (bmp == null) return;
        e.Handled = true;
        using var ms = new MemoryStream();
#pragma warning disable CS0618 // the replacement overload takes encoder options this app has no use for; PNG is the default
        bmp.Save(ms);
#pragma warning restore CS0618
        await hub.AttachImageAsync("pasted-image.png", ms.ToArray());
    }

    private ScrollViewer? ScrollOf(ListBox list) => list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    private void CapturePlace(string taskId) => places[taskId] = [.. columns.Select(l => ScrollOf(l)?.Offset.Y ?? 0)];
    private void RestorePlace(string taskId)
    {
        if (!places.TryGetValue(taskId, out var offsets)) return;
        // After the selection has scrolled its own card into view, put the board back exactly where it was.
        Dispatcher.UIThread.Post(() => { for (int i = 0; i < columns.Length && i < offsets.Length; i++) if (ScrollOf(columns[i]) is { } sv) sv.Offset = new(sv.Offset.X, offsets[i]); }, DispatcherPriority.Background);
    }

    /// <summary>The close prompt: hide, let the runs finish and then exit, or stop them. Only shown when a run is in progress.</summary>
    private async Task AskCloseAsync()
    {
        string? choice = null;
        var dialog = new PromptDialog("Runs are in progress", "What should happen to them?", "", null, [.. HubViewModel.CloseChoices.Select(c => (c.Value, c.Label))], "Continue", (value, _) => { choice = value; return Task.FromResult<string?>(null); });
        await dialog.ShowDialog<bool>(this);
        if (choice == null) return; // the owner changed their mind: the window stays open
        string action = await hub.ClosePolicyAsync(choice);
        if (action == "close") { closeApproved = true; Close(); return; }
        WindowState = WindowState.Minimized;
        if (action != "finish") return;
        int waited = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            if (hub.HasActiveRuns && ++waited < 600) return;
            timer.Stop();
            if (hub.HasActiveRuns) await hub.ClosePolicyAsync("stop"); // bounded: after ten minutes the runs are stopped, not abandoned mid-request
            closeApproved = true;
            Close();
        };
        timer.Start();
    }

    // --- Command menu, plan graph, saved views, window placement ---------------------------------------------------------
    private Control? menuReturn;

    private void OnMenu()
    {
        if (hub.MenuOpen)
        {
            menuReturn = lastFocus;
            Dispatcher.UIThread.Post(() => { MenuBox.Focus(); MenuBox.SelectAll(); }, DispatcherPriority.Loaded);
        }
        else if (menuReturn is { IsEffectivelyVisible: true, IsEnabled: true } back) Dispatcher.UIThread.Post(() => back.Focus(), DispatcherPriority.Loaded);
        else RestoreFocus();
    }

    private void MenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { hub.CloseMenuCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { RunMenu(); e.Handled = true; }
        else if (e.Key == Key.Down && sender == MenuBox && MenuList.ItemCount > 0) { MenuList.SelectedIndex = Math.Max(0, MenuList.SelectedIndex); MenuList.Focus(); e.Handled = true; }
    }

    private void MenuPicked(object? sender, TappedEventArgs e) => RunMenu();

    private void RunMenu()
    {
        var entry = MenuList.SelectedItem as MenuEntry ?? hub.MenuEntries.FirstOrDefault();
        if (entry != null) hub.RunMenuEntryCommand.Execute(entry);
    }

    private void PlanKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        hub.ActivateNodeCommand.Execute(null);
        e.Handled = true;
    }

    private void OpenNode(object? sender, RoutedEventArgs e) => hub.ActivateNodeCommand.Execute((sender as Control)?.DataContext);
    private void OpenNodeBlocker(object? sender, RoutedEventArgs e) => hub.OpenNodeBlockerCommand.Execute((sender as Control)?.DataContext);
    private void ApplyView(object? sender, RoutedEventArgs e) => hub.ApplyViewCommand.Execute((sender as Control)?.DataContext);

    private IReadOnlyList<ScreenArea> ScreenAreas() => [.. Screens.All.Select(s => new ScreenArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.IsPrimary))];

    private void ApplyPlacement(WindowPlacement saved)
    {
        var fit = LayoutStore.Fit(saved, ScreenAreas());
        double scale = Math.Max(0.5, RenderScaling);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new(fit.X, fit.Y);
        Width = Math.Max(MinWidth, fit.Width / scale);
        Height = Math.Max(MinHeight, fit.Height / scale);
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal || Bounds.Width < 100) return;
        hub.NoteWindow(new(Position.X, Position.Y, (int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling)));
    }

    private void RestoreFocus()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (hub.SelectedTask is { } t && ColumnWith(t) is { } list && list.ContainerFromItem(t) is { } card) { card.Focus(); return; }
            (lastFocus is { IsEffectivelyVisible: true, IsEnabled: true } ? lastFocus : columns.FirstOrDefault(l => l.ItemCount > 0) ?? columns[0]).Focus();
        }, DispatcherPriority.Loaded);
    }

    private void GoToBlocker(object? sender, RoutedEventArgs args) =>
        hub.GoToBlockerCommand.Execute((args.Source as Control)?.DataContext);

    private void ProbeConnection(object? sender, RoutedEventArgs args) => hub.ProbeCommand.Execute((sender as Button)?.DataContext);
    private void EnterKey(object? sender, RoutedEventArgs args) => hub.EnterKeyCommand.Execute((sender as Button)?.DataContext);
    private void DeleteKey(object? sender, RoutedEventArgs args) => hub.DeleteKeyCommand.Execute((sender as Button)?.DataContext);
    private void RemoveConnection(object? sender, RoutedEventArgs args) => hub.RemoveConnectionCommand.Execute((sender as Button)?.DataContext);
    private void PauseAttempt(object? sender, RoutedEventArgs args) => hub.PauseAttemptCommand.Execute((sender as Button)?.DataContext);
    private void CancelAttempt(object? sender, RoutedEventArgs args) => hub.CancelAttemptCommand.Execute((sender as Button)?.DataContext);
    private void ResumeAttempt(object? sender, RoutedEventArgs args) => hub.ResumeAttemptCommand.Execute((sender as Button)?.DataContext);
    private void RetryAttempt(object? sender, RoutedEventArgs args) => hub.RetryWithCostCommand.Execute((sender as Button)?.DataContext);

    private void SendReply(object? sender, RoutedEventArgs args) => hub.ReplyQuestionCommand.Execute((sender as Button)?.DataContext);

    // The channel list is virtualized. Streaming adds rows at the end and never moves what the reader is doing: selection stays,
    // the viewport stays unless the reader chose to follow, and scrolling away from the end turns following off.
    private void WireChannel()
    {
        ScrollViewer? scroll = null;
        ScrollViewer? Scroll() => scroll ??= ChannelList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        ChannelList.SelectionChanged += (_, _) => hub.ChannelSelection = [.. ChannelList.SelectedItems!.Cast<ChannelRow>()];
        ChannelList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) && hub.CopyChannelCommand.CanExecute(null))
            {
                hub.CopyChannelCommand.Execute(null);
                e.Handled = true;
            }
        };
        ChannelList.LayoutUpdated += (_, _) =>
        {
            if (Scroll() is not { } sv || scroll == null || sv.Tag != null) return;
            sv.Tag = "wired";
            sv.ScrollChanged += (_, e) =>
            {
                bool atEnd = sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 4;
                if (e.OffsetDelta.Y < 0 && !atEnd) hub.FollowLatest = false;
                else if (atEnd && e.OffsetDelta.Y > 0) hub.FollowLatest = true;
                if (ChannelList.ItemsPanelRoot is VirtualizingStackPanel panel && panel.FirstRealizedIndex is >= 0 and var first && first < hub.ChannelRows.Count)
                    hub.ChannelAnchorSeq = hub.ChannelRows[first].Message.Seq;
            };
        };
        hub.ChannelScrollToEnd += () => Dispatcher.UIThread.Post(() =>
        {
            if (hub.ChannelRows.Count > 0 && hub.ChannelVisible) ChannelList.ScrollIntoView(hub.ChannelRows[^1]);
        }, DispatcherPriority.Loaded);
        hub.ChannelAnchorRestore += seq => Dispatcher.UIThread.Post(() =>
        {
            if (hub.ChannelRows.FirstOrDefault(r => r.Message.Seq == seq) is { } row) ChannelList.ScrollIntoView(row);
        }, DispatcherPriority.Loaded);
        hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HubViewModel.ChannelVisible) && hub.ChannelVisible) { hub.FollowLatest = true; hub.ChannelScrollToEndRequested(); }
        };
    }

    private void OpenInboxItem(object? sender, RoutedEventArgs args) => hub.OpenInboxItemCommand.Execute((sender as Button)?.DataContext);
    private void OpenChannelTask(object? sender, RoutedEventArgs args) => hub.OpenChannelTaskCommand.Execute((sender as Button)?.DataContext);

    private void GoToPrerequisite(object? sender, RoutedEventArgs args) =>
        hub.GoToBlockerCommand.Execute((sender as Button)?.DataContext);

    private void Reveal(TaskItem target)
    {
        SyncSelection();
        var list = ColumnWith(target);
        list.ScrollIntoView(target);
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(list.SelectedItem, target))
                list.ContainerFromItem(target)?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private ProcessWorker? worker;
    private Task<WorkerClient>? client;

    // One worker per app run; its credential never leaves this process pair.
    private Task<WorkerClient> ConnectAsync() => client ??= StartWorkerAsync();

    private async Task<WorkerClient> StartWorkerAsync()
    {
        try
        {
            string exe = ProcessWorker.Locate() ?? throw new WorkerException(new("worker_unavailable", "The Agent Hub worker is missing.", "Reinstall or rebuild Agent Hub, then restart it."));
            string profile = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.UserName)))[..12];
            worker = new ProcessWorker(exe, profile);
            await worker.StartAsync();
            return new WorkerClient(worker);
        }
        catch (Exception e)
        {
            client = null; // let the next attempt try again
            if (e is WorkerException) throw;
            throw new WorkerException(new("worker_unavailable", "The Agent Hub worker could not start.", "Reinstall or rebuild Agent Hub, then restart it."));
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a workspace folder" });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a LocalMind projects database",
            FileTypeFilter = [new FilePickerFileType("SQLite database") { Patterns = ["*.db", "*.sqlite", "*.sqlite3"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
