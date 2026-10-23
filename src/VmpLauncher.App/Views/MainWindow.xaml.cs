using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.Services;
using VmpLauncher.App.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace VmpLauncher.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly AppNavigator _navigator;
    private readonly AccessibilityService _accessibility;
    private readonly StartupOptions _options;
    private readonly AppSettingsStore _settings;
    private readonly ISnackbarService _snackbar;
    private readonly HostToolsService _host;
    private readonly ILogger<MainWindow> _log;

    public MainWindow(
        MainWindowViewModel viewModel,
        AppNavigator navigator,
        ISnackbarService snackbar,
        IContentDialogService dialogs,
        AccessibilityService accessibility,
        StartupOptions options,
        AppSettingsStore settings,
        OperationRunner runner,
        HostToolsService host,
        HomeViewModel home,
        IActivationService activation,
        ILogger<MainWindow> log)
    {
        _settings = settings;
        _log = log;
        _navigator = navigator;
        _accessibility = accessibility;
        _options = options;
        _snackbar = snackbar;
        _host = host;
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        navigator.Attach(PageHost);
        snackbar.SetSnackbarPresenter(SnackbarPresenter);
        dialogs.SetDialogHost(DialogHost);

        runner.Finished += outcome => Dispatcher.BeginInvoke(() => ShowOutcome(outcome));
        Loaded += (_, _) =>
        {
            ApplyTextScale();
            var requested = AppNavigator.Parse(_options.StartPage);
            if (requested == AppPage.Host && !_host.IsHost)
            {
                requested = null;
            }
            var start = requested ?? (_settings.Current.WelcomeCompleted ? AppPage.Home : AppPage.Welcome);
            _log.LogInformation("Start page {Page} (requested '{Requested}', welcomeCompleted={Welcome})", start, _options.StartPage, _settings.Current.WelcomeCompleted);
            _navigator.Navigate(start);
            if (start != AppPage.Welcome)
            {
                _ = home.OnShownAsync();
            }
            if (!string.IsNullOrWhiteSpace(_options.Activation))
            {
                activation.Handle(_options.Activation);
            }
        };
        settings.Changed += _ => Dispatcher.BeginInvoke(ApplyTextScale);
        accessibility.Changed += ApplyTextScale;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public MainWindowViewModel ViewModel { get; }

    private void ShowOutcome(OperationOutcome o)
    {
        if (o.Kind == OperationKind.Publish)
        {
            return; // the Host page reports its own result
        }
        var (appearance, icon, title) = o.Succeeded
            ? (ControlAppearance.Success, SymbolRegular.CheckmarkCircle24, o.Title + " finished")
            : o.Cancelled
                ? (ControlAppearance.Caution, SymbolRegular.Warning24, o.Title + " cancelled")
                : (ControlAppearance.Danger, SymbolRegular.ErrorCircle24, o.Title + " failed");
        _snackbar.Show(title, o.Message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(o.Succeeded ? 5 : 9));
    }

    private void ApplyTextScale()
    {
        var scale = Math.Clamp(_settings.Current.TextScale * _accessibility.SystemTextScale, 1.0, 2.25);
        var root = (FrameworkElement)Content;
        root.LayoutTransform = scale == 1.0 ? null : new System.Windows.Media.ScaleTransform(scale, scale);
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || ViewModel.IsOnboarding)
        {
            return;
        }
        AppPage? page = e.Key switch
        {
            Key.D1 or Key.NumPad1 => AppPage.Home,
            Key.D2 or Key.NumPad2 => AppPage.Play,
            Key.D3 or Key.NumPad3 => AppPage.Mods,
            Key.D4 or Key.NumPad4 => AppPage.Activity,
            Key.D5 or Key.NumPad5 => AppPage.Doctor,
            Key.OemComma => AppPage.Settings,
            _ => null,
        };
        if (page is { } p)
        {
            _navigator.Navigate(p);
            e.Handled = true;
        }
    }
}
