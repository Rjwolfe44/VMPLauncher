using System.Collections.ObjectModel;
using System.Diagnostics;
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
    [ObservableProperty] private string _status = "Unknown";
    [ObservableProperty] private string _statusKind = "neutral"; // ok | warn | error | neutral | checking
    [ObservableProperty] private string _rtt = string.Empty;
    [ObservableProperty] private bool _isSelected;

    public string Address => $"{Host}:{Port}";
}

/// <summary>Probes a route's reachability; implemented over the Net layer. Stubbed until it is wired.</summary>
public interface IRouteStatusProvider
{
    Task<(string Status, string Kind, string Rtt)> CheckAsync(RouteCardViewModel route, CancellationToken ct);
}

public sealed class UnknownRouteStatusProvider : IRouteStatusProvider
{
    public Task<(string Status, string Kind, string Rtt)> CheckAsync(RouteCardViewModel route, CancellationToken ct)
        => Task.FromResult(("Not checked", "neutral", string.Empty));
}

public partial class PlayViewModel : ObservableObject
{
    private readonly LauncherService _launcher;
    private readonly AppSettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IRouteStatusProvider _status;
    private readonly ServerRoutesService _routes;
    private readonly ILogger<PlayViewModel> _log;

    public PlayViewModel(LauncherService launcher, AppSettingsStore settings, ISecretStore secrets, IRouteStatusProvider status, ServerRoutesService routes, ILogger<PlayViewModel> log)
    {
        _launcher = launcher;
        _settings = settings;
        _secrets = secrets;
        _status = status;
        _routes = routes;
        _log = log;
        PlayerName = settings.Current.PlayerName ?? string.Empty;
        HasPassword = secrets.Has(launcher.PasswordKey);
    }

    public ObservableCollection<RouteCardViewModel> Routes { get; } = new();
    public ObservableCollection<RequirementRow> Preflight { get; } = new();

    [ObservableProperty] private string _playerName = string.Empty;
    [ObservableProperty] private bool _hasPassword;
    [ObservableProperty] private string _passwordInput = string.Empty;
    [ObservableProperty] private bool _canPlay;
    [ObservableProperty] private string _playHint = string.Empty;
    [ObservableProperty] private bool _gameRunning;
    [ObservableProperty] private string _runningSince = string.Empty;
    [ObservableProperty] private bool _autoJoinSupported;
    [ObservableProperty] private UserFacingError? _error;
    [ObservableProperty] private string _routesSource = string.Empty;
    [ObservableProperty] private string? _motd;
    [ObservableProperty] private string? _maintenance;

