using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalMind.AgentHub.Controls;

/// <summary>A task read without opening it: title, outcome, recorded state, owner and the first thing blocking it.</summary>
public partial class TaskCard : UserControl
{
    public static readonly RoutedEvent<RoutedEventArgs> BlockerRequestedEvent =
        RoutedEvent.Register<TaskCard, RoutedEventArgs>(nameof(BlockerRequested), RoutingStrategies.Bubble);
    /// <summary>Optional line below the card for a busy or failed operation. NoteTone is "Busy" or "Error".</summary>
    public static readonly StyledProperty<string?> NoteProperty = AvaloniaProperty.Register<TaskCard, string?>(nameof(Note));
    public static readonly StyledProperty<string> NoteToneProperty = AvaloniaProperty.Register<TaskCard, string>(nameof(NoteTone), "Busy");

    public event EventHandler<RoutedEventArgs> BlockerRequested { add => AddHandler(BlockerRequestedEvent, value); remove => RemoveHandler(BlockerRequestedEvent, value); }
    public string? Note { get => GetValue(NoteProperty); set => SetValue(NoteProperty, value); }
    public string NoteTone { get => GetValue(NoteToneProperty); set => SetValue(NoteToneProperty, value); }

    public TaskCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Describe();
    }

    private void BlockerClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(BlockerRequestedEvent));
    }

    // The accessible name carries everything the card shows, so it reads without color or layout.
    private void Describe()
    {
        if (DataContext is not TaskItem t) return;
        string name = $"{t.Title}, {t.Status}";
        if (t.HasOutcome) name += $", outcome: {t.Outcome}";
        AutomationProperties.SetName(this, $"{name}, owner: {t.OwnerLabel}, {t.MoveNote}");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != NoteProperty && change.Property != NoteToneProperty) return;
        NoteBox.IsVisible = !string.IsNullOrEmpty(Note);
        NoteText.Text = Note;
        NoteBox.Classes.Set("error", NoteTone == "Error");
    }
}
