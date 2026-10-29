using System.Windows.Controls;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (string.IsNullOrEmpty(ViewModel.CacheSize))
            {
                await ViewModel.MeasureCacheCommand.ExecuteAsync(null);
            }
        };
    }

    public SettingsViewModel ViewModel { get; }
}
