using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.Services;
using Wpf.Ui.Appearance;

namespace VmpLauncher.App;

public partial class App : Application
{
    public App(IServiceProvider services)
    {
        Services = services;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.GetRequiredService<ILogger<App>>().LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public IServiceProvider Services { get; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Theme first (it can reset the accent), then the brand accent (Shikigami purple).
        // Status colours live in Space.xaml and are never derived from the accent.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        ApplicationAccentColorManager.Apply(Color.FromRgb(0xB6, 0x9C, 0xFF), ApplicationTheme.Dark, false, false);
        Services.GetRequiredService<AccessibilityService>().Start();
        Services.GetRequiredService<UpdateService>().Start();
    }

    /// <summary>Called for a vmpack:// link or .vmpack file, both at startup and when forwarded from a second instance.</summary>
    public void HandleActivation(string? activation)
    {
        if (string.IsNullOrWhiteSpace(activation))
        {
            MainWindow?.Activate();
            return;
        }
        Services.GetRequiredService<IActivationService>().Handle(activation);
        MainWindow?.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.GetRequiredService<ILogger<App>>().LogCritical(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        MessageBox.Show(
            "VMP Launcher hit an unexpected error. Details were written to the log folder (Settings → Open logs).\n\n" + e.Exception.Message,
            "VMP Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
