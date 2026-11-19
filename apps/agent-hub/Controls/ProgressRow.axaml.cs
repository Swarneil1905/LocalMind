using Avalonia.Automation;
using Avalonia.Controls;

namespace LocalMind.AgentHub.Controls;

/// <summary>Fraction is null when the work reports no progress; the bar then shows an indeterminate state, never an invented percentage.</summary>
public sealed record ProgressRowViewModel(string Label, double? Fraction, string StateText)
{
    public bool Indeterminate => Fraction is null && StateText != "Not started";
    public string AccessibleName => Fraction is { } f ? $"{Label}: {StateText}, {(int)(f * 100)} percent" : $"{Label}: {StateText}";
}

public partial class ProgressRow : UserControl
{
    public ProgressRow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is ProgressRowViewModel vm) AutomationProperties.SetName(this, vm.AccessibleName); };
    }
}
