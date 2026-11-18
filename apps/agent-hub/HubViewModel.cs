using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Data.Sqlite;

namespace LocalMind.AgentHub;

public sealed class HubViewModel : INotifyPropertyChanged
{
    private string? path;
    private int version;
    private ProjectItem? selected;
    private string statusText = "Open a LocalMind projects.db file to view your workspace.";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));

    public IReadOnlyList<ProjectItem> Projects { get; private set; } = [];
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];
    public string StatusText { get => statusText; private set { statusText = value; Notify(); } }
    public string ProjectHeading => selected?.Name ?? "Project workspace";
    public ICommand RefreshCommand { get; }

    private sealed class Refresh(HubViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        public bool CanExecute(object? parameter) => owner.path != null;
        public async void Execute(object? parameter)
        {
            if (owner.path is string file) await owner.OpenAsync(file);
        }
    }

    public HubViewModel() => RefreshCommand = new Refresh(this);
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

    private void SetSelection(ProjectItem? value)
    {
        selected = value;
        Tasks = [];
        Notify(nameof(SelectedProject)); Notify(nameof(ProjectHeading)); Notify(nameof(Tasks));
    }

    public async Task OpenAsync(string file)
    {
        var preferred = path == file ? selected?.Id : null;
        int request = ++version;
        path = file;
        SetSelection(null);
        Projects = [];
        Notify(nameof(Projects));
        ((Refresh)RefreshCommand).Changed();
        StatusText = "Opening database…";
        try
        {
            var projects = await Task.Run(() => new ProjectReader(file).Projects());
            if (version != request) return;
            Projects = projects;
            Notify(nameof(Projects));
            var first = projects.Find(p => p.Id == preferred) ?? projects.FirstOrDefault();
            SetSelection(first);
            await LoadTasksAsync(first, request);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            if (request == version) StatusText = "Could not open this database. Choose a readable LocalMind projects.db file.";
        }
    }

    private async Task LoadTasksAsync(ProjectItem? project, int request)
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
