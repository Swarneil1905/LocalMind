using LocalMind.AgentHub.Core;

namespace LocalMind.AgentHub;

public sealed record VisualRow(string Label, string ValueText, double Bar, string AutomationName, double BarLeft, double ZeroLeft, double DotLeft, bool IsBar);

/// <summary>AH55 · a bar or line chart drawn from the revision shown, as a list the keyboard can walk, and a static SVG that names the revision it came from.</summary>
public sealed partial class HubViewModel
{
    private string? visualFor; private VisualResult visual = new(null, null);
    private VisualResult Visual { get { string t = OutputText; if (visualFor != t) { visualFor = t; visual = Visuals.Parse(t); VisualExportStatus = ""; } return visual; } }
    public string VisualFolder { get; set; } = System.IO.Path.Combine(LaunchOptions.DataRoot, "exports");
    public bool VisualVisible => Visual.Chart != null;
    public bool VisualRefused => Visual.Refusal != null;
    public string VisualRefusal => Visual.Refusal ?? "";
    public string VisualExportStatus { get; private set; } = "";
    public string VisualHeading => Visual.Chart is { } c && Review is { } r ? $"{c.Title} · {c.Kind} chart from revision {r.Revision} of {selectedTask?.Title}" : "";
    public const double TrackWidth = 120, PlotWidth = 240, PlotHeight = 90;
    public bool VisualIsLine => Visual.Chart?.Kind == "line";
    /// <summary>Each row places its value on the same axis the SVG uses (zero included): a bar runs from zero toward its sign, a line chart shows a dot. The name says the sign and the change from the previous point.</summary>
    public IReadOnlyList<VisualRow> VisualRows => Visual.Chart is { } c ? Rows(c) : [];
    public static IReadOnlyList<VisualRow> Rows(Chart c)
    {
        {
            var (min, _, span) = Visuals.Range(c);
            double X(double v) => (v - min) / span * TrackWidth; double zero = X(0);
            string u = c.Unit.Length > 0 ? " " + c.Unit : "";
            return [.. c.Points.Select((p, i) =>
            {
                double x = X(p.Value);
                string sign = p.Value < 0 ? "negative" : p.Value > 0 ? "positive" : "zero";
                string trend = i == 0 ? "" : p.Value > c.Points[i - 1].Value ? $", up {p.Value - c.Points[i - 1].Value:0.##} from previous" : p.Value < c.Points[i - 1].Value ? $", down {c.Points[i - 1].Value - p.Value:0.##} from previous" : ", unchanged from previous";
                return new VisualRow(p.Label, p.Value.ToString("0.##") + u, Math.Max(Math.Abs(x - zero), 1), $"{p.Label}: {p.Value:0.##}{u}, {sign}{trend}, point {i + 1} of {c.Points.Count}", Math.Min(x, zero), zero, x - 5, c.Kind == "bar");
            })];
        }
    }
    /// <summary>The line's shape, from the same axis and spacing as the SVG; empty for a bar chart.</summary>
    public IList<Avalonia.Point> VisualLine => Visual.Chart is { Kind: "line" } c ? Plot(c) : [];
    public static IList<Avalonia.Point> Plot(Chart c)
    {
        {
            var (min, _, span) = Visuals.Range(c); double step = PlotWidth / c.Points.Count;
            return [.. c.Points.Select((p, i) => new Avalonia.Point(step * (i + .5), PlotHeight - (p.Value - min) / span * PlotHeight))];
        }
    }
    public IList<Avalonia.Point> VisualZeroLine
    {
        get
        {
            if (Visual.Chart is not { Kind: "line" } c) return [];
            var (min, _, span) = Visuals.Range(c); double y = PlotHeight - (0 - min) / span * PlotHeight;
            return [new(0, y), new(PlotWidth, y)];
        }
    }
    public WorkspaceCommand ExportVisualCommand { get; private set; } = null!;
    private static readonly string[] VisualProps = [nameof(VisualVisible), nameof(VisualRefused), nameof(VisualRefusal), nameof(VisualHeading), nameof(VisualRows), nameof(VisualIsLine), nameof(VisualLine), nameof(VisualZeroLine), nameof(VisualExportStatus)];

    private WorkspaceCommand[] InitVisuals()
    {
        ExportVisualCommand = new(_ => Visual.Chart != null && Review is { HasOutput: true, OutputId: not null }, _ =>
        {
            var c = Visual.Chart!; var r = Review!; string source = OutputText;
            string svg = Visuals.ExportSvg(c, selectedTask!.Id, selectedTask.Title, r.Revision, r.OutputId!, source);
            string file = System.IO.Path.Combine(VisualFolder, $"chart-{selectedTask.Id[..Math.Min(8, selectedTask.Id.Length)]}-r{r.Revision}-{Visuals.Fingerprint(source)}.svg");
            try
            {
                Directory.CreateDirectory(VisualFolder);
                // The name carries the revision and a fingerprint of its text, so the same revision always lands in the same file and nothing else is overwritten.
                if (File.Exists(file) && File.ReadAllText(file) == svg) VisualExportStatus = $"Already exported: {file}. It is a static picture of revision {r.Revision}.";
                else { using var f = new FileStream(file, FileMode.CreateNew, FileAccess.Write); using var w = new StreamWriter(f, new System.Text.UTF8Encoding(false)); w.Write(svg); VisualExportStatus = $"Exported a static picture of revision {r.Revision} to {file}. It will not change if the task gets a newer revision."; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { VisualExportStatus = $"The picture could not be saved ({e.Message.TrimEnd('.')}). Choose another folder or free some space, then try again."; }
            Notify(nameof(VisualExportStatus)); return Task.CompletedTask;
        });
        return [ExportVisualCommand];
    }
}
