using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VmpLauncher.App.Services;

public interface IActivationService
{
    /// <summary>Handle a vmpack:// link, a .vmpack file, or an invite code. Only ever navigates or pre-fills; never installs or applies anything.</summary>
    void Handle(string activation);
}

public sealed class ActivationService(IAppNavigator navigator, IServiceProvider services, ILogger<ActivationService> log) : IActivationService
{
    public void Handle(string activation)
    {
        log.LogInformation("Activation received ({Length} chars)", activation.Length);
        // An invite opened from a link is only pre-filled in Settings: the user confirms it with "Use invite",
        // so a stray link can never replace a saved password or access token on its own.
        if (Core.Private.InviteCode.LooksLikeInvite(activation) && navigator.Current == AppPage.Welcome)
        {
            services.GetRequiredService<ViewModels.WelcomeViewModel>().InviteText = Uri.UnescapeDataString(activation);
            return;
        }
        if (Core.Private.InviteCode.LooksLikeInvite(activation))
        {
            var settings = services.GetRequiredService<ViewModels.SettingsViewModel>();
            settings.InviteText = Uri.UnescapeDataString(activation);
            navigator.Navigate(AppPage.Settings);
            return;
        }
        navigator.Navigate(AppPage.Home);
    }
}
