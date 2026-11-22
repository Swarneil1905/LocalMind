using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;

namespace LocalMind.AgentHub;

/// <summary>Switches and values given on the command line. Used to pin a deterministic window for baselines and to open a workspace for a measurement; the owner never needs them.</summary>
public static class LaunchOptions
{
    public static string? Open { get; private set; }
    public static string? Theme { get; private set; }
    public static double? Scale { get; private set; }
    public static (int W, int H)? Size { get; private set; }
    public static string? PerfFile { get; private set; }
    /// <summary>A recorded audio file standing in for the microphone, and the words a check scripted for it (there is no real transcriber in this build).</summary>
    public static string? VoiceFixture { get; private set; }
    public static string? VoiceText { get; private set; }
    public static bool VoiceDenied { get; private set; }
    public static int VoiceDelay { get; private set; }
    /// <summary>A bare path given on the command line (open this legacy file). Switches and their values are not paths.</summary>
    public static string? File { get; private set; }
    /// <summary>Where this window keeps drafts, layouts, the tutorial and feedback reports. <c>--profile dir</c> gives a disposable, empty one.</summary>
    public static string DataRoot { get; private set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMind", "AgentHub");

    public static void Parse(string[] args)
    {
        string[] withValue = ["--open", "--theme", "--scale", "--size", "--perf", "--profile", "--voice-fixture", "--voice-text", "--voice-delay"];
        for (int i = 0; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            if (!args[i].StartsWith("--") && (i == 0 || !withValue.Contains(args[i - 1])) && File == null) File = args[i];
            switch (args[i])
            {
                case "--open" when next != null: Open = next; break;
                case "--voice-fixture" when next != null: VoiceFixture = next; break;
                case "--voice-text" when next != null: VoiceText = next; break;
                case "--voice-denied": VoiceDenied = true; break;
                case "--voice-delay" when int.TryParse(next, out var vd): VoiceDelay = vd; break;
                case "--theme" when next != null: Theme = next; break;
                case "--scale" when double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s): Scale = s; break;
                case "--size" when next != null && next.Split('x') is [var w, var h] && int.TryParse(w, out var wi) && int.TryParse(h, out var hi): Size = (wi, hi); break;
                case "--perf" when next != null: PerfFile = next; break;
                case "--profile" when next != null: DataRoot = System.IO.Path.GetFullPath(next); break;
            }
        }
    }
}

public sealed record Palette(string Name, IReadOnlyDictionary<string, string> Colors);

/// <summary>Dark, light and high-contrast colour sets over the same names the window already uses, a text size, and the contrast arithmetic the checks apply to each pair.</summary>
public static class Appearance
{
    public static readonly string[] ThemeNames = ["System", "Dark", "Light", "High contrast"];
    public static readonly double[] Scales = [1.0, 1.25, 1.5, 2.0];

    public static readonly Palette Dark = new("Dark", new Dictionary<string, string>
    {
        ["Canvas"] = "#111B21", ["Pane"] = "#152127", ["Line"] = "#36474A", ["Text"] = "#EEF5F1", ["Muted"] = "#BDD0CB", ["Badge"] = "#294238", ["BadgeText"] = "#BAE9CE",
        ["Focus"] = "#7FD6A9", ["Selected"] = "#203A3A", ["Hover"] = "#1A2B30", ["ErrorText"] = "#FFB4A8", ["ErrorFill"] = "#3D2623", ["BusyFill"] = "#2C3A22",
    });
    public static readonly Palette Light = new("Light", new Dictionary<string, string>
    {
        ["Canvas"] = "#F4F7F6", ["Pane"] = "#FFFFFF", ["Line"] = "#6F827D", ["Text"] = "#14201D", ["Muted"] = "#3F514C", ["Badge"] = "#D6EEDD", ["BadgeText"] = "#0E4A2B",
        ["Focus"] = "#005FCC", ["Selected"] = "#DCEBE6", ["Hover"] = "#EAF2EF", ["ErrorText"] = "#9B1C1C", ["ErrorFill"] = "#FBE3E0", ["BusyFill"] = "#EDF3D6",
    });
    public static readonly Palette HighContrast = new("High contrast", new Dictionary<string, string>
    {
        ["Canvas"] = "#000000", ["Pane"] = "#000000", ["Line"] = "#FFFFFF", ["Text"] = "#FFFFFF", ["Muted"] = "#E6E6E6", ["Badge"] = "#000000", ["BadgeText"] = "#7DF9FF",
        ["Focus"] = "#FFFF00", ["Selected"] = "#003A70", ["Hover"] = "#1F1F1F", ["ErrorText"] = "#FF9C9C", ["ErrorFill"] = "#3A0000", ["BusyFill"] = "#222200",
    });

