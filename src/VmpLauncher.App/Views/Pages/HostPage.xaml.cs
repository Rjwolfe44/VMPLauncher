using System.Collections.Specialized;
using System.Windows.Controls;
using VmpLauncher.App.ViewModels;

namespace VmpLauncher.App.Views.Pages;

public partial class HostPage : UserControl
{
    public HostPage(HostViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Log.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HostViewModel.TokenInput) && viewModel.TokenInput.Length == 0 && TokenBox.Password.Length > 0)
            {
                TokenBox.Password = string.Empty;
            }
        };
    }

    public HostViewModel ViewModel { get; }

    private void OnTokenChanged(object sender, System.Windows.RoutedEventArgs e) => ViewModel.TokenInput = TokenBox.Password;
}
