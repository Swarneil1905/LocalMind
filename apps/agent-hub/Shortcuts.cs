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
            ["Menu"] = new(Key.K, primary),
            ["New"] = new(Key.N, primary),
            ["OpenWorkspace"] = new(Key.O, primary | KeyModifiers.Shift),
            ["Refresh"] = mac ? new(Key.R, primary) : new(Key.F5),
            ["Projects"] = new(Key.B, primary),
            ["Details"] = new(Key.I, primary),
            ["Blocker"] = new(Key.G, primary),
            ["Back"] = new(Key.Back, primary),
            ["NewTask"] = new(Key.T, primary),
            ["AskBuilder"] = new(Key.B, primary | KeyModifiers.Shift),
            ["AskReviewer"] = new(Key.R, primary | KeyModifiers.Shift),
            ["Accept"] = new(Key.A, primary | KeyModifiers.Shift),
            ["RequestChanges"] = new(Key.C, primary | KeyModifiers.Shift),
            ["RequireReview"] = new(Key.Q, primary | KeyModifiers.Shift),
            ["Assign"] = new(Key.E, primary),
            ["AddPrerequisite"] = new(Key.D, primary),
            ["ShowBoard"] = new(Key.D1, primary | KeyModifiers.Shift),
            ["ShowInbox"] = new(Key.D2, primary | KeyModifiers.Shift),
            ["ShowChannel"] = new(Key.D3, primary | KeyModifiers.Shift),
            ["ShowQuestions"] = new(Key.D4, primary | KeyModifiers.Shift),
            ["MoveOpen"] = new(Key.D1, primary),
            ["MoveActive"] = new(Key.D2, primary),
            ["MoveDone"] = new(Key.D3, primary),
            ["MoveCancelled"] = new(Key.D4, primary),
        };
    }
}
