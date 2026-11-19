using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace LocalMind.AgentHub.Controls;

/// <summary>The recorded status of a task or run. Tone is "Neutral", "Busy" or "Error"; the text always carries the meaning.</summary>
public partial class StatusChip : UserControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<StatusChip, string?>(nameof(Text));
    public static readonly StyledProperty<string> ToneProperty = AvaloniaProperty.Register<StatusChip, string>(nameof(Tone), "Neutral");
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    public StatusChip() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Label.Text = Text;
            AutomationProperties.SetName(this, $"Status: {Text}");
        }
        else if (change.Property == ToneProperty)
        {
            Root.Classes.Set("error", Tone == "Error");
            Root.Classes.Set("busy", Tone == "Busy");
        }
    }
}
