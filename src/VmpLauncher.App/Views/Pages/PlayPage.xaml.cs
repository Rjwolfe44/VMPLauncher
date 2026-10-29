using System.Windows.Controls;
using System.Windows.Input;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class PlayPage : UserControl
{
    public PlayPage(PlayViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayViewModel.PasswordInput) && ViewModel.PasswordInput.Length == 0 && PasswordBox.Password.Length > 0)
            {
                PasswordBox.Password = string.Empty;
            }
        };
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

    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) => ViewModel.PasswordInput = PasswordBox.Password;

    private async void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ViewModel.SavePasswordCommand.ExecuteAsync(null);
            PasswordBox.Password = string.Empty;
        }
    }
}
