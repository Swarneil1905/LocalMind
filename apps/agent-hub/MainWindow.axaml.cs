using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LocalMind.AgentHub;

public partial class MainWindow : Window
{
    private readonly HubViewModel hub = new();
    private Control? lastFocus;

    public MainWindow()
    {
        InitializeComponent();
        hub.PickFile = PickFileAsync;
        DataContext = hub;
        var keys = Shortcuts.For(OperatingSystem.IsMacOS());
        Bind(OpenItem, hub.OpenCommand, keys["Open"]);
        Bind(RefreshItem, hub.RefreshCommand, keys["Refresh"]);
        Bind(ProjectsItem, hub.ToggleProjectsCommand, keys["Projects"]);
        Bind(DetailsItem, hub.ToggleInspectorCommand, keys["Details"]);
        Bind(BlockerItem, hub.GoToBlockerCommand, keys["Blocker"]);
        Bind(BackItem, hub.BackCommand, keys["Back"]);
        hub.TaskRevealed += Reveal;
        SizeChanged += (_, e) => hub.SetWidth(e.NewSize.Width);
        // Closing a menu (Escape or after an action) hands focus back to where the user was working.
        AddHandler(GotFocusEvent, (_, e) => { if (e.Source is Control c and not (MenuItem or Menu)) lastFocus = c; });
        MainMenu.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (MainMenu.IsOpen) return; // Closed also fires while arrowing between menus.
            (lastFocus is { IsEffectivelyVisible: true, IsEnabled: true } ? lastFocus : TaskList).Focus();
        }, DispatcherPriority.Input);
        if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() is string file)
            Opened += async (_, _) => await hub.OpenAsync(file);
    }

    // The same gesture object drives the hint and the binding, so they cannot disagree.
    private void Bind(MenuItem item, ICommand command, KeyGesture gesture)
    {
        item.InputGesture = gesture;
        KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = command });
    }

    private void GoToBlocker(object? sender, RoutedEventArgs args) =>
        hub.GoToBlockerCommand.Execute((sender as Button)?.DataContext);

    private void GoToPrerequisite(object? sender, RoutedEventArgs args) =>
        hub.GoToBlockerCommand.Execute((sender as Button)?.DataContext);

    private void Reveal(TaskItem target)
    {
        TaskList.ScrollIntoView(target);
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(TaskList.SelectedItem, target))
                TaskList.ContainerFromItem(target)?.Focus();
        }, DispatcherPriority.Loaded);
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
