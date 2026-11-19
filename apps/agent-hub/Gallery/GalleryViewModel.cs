using LocalMind.AgentHub.Controls;

namespace LocalMind.AgentHub.Gallery;

public sealed record GalleryCard(string Label, TaskItem Task, string? Note = null, string NoteTone = "Busy", bool Enabled = true);
public sealed record GalleryChip(string Label, string Text, string Tone, bool Enabled = true);
public sealed record GalleryDecision(string Label, DecisionViewModel Decision);
public sealed record GalleryAgent(string Label, AgentIdentity Identity);
public sealed record GalleryProgress(string Label, ProgressRowViewModel Row);

/// <summary>Development-only sample states. Every value here is synthetic; the production window never references this type.</summary>
public sealed class GalleryViewModel
{
    private static readonly Prerequisite Draft = new("draft-1", "Draft the launch note", "open");
    private static readonly Prerequisite Cancelled = new("draft-2", "Draft the launch note", "cancelled");

    public IReadOnlyList<GalleryCard> Cards { get; } =
    [
        new("Normal", new("c1", "Write the outline", "open", [], "A one-page outline the reviewer can approve.", "Maya Chen")),
        new("Blocked, long title", new("c2", "QA the end-to-end onboarding flow across every supported provider, including the offline reviewer fixtures and the cancelled-prerequisite case",
            "open", [Draft, Cancelled], "Confirm the whole flow works on a fresh profile.", null)),
        new("Disabled", new("c3", "Archive the old board", "open", [], null, "Sam Ortiz"), null, "Busy", false),
        new("Busy", new("c4", "Assign the reviewer", "open", [], null, null), "Saving your change…", "Busy"),
        new("Error", new("c5", "Move QA to in progress", "open", [Draft], null, null),
            "This task changed in another window. Reload the board, then try the move again.", "Error"),
    ];

    public IReadOnlyList<GalleryChip> Chips { get; } =
    [
        new("Normal", "open", "Neutral"), new("Busy", "running", "Busy"), new("Error", "failed", "Error"), new("Disabled", "archived", "Neutral", false),
    ];

    public IReadOnlyList<GalleryProgress> Progress { get; } =
    [
        new("Normal", new("Draft", 0.4, "40% complete")),
        new("No progress reported", new("Review", null, "Running")),
        new("Not started", new("QA", null, "Not started")),
        new("Error", new("Publish", 0.75, "Failed")),
    ];

    public IReadOnlyList<GalleryDecision> Decisions { get; } =
    [
        new("Normal", new() { Question = "Accept revision 2 of the launch note?", Subject = "revision 2" }),
        new("Disabled", new() { Question = "No output is ready to decide on yet.", Enabled = false }),
        new("Busy", new() { Question = "Accept revision 2 of the launch note?", Subject = "revision 2", Busy = true }),
        new("Error", new() { Question = "Accept revision 2 of the launch note?", Subject = "revision 2", Error = "Your decision was not recorded. Check the workspace folder is writable, then try again." }),
    ];

    public IReadOnlyList<GalleryAgent> Agents { get; } =
    [
        new("Two people-configured agents on the same model", new("Builder North", "Builder", "Anthropic · claude-sonnet", true)),
        new("", new("Reviewer South", "Reviewer", "Anthropic · claude-sonnet", true)),
        new("Local endpoint", new("Workshop box", "Builder", "Local · http://localhost:11434", true)),
        new("Custom agent, no icon, no provider", new("Release helper", "Coordinator", null, true)),
    ];
}
