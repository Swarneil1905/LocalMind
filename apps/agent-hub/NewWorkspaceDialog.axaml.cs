using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalMind.AgentHub;

public partial class NewWorkspaceDialog : Window
{
    private readonly Func<string, string, Task<string?>> submit;
    private readonly Func<Task<string?>> pickFolder;
    private bool folderEdited;
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Agent Hub");

    public NewWorkspaceDialog() : this((_, _) => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null)) { }

    /// <param name="submit">Creates the workspace; returns an error to show here, or null when it worked.</param>
    public NewWorkspaceDialog(Func<string, string, Task<string?>> submit, Func<Task<string?>> pickFolder)
    {
        this.submit = submit;
        this.pickFolder = pickFolder;
        InitializeComponent();
        FolderBox.Text = Root;
        // The folder follows the name until the user types a folder themselves.
        NameBox.TextChanged += (_, _) => { if (!folderEdited) FolderBox.Text = Path.Combine(Root, NameBox.Text?.Trim() ?? ""); };
        FolderBox.TextChanged += (_, _) => { if (FolderBox.IsKeyboardFocusWithin) folderEdited = true; };
        Opened += (_, _) => NameBox.Focus();
    }

    private async void Browse(object? sender, RoutedEventArgs e)
    {
        if (await pickFolder() is string folder) { FolderBox.Text = folder; folderEdited = true; }
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void Create(object? sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        string? error = await submit(NameBox.Text ?? "", FolderBox.Text ?? "");
        if (error is null) { Close(true); return; }
        ErrorText.Text = error;
        ErrorText.IsVisible = true;
        CreateButton.IsEnabled = true;
        NameBox.Focus();
    }
}
