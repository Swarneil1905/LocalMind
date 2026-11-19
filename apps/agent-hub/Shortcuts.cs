using Avalonia.Input;

namespace LocalMind.AgentHub;

/// <summary>One gesture table per platform. The window uses each entry for both the key binding and the menu hint.</summary>
public static class Shortcuts
{
    public static IReadOnlyDictionary<string, KeyGesture> For(bool mac)
    {
        var primary = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        return new Dictionary<string, KeyGesture>
        {
            ["Open"] = new(Key.O, primary),
            ["Refresh"] = mac ? new(Key.R, primary) : new(Key.F5),
            ["Projects"] = new(Key.B, primary),
            ["Details"] = new(Key.I, primary),
            ["Blocker"] = new(Key.G, primary),
            ["Back"] = new(Key.Back, primary),
        };
    }
}
