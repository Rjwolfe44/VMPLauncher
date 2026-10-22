using System.Windows.Controls;
using System.Windows.Input;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class PlayPage : Page
{
    public PlayPage(PlayViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadCommand.ExecuteAsync(null);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                ViewModel.CheckRoutesCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    public PlayViewModel ViewModel { get; }

    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.PasswordInput = PasswordBox.Password;
    }
}
