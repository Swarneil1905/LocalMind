using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace LocalMind.AgentHub.Controls;

/// <summary>A human decision on one exact output. Disabled while busy or when the owner says no; errors name what to do next.</summary>
public sealed class DecisionViewModel : INotifyPropertyChanged
{
    private bool busy, enabled = true;
    private string? error, outcome;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

    public string Question { get; init; } = "";
    public string Subject { get; init; } = "this output";
    public bool Busy { get => busy; set { busy = value; Notify(); Refresh(); } }
    public bool Enabled { get => enabled; set { enabled = value; Notify(); Refresh(); } }
    public string? Error { get => error; set { error = value; Notify(); Notify(nameof(HasError)); } }
    public string? Outcome { get => outcome; private set { outcome = value; Notify(); Notify(nameof(HasOutcome)); } }
    public bool HasError => !string.IsNullOrEmpty(error);
    public bool HasOutcome => !string.IsNullOrEmpty(outcome);
    public WorkspaceCommand AcceptCommand { get; }
    public WorkspaceCommand RequestChangesCommand { get; }

    public DecisionViewModel()
    {
        AcceptCommand = new(_ => enabled && !busy, _ => { Outcome = "Accepted by you."; return Task.CompletedTask; });
        RequestChangesCommand = new(_ => enabled && !busy, _ => { Outcome = "Changes requested by you."; return Task.CompletedTask; });
    }

    private void Refresh() { AcceptCommand.Changed(); RequestChangesCommand.Changed(); }
}

public partial class DecisionControl : UserControl { public DecisionControl() => InitializeComponent(); }
