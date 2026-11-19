using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace LocalMind.AgentHub.Gallery;

public partial class GalleryWindow : Window
{
    public GalleryWindow()
    {
        InitializeComponent();
        var model = new GalleryViewModel();
        DataContext = model;
        foreach (var card in model.Cards)
        {
            CardHost.Children.Add(new TextBlock { Text = card.Label, Foreground = this.FindResource("Muted") as Avalonia.Media.IBrush });
            CardHost.Children.Add(new Controls.TaskCard { DataContext = card.Task, Note = card.Note, NoteTone = card.NoteTone, IsEnabled = card.Enabled });
        }
        // Show the keyboard-focus state on a real control.
        Opened += (_, _) => this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsVisible && b.IsEnabled && b.Content as string == "Go to blocker")
            ?.Focus(NavigationMethod.Tab);
    }
}
