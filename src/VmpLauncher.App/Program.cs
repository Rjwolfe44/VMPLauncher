using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using VmpLauncher.App.Hosting;
using VmpLauncher.App.Services;
using VmpLauncher.App.Views;
using VmpLauncher.Core;

namespace VmpLauncher.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack hooks must run before anything else (install/update/uninstall callbacks exit early).
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => ShellRegistration.TryRegister())
            .OnAfterUpdateFastCallback(_ => ShellRegistration.TryRegister())
            .OnBeforeUninstallFastCallback(_ => ShellRegistration.TryUnregister())
            .Run();

        var options = StartupOptions.Parse(args);
        // Moves 0.2.1-era data out of Velopack's install folder (Setup and uninstall would delete it).
        var (paths, migrationNotes) = options.DataDir is null ? DataMigration.ResolveDefault() : (new LauncherPaths(options.DataDir), Array.Empty<string>());
        paths.EnsureCreated();

        // CKAN reads CKAN_CONFIG_FILE in a static initializer: set it before any CKAN type is touched.
        Ckan.CkanBootstrap.Initialize(paths);

        using var single = SingleInstance.TryAcquire(paths.DataRoot);
        if (single is null)
        {
            SingleInstance.ForwardActivation(paths.DataRoot, options.Activation);
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = Array.Empty<string>(),
            DisableDefaults = true,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(paths.LogsDir, "launcher")));
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(options);
        builder.Services.AddLauncherServices();

        using var host = builder.Build();
        var log = host.Services.GetRequiredService<ILogger<App>>();
        log.LogInformation("VMP Launcher {Version} starting; data dir {DataRoot}", AppInfo.Version, paths.DataRoot);
        foreach (var note in migrationNotes)
        {
            log.LogWarning("Data migration: {Note}", note);
        }

        try
        {
            var app = new App(host.Services);
            single.ActivationReceived += activation => app.Dispatcher.BeginInvoke(() => app.HandleActivation(activation));
            app.InitializeComponent();
            var exit = app.Run(host.Services.GetRequiredService<MainWindow>());
            log.LogInformation("VMP Launcher exiting with code {Code}", exit);
            return exit;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal startup error");
            WriteCrashFile(paths, ex);
            MessageBox.Show(
                $"VMP Launcher could not start.{Environment.NewLine}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}A crash report was written to:{Environment.NewLine}{paths.LogsDir}",
                "VMP Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static void WriteCrashFile(LauncherPaths paths, Exception ex)
    {
        try
        {
            var file = Path.Combine(paths.LogsDir, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(file, $"VMP Launcher {AppInfo.Version}{Environment.NewLine}{Environment.OSVersion}{Environment.NewLine}{Environment.NewLine}{ex}");
        }
        catch (Exception)
        {
        }
    }
}
