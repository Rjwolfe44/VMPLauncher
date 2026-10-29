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
        if (SharedSetupText(activation) is { } shared)
        {
            // A shared setup only opens the Import dialog; the player reviews before anything is installed.
            navigator.Navigate(AppPage.Mods);
            _ = services.GetRequiredService<ViewModels.ModsViewModel>().BeginImportAsync(shared);
            return;
        }
        navigator.Navigate(AppPage.Home);
    }

    /// <summary>The share code or shared-setup file text in an activation (vmpack:// link, pasted code, .vmpack file).</summary>
    private string? SharedSetupText(string activation)
    {
        var unescaped = Uri.UnescapeDataString(activation);
        if (Core.Catalog.SetupShare.FindCode(unescaped) is { } code)
        {
            return code;
        }
        try
        {
            var path = unescaped.Trim('"');
            if (path.EndsWith(".vmpack", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length < 1024 * 1024)
            {
                var text = System.IO.File.ReadAllText(path);
                return Core.Catalog.SetupShare.TryParse(text, out _, out _) ? text : null;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.LogWarning(ex, "Could not read the opened file");
        }
        return null;
    }
}
