using System.Windows.Controls;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class DoctorPage : UserControl
{
    public DoctorPage(DoctorViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // First visit runs the checks straight away; later visits keep the last result until "Run checks".
        Loaded += async (_, _) =>
        {
            _ = ViewModel.CheckPackAsync();
            if (ViewModel.Steps.Count == 0 && !ViewModel.IsRunning)
            {
                await ViewModel.RunCommand.ExecuteAsync(null);
            }
        };
    }

    public DoctorViewModel ViewModel { get; }
}
