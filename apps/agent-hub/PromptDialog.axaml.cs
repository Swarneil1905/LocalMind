using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalMind.AgentHub;

/// <summary>One small dialog for task edits: a text field with an optional second field, or a choice of tasks.</summary>
public partial class PromptDialog : Window
{
    private readonly Func<string, string, Task<string?>> submit;
    private readonly IReadOnlyList<(string Id, string Label)>? choices;

    public PromptDialog() : this("Edit", "Value", "", null, null, "Save", (_, _) => Task.FromResult<string?>(null)) { }

    /// <param name="submit">Gets the first value (or the chosen task ID) and the second value; returns an error to show here, or null when it worked.</param>
    public PromptDialog(string title, string label1, string value1, string? label2, IReadOnlyList<(string Id, string Label)>? choices, string okText,
                        Func<string, string, Task<string?>> submit)
    {
        this.submit = submit;
        this.choices = choices;
        InitializeComponent();
        Title = title;
        Label1.Text = label1;
        OkButton.Content = okText;
        if (choices != null)
        {
            Field1.IsVisible = false;
            Choices.IsVisible = true;
            Choices.ItemsSource = choices.Select(c => c.Label).ToList();
            Choices.SelectedIndex = 0;
        }
        else
        {
            Field1.Text = value1;
            Field1.AutomationProperties_Name(label1);
        }
        if (label2 != null) { Label2.Text = label2; Label2.IsVisible = Field2.IsVisible = true; Field2.AutomationProperties_Name(label2); }
        // Enter on the task chooser submits, unless it is just opening the list.
        if (choices != null)
            AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Avalonia.Input.Key.Enter && !Choices.IsDropDownOpen && OkButton.IsEnabled) { e.Handled = true; Ok(null, new RoutedEventArgs()); } }, RoutingStrategies.Tunnel);
        Opened += (_, _) => (choices != null ? Choices : (Control)Field1).Focus();
    }

    /// <summary>Hides what is typed in the first field: for a key that must not be shown.</summary>
    public void MaskFirstField() { Field1.PasswordChar = '•'; Field1.MaxLength = 512; }

    /// <summary>Raised as the first field changes, so the caller can keep an unsent draft.</summary>
    public event Action<string>? FirstChanged;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Field1.TextChanged += (_, _) => FirstChanged?.Invoke(Field1.Text ?? "");
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void Ok(object? sender, RoutedEventArgs e)
    {
        OkButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        string first = choices != null ? (Choices.SelectedIndex >= 0 ? choices[Choices.SelectedIndex].Id : "") : Field1.Text ?? "";
        string? error = await submit(first, Field2.Text ?? "");
        if (error is null) { Close(true); return; }
        ErrorText.Text = error;
        ErrorText.IsVisible = true;
        OkButton.IsEnabled = true;
    }
}

internal static class AutomationNames
{
    public static void AutomationProperties_Name(this Control c, string name) => Avalonia.Automation.AutomationProperties.SetName(c, name);
}
