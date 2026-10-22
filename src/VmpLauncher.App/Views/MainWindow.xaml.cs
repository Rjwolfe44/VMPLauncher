using System.Windows;
using System.Windows.Input;
using VmpLauncher.App.Services;
using VmpLauncher.App.ViewModels;
using VmpLauncher.App.Views.Pages;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace VmpLauncher.App.Views;

public partial class MainWindow : FluentWindow, INavigationWindow
{
    private readonly INavigationService _navigation;
    private readonly AccessibilityService _accessibility;
    private readonly StartupOptions _options;
    private readonly AppSettingsStore _settings;
    private readonly Microsoft.Extensions.Logging.ILogger<MainWindow> _log;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationViewPageProvider pageProvider,
        INavigationService navigation,
        ISnackbarService snackbar,
        IContentDialogService dialogs,
        AccessibilityService accessibility,
        StartupOptions options,
        AppSettingsStore settings,
        Microsoft.Extensions.Logging.ILogger<MainWindow> log)
    {
        _settings = settings;
        _log = log;
        ViewModel = viewModel;
        _navigation = navigation;
        _accessibility = accessibility;
        _options = options;
        DataContext = viewModel;
        InitializeComponent();

        SetPageService(pageProvider);
        navigation.SetNavigationControl(RootNavigation);
        snackbar.SetSnackbarPresenter(SnackbarPresenter);
        dialogs.SetDialogHost(DialogHost);

        ApplyBackdrop();
        accessibility.Changed += ApplyBackdrop;
        Loaded += (_, _) =>
        {
            ApplyTextScale();
            var start = _options.StartPage?.ToLowerInvariant() switch
            {
                "home" => typeof(HomePage),
                "play" => typeof(PlayPage),
                "install" or "activity" => typeof(InstallPage),
                "doctor" => typeof(DoctorPage),
                "mods" => typeof(ModsPage),
                "settings" => typeof(SettingsPage),
                "about" => typeof(AboutPage),
                "welcome" => typeof(WelcomePage),
                _ => _settings.Current.WelcomeCompleted ? typeof(HomePage) : typeof(WelcomePage),
            };
            System.Diagnostics.Debug.WriteLine($"start page {start.Name}");
            _log.LogInformation("Start page {Page} (requested '{Requested}', welcomeCompleted={Welcome}, settings {File})", start.Name, _options.StartPage, _settings.Current.WelcomeCompleted, _settings.Current.SourceKspPath ?? "(no ksp)");
            _navigation.Navigate(start);
        };
        settings.Changed += _ => Dispatcher.BeginInvoke(ApplyTextScale);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public MainWindowViewModel ViewModel { get; }

    private void ApplyTextScale()
    {
        var scale = Math.Clamp(_settings.Current.TextScale * _accessibility.SystemTextScale, 1.0, 2.25);
        RootNavigation.LayoutTransform = scale == 1.0 ? null : new System.Windows.Media.ScaleTransform(scale, scale);
    }

    private void ApplyBackdrop()
    {
        var mica = AppInfo.IsWindows11 && !_options.NoBackdrop && !_accessibility.HighContrast;
        WindowBackdropType = mica ? WindowBackdropType.Mica : WindowBackdropType.None;
        Background = mica ? System.Windows.Media.Brushes.Transparent : (System.Windows.Media.Brush)FindResource("Space.Bg.BaseBrush");
        if (_accessibility.ReducedMotion)
        {
            RootNavigation.Transition = Wpf.Ui.Animations.Transition.None;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }
        Type? page = e.Key switch
        {
            Key.D1 => typeof(HomePage),
            Key.D2 => typeof(PlayPage),
            Key.D3 => typeof(InstallPage),
            Key.D4 => typeof(DoctorPage),
            Key.D5 => typeof(ModsPage),
            Key.OemComma => typeof(SettingsPage),
            _ => null,
        };
        if (page is not null)
        {
            _navigation.Navigate(page);
            e.Handled = true;
        }
    }

    // INavigationWindow
    public INavigationView GetNavigation() => RootNavigation;
    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);
    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) => RootNavigation.SetPageProviderService(navigationViewPageProvider);
    public void ShowWindow() => Show();
    public void CloseWindow() => Close();
    public void SetServiceProvider(IServiceProvider serviceProvider) => RootNavigation.SetServiceProvider(serviceProvider);
}
