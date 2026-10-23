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
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.None, false);
        ApplyBrandAccent();
        Services.GetRequiredService<AccessibilityService>().Start();
        Services.GetRequiredService<UpdateService>().Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.GetRequiredService<UpdateService>().ApplyOnExit();
        base.OnExit(e);
    }

    /// <summary>
    /// Shikigami purple as the Fluent accent. WPF-UI derives lighter shades for dark themes, which turned #B69CFF into
    /// near-white buttons, so every accent shade is pinned explicitly and the brushes are re-pointed at the palette.
    /// </summary>
    private void ApplyBrandAccent()
    {
        var accent = Color.FromRgb(0xB6, 0x9C, 0xFF);
        ApplicationAccentColorManager.Apply(accent, Color.FromRgb(0xA3, 0x86, 0xF6), Color.FromRgb(0xB6, 0x9C, 0xFF), Color.FromRgb(0xC9, 0xB5, 0xFF));
        var res = Resources;
        void Brush(string key, Color c) => res[key] = new SolidColorBrush(c);
        Brush("AccentFillColorDefaultBrush", accent);
        Brush("AccentFillColorSecondaryBrush", Color.FromArgb(0xE6, 0xB6, 0x9C, 0xFF));
        Brush("AccentFillColorTertiaryBrush", Color.FromArgb(0xCC, 0xB6, 0x9C, 0xFF));
        Brush("AccentFillColorDisabledBrush", Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
        Brush("AccentTextFillColorPrimaryBrush", Color.FromRgb(0xC9, 0xB5, 0xFF));
        Brush("AccentTextFillColorSecondaryBrush", Color.FromRgb(0xC9, 0xB5, 0xFF));
        Brush("AccentTextFillColorTertiaryBrush", accent);
        Brush("TextOnAccentFillColorPrimaryBrush", Color.FromRgb(0x1A, 0x10, 0x33));
        Brush("TextOnAccentFillColorSecondaryBrush", Color.FromArgb(0xB3, 0x1A, 0x10, 0x33));
        Brush("TextOnAccentFillColorDisabledBrush", Color.FromArgb(0x87, 0xFF, 0xFF, 0xFF));
        Brush("TextOnAccentFillColorSelectedTextBrush", Color.FromRgb(0x1A, 0x10, 0x33));
        Brush("SystemAccentColorPrimaryBrush", accent);
        Brush("SystemAccentColorSecondaryBrush", accent);
        Brush("SystemAccentColorTertiaryBrush", Color.FromRgb(0xC9, 0xB5, 0xFF));
        Brush("AccentFillColorSelectedTextBackgroundBrush", Color.FromRgb(0x7C, 0x5C, 0xE0));
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
            "VMP Launcher hit an unexpected error. Details were written to the log folder (Settings, Open logs).\n\n" + e.Exception.Message,
            "VMP Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