    /// <summary>Text pairs must reach 4.5:1; the focus outline and the dividing line must reach 3:1 against the pane they sit on.</summary>
    public static readonly (string Fg, string Bg, double Min, string What)[] Pairs =
    [
        ("Text", "Canvas", 4.5, "body text"), ("Text", "Pane", 4.5, "pane text"), ("Muted", "Canvas", 4.5, "secondary text"), ("Muted", "Pane", 4.5, "secondary text in a pane"),
        ("BadgeText", "Badge", 4.5, "badge text"), ("ErrorText", "ErrorFill", 4.5, "error text"), ("Text", "Selected", 4.5, "text on a selected row"), ("Text", "Hover", 4.5, "text on a hovered row"),
        ("Text", "BusyFill", 4.5, "text on a busy row"), ("Focus", "Canvas", 3.0, "focus outline on the canvas"), ("Focus", "Pane", 3.0, "focus outline in a pane"), ("Focus", "Selected", 3.0, "focus outline on a selected row"),
        ("Line", "Pane", 1.5, "dividers (decorative; control borders come from the Fluent theme and are checked by screenshot, not by this table)"),
    ];

    public static double Luminance(string hex)
    {
        var c = Color.Parse(hex);
        double F(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    public static double Contrast(string a, string b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    public static IReadOnlyList<string> Failures(Palette p) =>
        [.. Pairs.Where(t => Contrast(p.Colors[t.Fg], p.Colors[t.Bg]) < t.Min).Select(t => $"{p.Name}: {t.What} ({t.Fg} on {t.Bg}) is {Contrast(p.Colors[t.Fg], p.Colors[t.Bg]):0.00}:1, below {t.Min}:1")];

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, ref HighContrastInfo info, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
    [StructLayout(LayoutKind.Sequential)] private struct HighContrastInfo { public uint Size; public uint Flags; public IntPtr Scheme; }

    /// <summary>True when Windows itself is in a high-contrast theme.</summary>
    public static bool SystemHighContrast
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            var info = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
            return SystemParametersInfo(0x0042, info.Size, ref info, 0) && (info.Flags & 1) != 0;
        }
    }

    public static Palette Resolve(string? choice) => choice switch
    {
        "Light" or "light" => Light,
        "High contrast" or "contrast" => HighContrast,
        "Dark" or "dark" => Dark,
        _ => SystemHighContrast ? HighContrast : Dark,
    };

    public static event Action<double>? ScaleChanged;
    public static string CurrentName { get; private set; } = "Dark";
    public static double CurrentScale { get; private set; } = 1.0;

    public static void Apply(Palette p, double scale)
    {
        CurrentName = p.Name; CurrentScale = scale;
        if (Application.Current is { } app)
        {
            foreach (var (k, v) in p.Colors) app.Resources[k] = new SolidColorBrush(Color.Parse(v));
            app.RequestedThemeVariant = p.Name == "Light" ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark; // the standard controls follow the palette
        }
        ScaleChanged?.Invoke(scale);
    }
}

/// <summary>Whether the operating system asks for less animation.</summary>
public static class Motion
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
    public static bool Reduced
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            return SystemParametersInfo(0x1042, 0, out bool on, 0) && !on; // SPI_GETCLIENTAREAANIMATION
        }
    }
}

/// <summary>
/// Optional latency sampling for measurements (<c>--perf file</c>). A sample runs from the moment the window receives a key press or wheel turn to the first idle turn of the UI thread
/// after layout and rendering of its effect have been queued and run. It does not include the display's own scan-out.
/// </summary>
public static class Perf
{
    private static StreamWriter? log;
    private static readonly object gate = new();
    public static bool Enabled => log != null;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Start(string file) { log = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; Event("process", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds); }

    public static void Event(string kind, double ms) { if (log == null) return; lock (gate) log.WriteLine(JsonSerializer.Serialize(new { kind, ms = Math.Round(ms, 2), at = DateTime.UtcNow.ToString("O") })); }

    public static long Begin() => Clock.ElapsedTicks;
    public static void End(string kind, long start) => Event(kind, (Clock.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency);
    public static double SinceProcessStart() => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
}
