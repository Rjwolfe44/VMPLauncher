using Microsoft.Extensions.DependencyInjection;
using VmpLauncher.App.Services;
using VmpLauncher.App.ViewModels;
using VmpLauncher.App.Views;
using VmpLauncher.App.Views.Pages;
using Wpf.Ui;

namespace VmpLauncher.App.Hosting;

public static class ServiceRegistration
{
    public static IServiceCollection AddLauncherServices(this IServiceCollection services)
    {
        // WPF-UI services we still use (toasts and dialogs); navigation is our own shell.
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();

        // Launcher services
        services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        services.AddSingleton<AccessibilityService>();
        services.AddSingleton<AppNavigator>();
        services.AddSingleton<IAppNavigator>(sp => sp.GetRequiredService<AppNavigator>());
        services.AddSingleton<IActivationService, ActivationService>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<OperationRunner>();
        services.AddSingleton<PackCatalogService>();
        services.AddSingleton<PrivateModsService>();
        // CKAN is not thread-safe: the pack and the mod manager share one CKAN thread.
        services.AddSingleton<VmpLauncher.Ckan.Backend.CkanWorker>();
        services.AddSingleton<LauncherService>();
        services.AddSingleton<SetupService>();
        services.AddSingleton<ModManagerService>();
        services.AddSingleton<ModInfoService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IRouteStatusProvider, ProbeRouteStatusProvider>();
        services.AddSingleton<ServerRoutesService>();
        services.AddSingleton<ServerStatusService>();
        services.AddSingleton<HostToolsService>();

        // Shell
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();

        // Pages (singletons keep their state while navigating)
        services.AddSingleton<WelcomePage>();
        services.AddSingleton<WelcomeViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<PlayPage>();
        services.AddSingleton<PlayViewModel>();
        services.AddSingleton<ModsPage>();
        services.AddSingleton<ModsViewModel>();
        services.AddSingleton<InstallPage>();
        services.AddSingleton<InstallViewModel>();
        services.AddSingleton<DoctorPage>();
        services.AddSingleton<DoctorViewModel>();
        services.AddSingleton<HostPage>();
        services.AddSingleton<HostViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutPage>();
        services.AddSingleton<AboutViewModel>();
        return services;
    }
}
