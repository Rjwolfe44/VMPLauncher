using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;

namespace VmpLauncher.App.Services;

/// <summary>Page keys used by the shell, deep links and keyboard shortcuts.</summary>
public enum AppPage
{
    Welcome,
    Home,
    Play,
    Mods,
    Activity,
    Doctor,
    Host,
    Settings,
    About,
}

/// <summary>
/// Shell navigation. Pages are singleton <see cref="UserControl"/>s resolved from DI, so they keep their state while
/// the user moves around; the host control fades/slides the new page in. ViewModels depend on this interface only.
/// </summary>
public interface IAppNavigator
{
    AppPage Current { get; }
    event Action<AppPage>? Navigated;
    void Navigate(AppPage page);
}

public sealed class AppNavigator : IAppNavigator
{
    private readonly IServiceProvider _services;
    private readonly AccessibilityService _accessibility;
    private ContentControl? _host;
    private AppPage? _pending;

    public AppNavigator(IServiceProvider services, AccessibilityService accessibility)
    {
        _services = services;
        _accessibility = accessibility;
    }

    public AppPage Current { get; private set; } = AppPage.Home;

    public event Action<AppPage>? Navigated;

    public static Type PageType(AppPage page) => page switch
    {
        AppPage.Welcome => typeof(Views.Pages.WelcomePage),
        AppPage.Home => typeof(Views.Pages.HomePage),
        AppPage.Play => typeof(Views.Pages.PlayPage),
        AppPage.Mods => typeof(Views.Pages.ModsPage),
        AppPage.Activity => typeof(Views.Pages.InstallPage),
        AppPage.Doctor => typeof(Views.Pages.DoctorPage),
        AppPage.Host => typeof(Views.Pages.HostPage),
        AppPage.Settings => typeof(Views.Pages.SettingsPage),
        AppPage.About => typeof(Views.Pages.AboutPage),
        _ => typeof(Views.Pages.HomePage),
    };

    public static AppPage? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "welcome" => AppPage.Welcome,
        "home" => AppPage.Home,
        "play" => AppPage.Play,
        "mods" => AppPage.Mods,
        "install" or "activity" => AppPage.Activity,
        "doctor" => AppPage.Doctor,
        "host" or "publish" => AppPage.Host,
        "settings" => AppPage.Settings,
        "about" => AppPage.About,
        _ => null,
    };

    /// <summary>Called once by the main window.</summary>
    public void Attach(ContentControl host)
    {
        _host = host;
        if (_pending is { } p)
        {
            _pending = null;
            Navigate(p);
        }
    }

    public void Navigate(AppPage page)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Navigate(page));
            return;
        }
        if (_host is null)
        {
            _pending = page;
            return;
        }
        var view = (FrameworkElement)_services.GetRequiredService(PageType(page));
        if (ReferenceEquals(_host.Content, view))
        {
            Current = page;
            Navigated?.Invoke(page);
            return;
        }
        Current = page;
        _host.Content = view;
        if (!_accessibility.ReducedMotion)
        {
            var transform = new TranslateTransform(0, 14);
            view.RenderTransform = transform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
        Navigated?.Invoke(page);
    }
}
