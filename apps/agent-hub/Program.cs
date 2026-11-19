using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace LocalMind.AgentHub;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<HubApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class HubApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://LocalMind.AgentHub/")) { Source = new Uri("/Theme.axaml", UriKind.Relative) });
        Styles.Add(new StyleInclude(new Uri("avares://LocalMind.AgentHub/")) { Source = new Uri("/Styles.axaml", UriKind.Relative) });
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            if (desktop.Args?.Contains("--gallery") == true) { desktop.MainWindow = new Gallery.GalleryWindow(); base.OnFrameworkInitializationCompleted(); return; }
#endif
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
