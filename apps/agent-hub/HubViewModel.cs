using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub;

public sealed class HubViewModel : INotifyPropertyChanged
{
    /// <summary>Below this width projects + 440-DIP task list + 280-DIP inspector no longer fit.</summary>
    public const double SplitWidth = 920;
    private const int MaxHistory = 20;

    private string? path;
    private int version;
    private ProjectItem? selected;
    private TaskItem? selectedTask;
    private readonly List<string> history = [];
    private bool showProjects = true, showInspector = true, narrow, inspectorBeforeNarrow = true;
    private string statusText = "Open a LocalMind projects.db file to view your workspace.";
    private readonly WorkspaceCommand[] commands;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));

    /// <summary>Supplied by the window; returns a local file path, or null when the user cancels.</summary>
    public Func<Task<string?>>? PickFile { get; set; }
    /// <summary>Raised when navigation selects a task the view should scroll to and focus.</summary>
    public event Action<TaskItem>? TaskRevealed;

    public IReadOnlyList<ProjectItem> Projects { get; private set; } = [];
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];
    public string StatusText { get => statusText; private set { statusText = value; Notify(); } }
    public string ProjectHeading => selected?.Name ?? "Project workspace";

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
        RefreshCommand = new(_ => path != null, _ => path is string file ? OpenAsync(file) : Task.CompletedTask);
        ToggleProjectsCommand = new(_ => true, _ => { showProjects = !showProjects; Layout(); return Task.CompletedTask; });
        ToggleInspectorCommand = new(_ => true, _ => { showInspector = !showInspector; Layout(); return Task.CompletedTask; });
        GoToBlockerCommand = new(p => Resolve(p) != null, p =>
        {
            if (Resolve(p) is var (source, target))
            {
                history.Add(source.Id);
                if (history.Count > MaxHistory) history.RemoveAt(0);
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
                if (Tasks.FirstOrDefault(t => t.Id == id) is { } source) { Select(source); break; }
            }
            RaiseCommands();
            return Task.CompletedTask;
        });
        commands = [OpenCommand, RefreshCommand, ToggleProjectsCommand, ToggleInspectorCommand, GoToBlockerCommand, BackCommand];
    }

    private void RaiseCommands() { foreach (var c in commands) c.Changed(); }

    public ProjectItem? SelectedProject
    {
        get => selected;
        set
        {
            if (selected == value) return;
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
            Notify(); Notify(nameof(HasTask));
            RaiseCommands();
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
    public double InspectorWidth => narrow ? double.NaN : 280;

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
                                     nameof(ListVisible), nameof(InspectorColumn), nameof(InspectorWidth) })
            Notify(name);
    }

    private void SetSelection(ProjectItem? value)
    {
        selected = value;
        Tasks = [];
        selectedTask = null;
        history.Clear();
        Notify(nameof(SelectedProject)); Notify(nameof(ProjectHeading)); Notify(nameof(Tasks));
        Notify(nameof(SelectedTask)); Notify(nameof(HasTask));
        RaiseCommands();
    }

    public async Task OpenAsync(string file)
    {
        var preferred = path == file ? selected?.Id : null;
        var keepTask = preferred == null ? null : selectedTask?.Id;
        var keepHistory = preferred == null ? [] : history.ToArray();
        int request = ++version;
        path = file;
        SetSelection(null);
        Projects = [];
        Notify(nameof(Projects));
        StatusText = "Opening database…";
        try
        {
            var projects = await Task.Run(() => new ProjectReader(file).Projects());
            if (version != request) return;
            Projects = projects;
            Notify(nameof(Projects));
            var first = projects.Find(p => p.Id == preferred) ?? projects.FirstOrDefault();
            SetSelection(first);
            bool same = first != null && first.Id == preferred;
            await LoadTasksAsync(first, request, same ? keepTask : null, same ? keepHistory : []);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            if (request == version) StatusText = "Could not open this database. Choose a readable LocalMind projects.db file.";
        }
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
            Notify(nameof(Tasks));
            // Refresh keeps what still exists; IDs that disappeared are dropped.
            history.Clear();
            history.AddRange((restoreHistory ?? []).Where(id => tasks.Any(t => t.Id == id)));
            SelectedTask = tasks.FirstOrDefault(t => t.Id == restoreTask);
            RaiseCommands();
            StatusText = tasks.Count == 0 ? "No tasks in this project." : $"{tasks.Count} task{(tasks.Count == 1 ? "" : "s")} · Read-only";
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            if (version == request) StatusText = "Could not read this project’s tasks. Refresh or choose another database.";
        }
    }

    private static bool IsDataError(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException or InvalidCastException or InvalidOperationException or ArgumentException;
}
