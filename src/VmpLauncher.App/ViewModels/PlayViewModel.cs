using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.Services;
using VmpLauncher.Game;

namespace VmpLauncher.App.ViewModels;

public partial class RouteCardViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string EntryName { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string Kind { get; init; } = "direct";
    public string? Notes { get; init; }
    public string? RequiresSoftware { get; init; }
    [ObservableProperty] private string _status = "Not checked";
    [ObservableProperty] private string _statusKind = "neutral"; // ok | warn | error | neutral | checking
    [ObservableProperty] private string _rtt = string.Empty;
    [ObservableProperty] private bool _isSelected;

    public string Address => $"{Host}:{Port}";

    public string KindLabel => Kind switch
    {
        "playit" => "Internet (Playit)",
        "zerotier" => "ZeroTier network",
        "lan" => "Home network",
        _ => "Direct",
    };

    public string Hint => Notes ?? (RequiresSoftware is { } sw ? $"Needs {sw} on this PC." : Kind == "playit" ? "Works from anywhere, no extra software." : string.Empty);
}

/// <summary>Probes a route's reachability; implemented over the Net layer.</summary>
public interface IRouteStatusProvider
{
    Task<(string Status, string Kind, string Rtt)> CheckAsync(RouteCardViewModel route, CancellationToken ct);
}

public partial class PlayViewModel : ObservableObject
{
    private readonly LauncherService _launcher;
    private readonly AppSettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IAppNavigator _navigation;
    private readonly ILogger<PlayViewModel> _log;

    public PlayViewModel(LauncherService launcher, AppSettingsStore settings, ISecretStore secrets, ServerStatusService server, IAppNavigator navigation, ILogger<PlayViewModel> log)
    {
        _launcher = launcher;
        _settings = settings;
        _secrets = secrets;
        Server = server;
        _navigation = navigation;
        _log = log;
        _playerName = settings.Current.PlayerName ?? string.Empty;
        _hasPassword = secrets.Has(launcher.PasswordKey);
        launcher.StateChanged += UpdatePreflight;
        launcher.PrivateMods.Changed += () => { HasPassword = _secrets.Has(_launcher.PasswordKey); UpdatePreflight(); };
    }

    public ServerStatusService Server { get; }
    public ObservableCollection<RouteCardViewModel> Routes => Server.Routes;
    public ObservableCollection<RequirementRow> Preflight { get; } = new();

    [ObservableProperty] private string _playerName = string.Empty;
    [ObservableProperty] private bool _hasPassword;
    [ObservableProperty] private string _passwordInput = string.Empty;
    [ObservableProperty] private bool _canPlay;
    [ObservableProperty] private bool _canLaunchOffline;
    [ObservableProperty] private string _playHint = string.Empty;
    [ObservableProperty] private bool _gameRunning;
    [ObservableProperty] private string _runningSince = string.Empty;
    [ObservableProperty] private bool _autoJoinSupported;
    [ObservableProperty] private UserFacingError? _error;
    [ObservableProperty] private string _policyLine = string.Empty;
    [ObservableProperty] private string _policyKind = "neutral";
    [ObservableProperty] private bool _passwordSavedFlash;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        Error = null;
        try
        {
            var pack = _launcher.CurrentPack ?? await _launcher.RefreshPackAsync(ct);
            if (pack is not null)
            {
                await Server.LoadAsync(pack, force: false, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Loading routes failed");
            Error = ErrorCatalog.From(ex);
        }
        HasPassword = _secrets.Has(_launcher.PasswordKey);
        UpdatePreflight();
        _ = RunPolicyCheckAsync(ct);
    }

    private async Task RunPolicyCheckAsync(CancellationToken ct)
    {
        try
        {
            PolicyLine = "Checking your mods against the server…";
            PolicyKind = "checking";
            var r = await _launcher.PolicyCheckAsync(ct);
            if (r is null)
            {
                PolicyLine = _launcher.GetStatus().Installed is null ? "Install the pack to check mods against the server." : "This pack ships no server mod list.";
                PolicyKind = "neutral";
                return;
            }
            PolicyKind = r.Passes ? "ok" : "warn";
            PolicyLine = r.Passes
                ? $"All {r.Checked} mod DLLs match what the server expects."
                : $"{r.Problems} mod file(s) differ from the server: {r.MissingMandatory.Count} missing, {r.ShaMismatch.Count} different, {r.NonListed.Count} extra. Repair from Home fixes this.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Policy check failed");
            PolicyLine = "Mod check failed: " + ex.Message;
            PolicyKind = "warn";
        }
    }

    [RelayCommand]
    private Task CheckRoutesAsync(CancellationToken ct) => Server.CheckAllAsync(ct);

