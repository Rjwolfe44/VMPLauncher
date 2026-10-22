using System.Windows;
using System.Windows.Controls;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

/// <summary>Code-built placeholder pages for features that land in later slices.</summary>
internal static class Placeholder
{
    public static UIElement Build(string title, string body)
    {
        var titleBlock = new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("Space.Style.Display") };
        var bodyBlock = new TextBlock { Text = body, Margin = new Thickness(0, 8, 0, 0), Style = (Style)Application.Current.FindResource("Space.Style.Secondary") };
        var panel = new StackPanel { Margin = (Thickness)Application.Current.FindResource("Space.Page.Padding"), MaxWidth = 1100 };
        panel.Children.Add(titleBlock);
        panel.Children.Add(bodyBlock);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

public sealed class ModsPage : Page
{
    public ModsPage(ModsViewModel vm)
    {
        DataContext = vm;
        Title = "Mods";
        Content = Placeholder.Build("Mods", "This pack is locked to match the server. A read-only browser of its mods arrives in a later update.");
    }
}
