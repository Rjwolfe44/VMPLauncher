using System.Windows.Controls;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class ModsPage : UserControl
{
    public ModsPage(ModsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.OnShownAsync();
        ContentArea.SizeChanged += (_, _) => LayoutDetails();
    }

    /// <summary>Wide windows show the mod page beside the list; narrow ones lay it over the list's right side.</summary>
    private void LayoutDetails()
    {
        var width = ContentArea.ActualWidth;
        var overlay = width < 1000;
        System.Windows.Controls.Grid.SetColumn(DetailsPane, overlay ? 0 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(DetailsPane, overlay ? 2 : 1);
        DetailsPane.HorizontalAlignment = overlay ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Stretch;
        DetailsPane.Width = overlay ? Math.Min(460, Math.Max(320, width - 24)) : 460;
        DetailsPane.Margin = overlay ? new System.Windows.Thickness(0) : new System.Windows.Thickness(14, 0, 0, 0);
        DetailsPane.Effect = overlay ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.55 } : null;
    }
}
