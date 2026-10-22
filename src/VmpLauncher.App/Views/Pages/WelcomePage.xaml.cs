using System.Windows.Controls;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class WelcomePage : Page
{
    public WelcomePage(WelcomeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public WelcomeViewModel ViewModel { get; }

    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) => ViewModel.Password = PasswordBox.Password;
}