    private RouteCardViewModel? Selected => Routes.FirstOrDefault(r => r.IsSelected);

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        Error = null;
        Routes.Clear();
        var pack = _launcher.CurrentPack ?? await _launcher.RefreshPackAsync(ct);
        if (pack is not null)
        {
            var result = await _routes.GetRoutesAsync(pack, ct);
            foreach (var r in result.Routes)
            {
                Routes.Add(new RouteCardViewModel { Id = r.Id, Name = r.Name, EntryName = r.EntryName, Host = r.Host, Port = r.Port, Kind = r.Kind, Notes = r.Notes, RequiresSoftware = r.RequiresSoftware });
            }
            RoutesSource = result.Warning is null ? $"Routes from the {result.Source}." : $"Routes from the {result.Source}. {result.Warning}";
            Motd = result.Motd;
            Maintenance = result.Maintenance;
        }
        var selectedId = _settings.Current.SelectedRouteId;
        var pick = Routes.FirstOrDefault(r => r.Id == selectedId) ?? Routes.FirstOrDefault();
        if (pick is not null)
        {
            pick.IsSelected = true;
        }
        UpdatePreflight();
        _ = CheckRoutesAsync(ct);
        _ = RunPolicyCheckAsync(ct);
    }

    [ObservableProperty] private string _policyLine = string.Empty;
    [ObservableProperty] private bool _policyOk;

    private async Task RunPolicyCheckAsync(CancellationToken ct)
    {
        try
        {
            var r = await _launcher.PolicyCheckAsync(ct);
            if (r is null)
            {
                PolicyLine = "No server policy file in this pack.";
                return;
            }
            PolicyOk = r.Passes;
            PolicyLine = r.Passes
                ? $"Mod policy: all {r.Checked} DLLs match the server."
                : $"Mod policy: {r.Problems} problem(s): {r.MissingMandatory.Count} missing, {r.ShaMismatch.Count} different, {r.NonListed.Count} extra" + (r.ShaMismatch.Count > 0 ? $" (e.g. {r.ShaMismatch[0].Path})" : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Policy check failed");
            PolicyLine = "Mod policy check failed: " + ex.Message;
        }
    }

    private static string GuessKind(string host)
        => host.EndsWith(".ply.gg", StringComparison.OrdinalIgnoreCase) ? "playit"
         : host.StartsWith("10.", StringComparison.Ordinal) ? "zerotier"
         : host.StartsWith("192.168.", StringComparison.Ordinal) ? "lan" : "direct";

    [RelayCommand]
    private async Task CheckRoutesAsync(CancellationToken ct)
    {
        var tasks = Routes.Select(async r =>
        {
            r.Status = "Checking…";
            r.StatusKind = "checking";
            try
            {
                var (status, kind, rtt) = await _status.CheckAsync(r, ct);
                r.Status = status;
                r.StatusKind = kind;
                r.Rtt = rtt;
            }
            catch (Exception ex)
            {
                r.Status = "Check failed";
                r.StatusKind = "error";
                _log.LogWarning(ex, "Route check failed for {Route}", r.Name);
            }
        });
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void SelectRoute(RouteCardViewModel route)
    {
        foreach (var r in Routes)
        {
            r.IsSelected = ReferenceEquals(r, route);
        }
        _settings.Update(s => s with { SelectedRouteId = route.Id });
        UpdatePreflight();
    }

    [RelayCommand]
    private void SavePassword()
    {
        if (string.IsNullOrEmpty(PasswordInput))
        {
            return;
        }
        _secrets.Set(_launcher.PasswordKey, PasswordInput);
        PasswordInput = string.Empty;
        HasPassword = true;
        UpdatePreflight();
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
        Preflight.Add(new RequirementRow { Label = "Pack installed", Ok = installed, Detail = installed ? $"version {status.Installed?.PackVersion}" : "install it from Home first" });
        var nameOk = VmpSettingsFile.PlayerNamePattern.IsMatch(PlayerName);
        Preflight.Add(new RequirementRow { Label = "Player name", Ok = nameOk, Detail = nameOk ? PlayerName : "1-15 letters, digits, _ or -" });
        Preflight.Add(new RequirementRow { Label = "Server password saved", Ok = HasPassword, Detail = HasPassword ? "saved on this PC" : "enter it below" });
        var client = _launcher.InstalledClient();
        AutoJoinSupported = client?.SupportsAutoJoin == true;
        Preflight.Add(new RequirementRow { Label = "VMP client", Ok = client is not null, Detail = client is null ? "not installed" : $"{client.ExactBuild}{(AutoJoinSupported ? " · auto-join" : " · manual connect")}" });
        var route = Selected;
        Preflight.Add(new RequirementRow { Label = "Server route", Ok = route is not null, Detail = route is null ? "pick one" : $"{route.Name} ({route.Address})" });
        var disclaimer = _settings.Current.DisclaimerAccepted;
        Preflight.Add(new RequirementRow { Label = "VMP disclaimer accepted", Ok = disclaimer, Detail = disclaimer ? "accepted" : "accept it in Settings or in-game" });
        CanPlay = installed && nameOk && route is not null && !GameRunning;
        PlayHint = !installed ? "Install the pack first." : !nameOk ? "Enter a player name." : !HasPassword ? "You can play, but joining needs the server password." : AutoJoinSupported ? $"Launch KSP and join {route?.Name}." : "Launch KSP, then connect in the VMP window.";
    }

    [RelayCommand]
    private async Task PlayAsync(CancellationToken ct)
    {
        Error = null;
        try
        {
            var route = Selected ?? throw new InvalidOperationException("Pick a server route.");
            var entries = Routes.Select(r => new VmpSettingsFile.ServerEntry(r.EntryName, r.Host, r.Port, null)).ToList();
            var process = _launcher.Launch(route.EntryName, entries);
            GameRunning = true;
            RunningSince = DateTime.Now.ToString("HH:mm");
            UpdatePreflight();
            if (_settings.Current.MinimizeWhilePlaying && System.Windows.Application.Current.MainWindow is { } w)
            {
                w.WindowState = System.Windows.WindowState.Minimized;
            }
            await process.WaitForExitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Launch failed");
            Error = ErrorCatalog.From(ex);
        }
        finally
        {
            GameRunning = false;
            UpdatePreflight();
        }
    }
}
