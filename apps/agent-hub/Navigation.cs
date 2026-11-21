using System.Text.Json;
using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

public sealed record ScreenArea(int X, int Y, int Width, int Height, bool Primary = false);
public sealed record WindowPlacement(int X, int Y, int Width, int Height);
public sealed record SavedView(string Name, string ProjectId, string? Owner, bool BlockedOnly, string Review);
public sealed record SavedLayout(double ProjectsWidth = 240, double InspectorWidth = 280, string? ProjectId = null, string View = "board", WindowPlacement? Window = null, List<SavedView>? Views = null);

/// <summary>One file per workspace, so two workspaces keep their own pane sizes, project, view and saved filters.</summary>
public sealed class LayoutStore(string folder)
{
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMind", "AgentHub", "layouts");
    private string FileFor(string workspaceId) => Path.Combine(folder, "layout-" + string.Concat(workspaceId.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".json");

    public SavedLayout? Load(string workspaceId)
    {
        try { return JsonSerializer.Deserialize<SavedLayout>(File.ReadAllText(FileFor(workspaceId))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Best effort: a layout that cannot be saved must never stop the app.</summary>
    public void Save(string workspaceId, SavedLayout layout)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string temp = FileFor(workspaceId) + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(layout));
            File.Move(temp, FileFor(workspaceId), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Keeps a saved window where it can be seen. If its monitor is gone it is centred on the primary one, shrunk to fit.</summary>
    public static WindowPlacement Fit(WindowPlacement w, IReadOnlyList<ScreenArea> screens)
    {
        if (screens.Count == 0) return w;
        long Overlap(ScreenArea s) => Math.Max(0, Math.Min(w.X + w.Width, s.X + s.Width) - Math.Max(w.X, s.X)) * (long)Math.Max(0, Math.Min(w.Y + w.Height, s.Y + s.Height) - Math.Max(w.Y, s.Y));
        var best = screens.OrderByDescending(Overlap).First();
        if (Overlap(best) >= Math.Min(w.Width, 400) * (long)Math.Min(w.Height, 200))
        {
            int width = Math.Min(w.Width, best.Width), height = Math.Min(w.Height, best.Height);
            return new(Math.Clamp(w.X, best.X, best.X + best.Width - width), Math.Clamp(w.Y, best.Y, best.Y + best.Height - height), width, height);
        }
        var home = screens.FirstOrDefault(s => s.Primary) ?? screens[0];
        int hw = Math.Min(w.Width, home.Width), hh = Math.Min(w.Height, home.Height);
        return new(home.X + (home.Width - hw) / 2, home.Y + (home.Height - hh) / 2, hw, hh);
    }
}

/// <summary>Filters by owner, blocker and review state, plus private search text. Each project has its own.</summary>
public sealed record BoardFilter(string? Owner = null, bool BlockedOnly = false, string Review = "any", string Search = "")
{
    public bool IsEmpty => this == new BoardFilter();
    public bool Matches(TaskItem t) =>
        (Owner == null || t.OwnerLabel == Owner) && (!BlockedOnly || t.HasBlocker)
        && Review switch { "needs" => t.ReviewRequired && !t.ReviewAccepted, "accepted" => t.ReviewAccepted, "none" => !t.ReviewRequired, _ => true }
        && (Search.Length == 0 || t.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || (t.Outcome?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false));
}

public sealed record MenuEntry(string Kind, string Title, string Detail, string? Shortcut, bool Enabled, string? DisabledReason, Func<Task> Run)
{
    public string Label => Enabled ? Title : $"{Title} — unavailable: {DisabledReason}";
    public double Opacity => Enabled ? 1 : 0.6;
    public string Hint => (Shortcut ?? "") is { Length: > 0 } s ? $"{Detail}  ·  {s}" : Detail;
}

/// <summary>
/// The board's order, held steady while the owner is working in it. New or removed cards wait as a count until the owner asks to see them,
/// so the cards under selection, focus and the scroll anchor never move.
/// </summary>
public sealed class SteadyBoard
{
    private List<string> shown = [], latest = [];
    public HashSet<string> Selected { get; } = [];
    public string? Focus { get; set; }
    public string? Anchor { get; set; }
    public IReadOnlyList<string> Shown => shown;
    public int Pending => latest.Except(shown).Count() + shown.Except(latest).Count();
    public bool Holding => Selected.Count > 0 || (Anchor != null && shown.IndexOf(Anchor) > 0);
    public int IndexOf(string id) => shown.IndexOf(id);

    public void Update(IReadOnlyList<string> order)
    {
        latest = [.. order];
        if (!Holding || shown.Count == 0) shown = [.. order];
    }

    /// <summary>The explicit way to see what arrived. Selection and focus stay; the anchor card keeps its place by ID.</summary>
    public void Reveal() => shown = [.. latest];
}

/// <summary>A brief animation belongs to one persisted handoff event. Opening, replaying, reconnecting and pausing leave the graph idle.</summary>
public sealed class HandoffAnimator
{
    public long Seen { get; private set; }
    public bool Reduced { get; set; }
    public bool Paused { get; set; }
    public HandoffEvent? Active { get; private set; }

    public void Baseline(long seq) { Seen = Math.Max(Seen, seq); Active = null; }

    public HandoffEvent? Live(IReadOnlyList<HandoffEvent> events)
    {
        var fresh = events.Where(e => e.Seq > Seen).ToList();
        if (fresh.Count == 0) return null;
        Seen = fresh.Max(e => e.Seq);
        return Active = Paused || Reduced ? null : fresh[^1];
    }

    public void Finish() => Active = null;
    public void SetReduced(bool on) { Reduced = on; if (on) Active = null; }
}

public sealed record PlanNodeRow(PlanNode Node, string Requires, string Feeds, bool Pulse = false)
{
    public string Title => Node.Title;
    public string StateLabel => Node.State switch
    {
        "accepted" => "Accepted", "running" => "Running", "awaiting_review" => "Waiting for your decision", "changes_requested" => "Changes requested",
        "failed" => "Failed", "ready" => "Ready", _ => "Waiting",
    };
    public string Direction => (Requires.Length > 0 ? $"Needs: {Requires}" : "Needs nothing") + (Feeds.Length > 0 ? $"  →  Hands off to: {Feeds}" : "");
    public string BlockerText => Node.Blockers.Count == 0 ? "" : string.Join(" ", Node.Blockers.Select(b => b.Reason));
    public bool HasBlocker => Node.Blockers.Count > 0;
    public string HandoffLabel => Pulse ? "● A handoff just arrived here" : "";
    public string Seq => $"event {Node.LastSeq}";
    public string AutomationName => $"{Title}, {StateLabel}. {Direction}. {BlockerText}";
    public override string ToString() => AutomationName;
}