    [RelayCommand]
    private void SelectRoute(RouteCardViewModel route)
    {
        Server.Select(route);
        UpdatePreflight();
    }

    [RelayCommand]
    private async Task SavePasswordAsync()
    {
        if (string.IsNullOrEmpty(PasswordInput))
        {
            return;
        }
        _secrets.Set(_launcher.PasswordKey, PasswordInput);
        PasswordInput = string.Empty;
        HasPassword = true;
        UpdatePreflight();
        PasswordSavedFlash = true;
        await Task.Delay(2500);
        PasswordSavedFlash = false;
    }

    [RelayCommand]
    private void ForgetPassword()
    {
        _secrets.Set(_launcher.PasswordKey, null);
        HasPassword = false;
        UpdatePreflight();
    }

    partial void OnPlayerNameChanged(string value)
    {
        if (VmpSettingsFile.PlayerNamePattern.IsMatch(value))
        {
            _settings.Update(s => s with { PlayerName = value });
        }
        UpdatePreflight();
    }

    private void UpdatePreflight()
    {
        Preflight.Clear();
        var status = _launcher.GetStatus();
        var installed = status.State is ProfileState.Installed or ProfileState.UpdateAvailable or ProfileState.Modified;
        Preflight.Add(new RequirementRow { Label = "Pack installed", Ok = installed, Detail = installed ? $"version {status.Installed?.PackVersion}" : "install it from Home" });
        var nameOk = VmpSettingsFile.PlayerNamePattern.IsMatch(PlayerName);
        Preflight.Add(new RequirementRow { Label = "Player name", Ok = nameOk, Detail = nameOk ? PlayerName : "1-15 letters, digits, _ or -" });
        Preflight.Add(new RequirementRow { Label = "Server password", Ok = HasPassword, Detail = HasPassword ? "saved on this PC" : "enter it on the right" });
        var client = _launcher.InstalledClient();
        AutoJoinSupported = client?.SupportsAutoJoin == true;
        Preflight.Add(new RequirementRow { Label = "VMP client", Ok = client is not null, Detail = client is null ? "not installed" : $"{client.ExactBuild}{(AutoJoinSupported ? " · auto-join" : " · manual connect")}" });
        var route = Server.Selected;
        Preflight.Add(new RequirementRow { Label = "Server route", Ok = route is not null, Detail = route is null ? "pick one" : $"{route.Name} · {route.Status.ToLowerInvariant()}" });
        var disclaimer = _settings.Current.DisclaimerAccepted;
        Preflight.Add(new RequirementRow { Label = "VMP disclaimer", Ok = disclaimer, Detail = disclaimer ? "accepted" : "accept it in Settings or in-game" });
        CanPlay = installed && nameOk && route is not null && !GameRunning;
        CanLaunchOffline = installed && !GameRunning;
        PlayHint = !installed ? "Install the pack from Home first."
                 : !nameOk ? "Enter a player name."
                 : GameRunning ? "KSP is running."
                 : !HasPassword ? "You can launch, but joining needs the server password."
                 : AutoJoinSupported ? $"Launches KSP and joins {route?.Name} automatically once the game has loaded."
                 : "Launches KSP; then connect from the VMP window.";
    }

    [RelayCommand]
    private void GoHome() => _navigation.Navigate(AppPage.Home);

    [RelayCommand]
    private Task PlayAsync(CancellationToken ct)
        => RunGameAsync(() => Server.Selected?.EntryName ?? throw new InvalidOperationException("Pick a server route."), ct);

    /// <summary>Starts KSP with the pack's mods but without joining: single-player, or connect later from the VMP window.</summary>
    [RelayCommand]
    private Task LaunchOfflineAsync(CancellationToken ct) => RunGameAsync(() => null, ct);

    private async Task RunGameAsync(Func<string?> joinEntry, CancellationToken ct)
    {
        Error = null;
        try
        {
            var join = joinEntry();
            var entries = Routes.Select(r => new VmpSettingsFile.ServerEntry(r.EntryName, r.Host, r.Port, null)).ToList();
            using var process = _launcher.Launch(join, entries);
            GameRunning = true;
            RunningSince = DateTime.Now.ToString("HH:mm");
            UpdatePreflight();
            if (_settings.Current.MinimizeWhilePlaying && System.Windows.Application.Current.MainWindow is { } w)
            {
                w.WindowState = System.Windows.WindowState.Minimized;
            }
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Launch failed");
            Error = ErrorCatalog.From(ex);
        }
        finally
        {
            GameRunning = false;
            UpdatePreflight();
            if (System.Windows.Application.Current.MainWindow is { WindowState: System.Windows.WindowState.Minimized } w)
            {
                w.WindowState = System.Windows.WindowState.Normal;
            }
        }
    }
}
