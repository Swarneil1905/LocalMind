using System.Windows.Input;
using Avalonia.Controls;
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
        hub.ShowNewWorkspace = () => new NewWorkspaceDialog(hub.CreateWorkspaceAsync, PickFolderAsync).ShowDialog<bool>(this);
        Closed += async (_, _) => { if (worker != null) await worker.DisposeAsync(); };
        DataContext = hub;
        var keys = Shortcuts.For(OperatingSystem.IsMacOS());
        Bind(NewItem, hub.NewWorkspaceCommand, keys["New"]);
        Bind(OpenWorkspaceItem, hub.OpenWorkspaceCommand, keys["OpenWorkspace"]);
        Bind(OpenItem, hub.OpenCommand, keys["Open"]);
        ImportItem.InputGesture = null;
        Bind(RefreshItem, hub.RefreshCommand, keys["Refresh"]);
        Bind(NewTaskItem, hub.NewTaskCommand, keys["NewTask"]);
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
        hub.TaskRevealed += Reveal;
        columns = [OpenList, ActiveList, DoneList, CancelledList];
        foreach (var list in columns)
        {
            list.AddHandler(Controls.TaskCard.BlockerRequestedEvent, GoToBlocker);
            list.SelectionChanged += (_, _) => OnColumnSelection(list);
        }
        // Tasks live in four lists but there is one selection: the view model's.
        hub.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(HubViewModel.SelectedTask) or nameof(HubViewModel.Tasks)) SyncSelection(); };
        hub.PromptNewTask = () => new PromptDialog("New task", "Title", "", "Requested outcome (optional)", null, "Create task",
            (title, outcome) => hub.CreateTaskAsync(title, outcome)).ShowDialog<bool>(this);
        hub.PromptAssign = () => new PromptDialog("Assign owner or role", "Owner or role (leave empty to unassign)", hub.SelectedTask?.Owner ?? "", null, null, "Assign",
            (owner, _) => hub.AssignAsync(owner)).ShowDialog<bool>(this);
        hub.PromptAddPrerequisite = () => new PromptDialog("Add prerequisite", "This task cannot start until this one is done:", "", null, hub.PrerequisiteCandidates(), "Add",
            (id, _) => hub.AddPrerequisiteAsync(id)).ShowDialog<bool>(this);
        hub.PromptRemovePrerequisite = () => new PromptDialog("Remove prerequisite", "Stop waiting for:", "", null, hub.CurrentPrerequisites(), "Remove",
            (id, _) => hub.RemovePrerequisiteAsync(id)).ShowDialog<bool>(this);
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

    private void RestoreFocus() =>
        (lastFocus is { IsEffectivelyVisible: true, IsEnabled: true } ? lastFocus : columns.FirstOrDefault(l => l.ItemCount > 0) ?? columns[0]).Focus();

    private void GoToBlocker(object? sender, RoutedEventArgs args) =>
        hub.GoToBlockerCommand.Execute((args.Source as Control)?.DataContext);

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
