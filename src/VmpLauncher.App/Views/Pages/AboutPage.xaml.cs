using System.Windows.Controls;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class AboutPage : UserControl
{
    public AboutPage(AboutViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public AboutViewModel ViewModel { get; }
}
