using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LocalMind.AgentHub;

public partial class MainWindow : Window
{
    private readonly HubViewModel hub = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = hub;
        if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() is string file)
            Opened += async (_, _) => await hub.OpenAsync(file);
    }

    private void GoToBlocker(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: TaskItem source } || !hub.Tasks.Contains(source)) return;
        var target = hub.Tasks.FirstOrDefault(task => task.Id == source.FirstBlockerId);
        if (target == null) return;
        TaskList.SelectedItem = target;
        TaskList.ScrollIntoView(target);
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(TaskList.SelectedItem, target))
                TaskList.ContainerFromItem(target)?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async void OpenProfile(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a LocalMind projects database",
            FileTypeFilter = [new FilePickerFileType("SQLite database") { Patterns = ["*.db", "*.sqlite", "*.sqlite3"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is string path)
            await hub.OpenAsync(path);
    }
}
