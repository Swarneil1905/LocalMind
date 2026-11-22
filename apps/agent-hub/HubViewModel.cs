using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalMind.AgentHub.Core;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub;

public sealed partial class HubViewModel : INotifyPropertyChanged
{
    /// <summary>Below this width projects + 440-DIP task list + 280-DIP inspector no longer fit.</summary>
    public const double SplitWidth = 920;
    private const int MaxHistory = 20;

    private string? path, workspaceName;
    private int version;
    private ProjectItem? selected;
    private TaskItem? selectedTask;
    private readonly List<string> history = [];
    private bool showProjects = true, showInspector = true, narrow, inspectorBeforeNarrow = true;
    private string statusText = "No workspace is open. Start the offline tutorial, or open or create a workspace.";
    private WorkspaceCommand[] commands;
    // Navigation snapshot of an in-flight open/refresh, so an overlapping refresh of the same file keeps it.
    private string? pendingFile, pendingProject, pendingTask;
    private string[] pendingHistory = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));

    /// <summary>Supplied by the window; returns a local file path, or null when the user cancels.</summary>
    public Func<Task<string?>>? PickFile
    {
        get => pickFile;
        set { pickFile = value; OpenCommand.Changed(); }
    }
    private Func<Task<string?>>? pickFile;
    /// <summary>Raised just before following a blocker, and again after Back, so the window can keep and restore the board's scroll position.</summary>
    public event Action<string>? PlaceCapture, PlaceRestore;
    /// <summary>Raised when navigation selects a task the view should scroll to and focus.</summary>
    public event Action<TaskItem>? TaskRevealed;

    public IReadOnlyList<ProjectItem> Projects { get; private set; } = [];
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];
    public string StatusText { get => statusText; private set { statusText = value; Notify(); Notify(nameof(FooterText)); } }
    public string ProjectHeading => selected?.Name ?? "Project workspace";

    /// <summary>Set by the window; null in tests that only read legacy files.</summary>
    public WorkspaceService? Workspaces { get => workspaces; set { workspaces = value; NotifyWorkspaceCommands(); Notify(nameof(IsWorkspace)); } }
    private WorkspaceService? workspaces;
    public Func<Task>? ShowNewWorkspace { get => showNew; set { showNew = value; NotifyWorkspaceCommands(); } }
    private Func<Task>? showNew;
    public Func<Task<string?>>? PickFolder { get => pickFolder; set { pickFolder = value; NotifyWorkspaceCommands(); } }
    private Func<Task<string?>>? pickFolder;
    private void NotifyWorkspaceCommands() { NewWorkspaceCommand?.Changed(); OpenWorkspaceCommand?.Changed(); ImportLegacyCommand?.Changed(); }

    public string ModeLabel => path == null ? "" : workspaceName == null ? "Legacy file · Read-only" : ReadOnlyReason != null ? $"{workspaceName} · read-only (open in another window)" : $"{(IsDemo ? "DEMO CONTENT · " : "")}{workspaceName} · Agent Hub workspace";

    public WorkspaceCommand NewWorkspaceCommand { get; }
    public WorkspaceCommand OpenWorkspaceCommand { get; }
    public WorkspaceCommand ImportLegacyCommand { get; }
    public WorkspaceCommand OpenCommand { get; }
    public WorkspaceCommand RefreshCommand { get; }
    public WorkspaceCommand ToggleProjectsCommand { get; }
    public WorkspaceCommand ToggleInspectorCommand { get; }
    /// <summary>Parameter: a TaskItem (its first unresolved prerequisite), a Prerequisite of the selected task, or none (selected task).</summary>
    public WorkspaceCommand GoToBlockerCommand { get; }
    public WorkspaceCommand BackCommand { get; }

    public HubViewModel()
    {
        OpenCommand = new(_ => PickFile != null, async _ =>
        {
            if (await PickFile!() is string file) await OpenAsync(file);
        });
        RefreshCommand = new(_ => path != null, _ => path is string file ? OpenAsync(file, workspaceName) : Task.CompletedTask);
        NewWorkspaceCommand = new(_ => workspaces != null && showNew != null, _ => showNew!());
        OpenWorkspaceCommand = new(_ => workspaces != null && pickFolder != null, async _ =>
        {
            if (await pickFolder!() is string folder && await OpenWorkspaceFolderAsync(folder) is { } error) StatusText = error;
        });
        ImportLegacyCommand = new(_ => workspaces != null && pickFolder != null && pickFile != null, async _ =>
        {
            if (await pickFile!() is not string source || await pickFolder!() is not string folder) return;
            try
            {
                var info = await workspaces!.ImportAsync(source, folder);
                await OpenAsync(info.DatabasePath, info.Name);
            }
            catch (WorkerException e) { StatusText = e.Message; }
        });
        ToggleProjectsCommand = new(_ => true, _ => { showProjects = !showProjects; Layout(); return Task.CompletedTask; });
        ToggleInspectorCommand = new(_ => true, _ => { showInspector = !showInspector; Layout(); return Task.CompletedTask; });
        GoToBlockerCommand = new(p => Resolve(p) != null, p =>
        {
            if (Resolve(p) is var (source, target))
            {
                history.Add(source.Id);
                if (history.Count > MaxHistory) history.RemoveAt(0);
                PlaceCapture?.Invoke(source.Id);
                Select(target);
            }
            return Task.CompletedTask;
        });
        BackCommand = new(_ => history.Count > 0, _ =>
        {
            while (history.Count > 0)
            {
                var id = history[^1];
                history.RemoveAt(history.Count - 1);
                if (Tasks.FirstOrDefault(t => t.Id == id) is { } source) { Select(source); PlaceRestore?.Invoke(source.Id); break; }
            }
            RaiseCommands();
            return Task.CompletedTask;
        });
        commands = [OpenCommand, RefreshCommand, ToggleProjectsCommand, ToggleInspectorCommand, GoToBlockerCommand, BackCommand];
        InitEditing();
        InitReview();
        InitEvidence();
        InitModels();
        InitDay5();
    }

    /// <summary>Creates a workspace and opens it. Returns an error message to show, or null on success; a failure leaves the current selection alone.</summary>
    public async Task<string?> CreateWorkspaceAsync(string name, string folder)
    {
        try
        {
            var info = await workspaces!.CreateAsync(folder, name);
            await OpenAsync(info.DatabasePath, info.Name);
            return null;
        }
        catch (WorkerException e) { return e.Message; }
    }

    public async Task<string?> OpenWorkspaceFolderAsync(string folder)
    {
        try
        {
            if (await workspaces!.OpenAsync(folder) is { } info) await OpenAsync(info.DatabasePath, info.Name);
            return null;
        }
        catch (WorkerException e) { return e.Message; }
    }

    /// <summary>Startup path: a folder or agenthub.db opens as a workspace; any other file opens read-only as a legacy database.</summary>
    public async Task OpenPathAsync(string target)
    {
        if (workspaces != null && (Directory.Exists(target) || Path.GetFileName(target) == ProjectStore.FileName))
        {
            if (await OpenWorkspaceFolderAsync(target) is { } error) StatusText = error;
        }
        else await OpenAsync(target);
    }

    private void RaiseCommands() { foreach (var c in commands) c.Changed(); NotifyEmpty(); if (center == "guide") RefreshGuide(); }

    public ProjectItem? SelectedProject
    {
        get => selected;
        set
        {
            if (selected == value) return;
            pendingFile = null; // an intentional project switch wins over any in-flight refresh snapshot
            SetSelection(value);
            _ = LoadTasksAsync(value, ++version);
        }
    }

    public TaskItem? SelectedTask
    {
        get => selectedTask;
        set
        {
            if (selectedTask == value) return;
            selectedTask = value;
            InvalidateHandoff();
            ClearRunsForSelection();
            _ = RefreshRunsAsync();
            Notify(); Notify(nameof(HasTask)); Notify(nameof(CanEdit));
            RaiseCommands();
            NotifyReview(); // the previous task's review must not stay on screen
            _ = LoadReviewAsync();
        }
    }
    public bool HasTask => selectedTask != null;

    private void Select(TaskItem task)
    {
        SelectedTask = task;
        TaskRevealed?.Invoke(task);
    }

    private (TaskItem Source, TaskItem Target)? Resolve(object? parameter)
    {
        var source = parameter as TaskItem ?? selectedTask;
        if (source == null || !Tasks.Contains(source)) return null;
        string? id = parameter is Prerequisite need
            ? source.Dependencies.Contains(need) && !need.IsResolved ? need.Id : null
            : source.FirstBlockerId;
        return Tasks.FirstOrDefault(t => t.Id == id) is { } target ? (source, target) : null;
    }

    // Layout state. Hiding the inspector or the project pane never touches the selection.
    public bool ProjectsVisible => showProjects;
    public bool InspectorVisible => showInspector;
    public bool Narrow => narrow;
    public bool Wide => !narrow;
    public bool ListVisible => !(showInspector && narrow);
    public int InspectorColumn => narrow ? 1 : 2;
    public double InspectorWidth => narrow ? double.NaN : inspectorSetting;

    /// <summary>At narrow widths the inspector replaces the task list, so it starts closed and returns when the window widens.</summary>
    public void SetWidth(double width)
    {
        if (width <= 0 || narrow == width < SplitWidth) return;
        narrow = !narrow;
        if (narrow) { inspectorBeforeNarrow = showInspector; showInspector = false; }
        else showInspector |= inspectorBeforeNarrow;
        Layout();
    }

    private void Layout()
    {
        foreach (var name in new[] { nameof(ProjectsVisible), nameof(InspectorVisible), nameof(Narrow), nameof(Wide),
                                     nameof(ListVisible), nameof(InspectorColumn), nameof(InspectorWidth), nameof(BoardVisible), nameof(InboxVisible), nameof(ChannelVisible), nameof(QuestionsVisible), nameof(ConnectionsVisible), nameof(TeamVisible), nameof(PlanVisible), nameof(DocsVisible), nameof(DataVisible), nameof(ReplayVisible), nameof(CodeVisible), nameof(GuideVisible) })
            Notify(name);
    }

    private void SetSelection(ProjectItem? value)
    {
        // A different project: its composer is not this one's, so the old text, attachments and inspector go at once, before the new draft is read.
        if (value != null && composerProject.Length > 0 && value.Id != composerProject) DetachComposer(force: true);
        selected = value;
        InvalidateHandoff();
        Tasks = [];
        selectedTask = null;
        ClearPlan();
        history.Clear();
        RefreshColumns();
        Notify(nameof(SelectedProject)); Notify(nameof(ProjectHeading)); Notify(nameof(Tasks));
        Notify(nameof(SelectedTask)); Notify(nameof(HasTask));
        RaiseCommands();
    }

    public async Task OpenAsync(string file, string? workspace = null)
    {
        string? preferred, keepTask;
        string[] keepHistory;
        if (pendingFile == file) (preferred, keepTask, keepHistory) = (pendingProject, pendingTask, pendingHistory);
        else if (path == file) (preferred, keepTask, keepHistory) = (selected?.Id, selectedTask?.Id, history.ToArray());
        else (preferred, keepTask, keepHistory) = (null, null, []);
        (pendingFile, pendingProject, pendingTask, pendingHistory) = (file, preferred, keepTask, keepHistory);
        int request = ++version;
        bool sameFile = path == file;
        path = file;
        workspaceName = workspace;
        await UpdateWriteOwnershipAsync(file, workspace);
        await LoadWorkspaceKeyAsync(file, workspace != null);
        LoadLayout();
        DetachComposer();
        // Attempts left behind by a crash are sorted out once this window owns the workspace: unsent ones freed, sent ones kept for a check.
        if (CanWrite) { try { await (await workspaces!.Client()).RecoverRunsAsync(file); } catch (WorkerException) { } }
        Notify(nameof(ModeLabel)); Notify(nameof(IsWorkspace)); Notify(nameof(CanEdit));
        SetSelection(null);
        // A refresh keeps the project list so a switch made mid-refresh still has projects to land on.
        if (!sameFile) { Projects = []; Notify(nameof(Projects)); }
        workerDown = false; StatusText = "Opening database…";
        try
        {
            var projects = await Task.Run(() => new ProjectReader(file).Projects());
            if (version != request) return;
            Projects = projects;
            Notify(nameof(Projects));
            var first = projects.Find(p => p.Id == (preferred ?? savedLayout?.ProjectId)) ?? projects.FirstOrDefault();
            SetSelection(first);
            bool same = first != null && first.Id == preferred;
            await LoadTasksAsync(first, request, same ? keepTask : null, same ? keepHistory : []);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            if (request == version)
            {
                Projects = [];
                Notify(nameof(Projects));
                StatusText = "Could not open this database. Choose a readable LocalMind projects.db file.";
            }
        }
        finally { if (request == version) pendingFile = null; }
    }

    private async Task LoadTasksAsync(ProjectItem? project, int request, string? restoreTask = null, string[]? restoreHistory = null)
    {
        if (project == null || path is not string file)
        {
            StatusText = Projects.Count == 0 ? "No projects in this database." : "Select a project.";
            return;
        }
        StatusText = "Loading tasks…";
        try
        {
            var tasks = await Task.Run(() => new ProjectReader(file).Tasks(project.Id));
            if (version != request) return;
            Tasks = tasks;
            RefreshColumns();
            Notify(nameof(Tasks));
            // Refresh keeps what still exists; IDs that disappeared are dropped.
            history.Clear();
            history.AddRange((restoreHistory ?? []).Where(id => tasks.Any(t => t.Id == id)));
            SelectedTask = tasks.FirstOrDefault(t => t.Id == (nextSelectId ?? restoreTask));
            nextSelectId = null;
            RaiseCommands();
            _ = RefreshPlanAsync(false);
            _ = LoadChannelAsync();
            _ = LoadInboxAsync(request);
            _ = LoadQuestionsAsync();
            _ = LoadTeamAsync();
            StatusText = tasks.Count == 0 ? (workspaceName == null ? "No tasks in this project." : "No tasks in this workspace yet.")
                : $"{tasks.Count} task{(tasks.Count == 1 ? "" : "s")}" + (workspaceName == null ? " · Read-only" : "");
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            if (version == request) StatusText = "Could not read this project’s tasks. Refresh or choose another database.";
        }
    }

    private static bool IsDataError(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException or InvalidCastException or InvalidOperationException or ArgumentException;
}
