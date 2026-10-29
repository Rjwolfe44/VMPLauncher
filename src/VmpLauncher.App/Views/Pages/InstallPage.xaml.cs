using System.Windows.Controls;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class InstallPage : UserControl
{
    public InstallPage(InstallViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Follow the log while it grows (like a terminal), unless the user scrolled up to read.
        viewModel.LinesAppended += () =>
        {
            if (LogList.Items.Count == 0)
            {
                return;
            }
            var sv = FindScrollViewer(LogList);
            if (sv is null || sv.VerticalOffset >= sv.ScrollableHeight - 24)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        };
    }

    private static ScrollViewer? FindScrollViewer(System.Windows.DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
