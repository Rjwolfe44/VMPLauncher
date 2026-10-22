using Microsoft.Extensions.DependencyInjection;
using VmpLauncher.App.Services;
using VmpLauncher.App.ViewModels;
using VmpLauncher.App.Views;
using VmpLauncher.App.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace VmpLauncher.App.Hosting;

public static class ServiceRegistration
{
    public static IServiceCollection AddLauncherServices(this IServiceCollection services)
    {
        // WPF-UI
        services.AddNavigationViewPageProvider();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();

        // Launcher services
        services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        services.AddSingleton<AccessibilityService>();
        services.AddSingleton<IActivationService, ActivationService>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<OperationRunner>();
        services.AddSingleton<PackCatalogService>();
        services.AddSingleton<LauncherService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IRouteStatusProvider, ProbeRouteStatusProvider>();
        services.AddSingleton<ServerRoutesService>();

        // Shell
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();

        // Pages (singletons keep state while navigating)
        services.AddSingleton<WelcomePage>();
        services.AddSingleton<WelcomeViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<PlayPage>();
        services.AddSingleton<PlayViewModel>();
        services.AddSingleton<InstallPage>();
        services.AddSingleton<InstallViewModel>();
        services.AddSingleton<DoctorPage>();
        services.AddSingleton<DoctorViewModel>();
        services.AddSingleton<ModsPage>();
        services.AddSingleton<ModsViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutPage>();
        services.AddSingleton<AboutViewModel>();
        return services;
    }
}
