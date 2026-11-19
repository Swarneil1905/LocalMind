using Avalonia.Automation;
using Avalonia.Controls;

namespace LocalMind.AgentHub.Controls;

/// <summary>Name, role and provider are separate values. Anything missing is left out, never guessed. A monogram stands in for an icon.</summary>
public sealed record AgentIdentity(string DisplayName, string? Role, string? Provider, bool IsFixture = false)
{
    public bool HasRole => !string.IsNullOrWhiteSpace(Role);
    public bool HasProvider => !string.IsNullOrWhiteSpace(Provider);
    public string Initials => string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string AccessibleName => DisplayName + (HasRole ? $", role {Role}" : "") + (HasProvider ? $", provider {Provider}" : ", no provider recorded")
        + (IsFixture ? ", fixture identity" : "");
}

public partial class AgentBadge : UserControl
{
    public AgentBadge()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is AgentIdentity a) AutomationProperties.SetName(this, a.AccessibleName); };
    }
}
