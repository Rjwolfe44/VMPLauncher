using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.ViewModels;
using VmpLauncher.Core.Packs;
using VmpLauncher.Net.Directory;

namespace VmpLauncher.App.Services;

/// <summary>
/// The server routes and their live status, shared by the Play page and the sidebar status card. Routes come from
/// <see cref="ServerRoutesService"/> (signed live list, cached copy, or lock presets); each route gets a cheap
/// reachability probe that never authenticates. Re-checked every 90 seconds while the launcher is open.
/// </summary>
public sealed partial class ServerStatusService : ObservableObject
{
    private static readonly TimeSpan AutoInterval = TimeSpan.FromSeconds(90);

    private readonly ServerRoutesService _routes;
    private readonly IRouteStatusProvider _status;
    private readonly AppSettingsStore _settings;
    private readonly HostToolsService _host;
    private readonly ILogger<ServerStatusService> _log;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private string? _loadedFor;
    private RoutesResult? _published;

    public ServerStatusService(ServerRoutesService routes, IRouteStatusProvider status, AppSettingsStore settings, HostToolsService host, ILogger<ServerStatusService> log)
    {
        _routes = routes;
        _status = status;
        _settings = settings;
        _host = host;
        _log = log;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AutoInterval };
        _timer.Tick += async (_, _) => await CheckAllAsync(CancellationToken.None);
    }

    public ObservableCollection<RouteCardViewModel> Routes { get; } = new();

    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private string? _motd;
    [ObservableProperty] private string? _maintenance;
    [ObservableProperty] private string _summary = "Server: not checked";
    [ObservableProperty] private string _summaryKind = "neutral";
    [ObservableProperty] private string _summaryDetail = string.Empty;
    [ObservableProperty] private bool _isChecking;

    public RouteCardViewModel? Selected => Routes.FirstOrDefault(r => r.IsSelected);

    /// <summary>Loads the routes for a pack (once per lock unless <paramref name="force"/>), then probes them.</summary>
    public async Task LoadAsync(ResolvedPack pack, bool force, CancellationToken ct)
    {
        if (!force && _loadedFor == pack.LockSha256 && Routes.Count > 0)
        {
            return;
        }
        _published = await _routes.GetRoutesAsync(pack, ct);
        _loadedFor = pack.LockSha256;
        Apply();
        _timer.Start();
        await CheckAllAsync(ct);
    }

    /// <summary>Rebuilds the list from the published routes, the host's private presets and the player's own servers.</summary>
    private void Apply()
    {
        var result = _published!;
        var hostOnly = _host.IsHost && _host.Pack is { } hostPack ? hostPack.Multiplayer.Servers : Array.Empty<ServerPreset>();
        var merged = RouteMerger.Merge(result.Routes, hostOnly, _settings.Current.MyServers);
        var previous = Selected?.Id ?? _settings.Current.SelectedRouteId;
        var before = Routes.ToDictionary(r => r.Id);
        Routes.Clear();
        foreach (var r in merged)
        {
            var card = new RouteCardViewModel { Id = r.Id, Name = r.Name, EntryName = r.EntryName, Host = r.Host, Port = r.Port, Kind = r.Kind, Notes = r.Notes, RequiresSoftware = r.RequiresSoftware, Origin = r.Origin };
            if (before.TryGetValue(r.Id, out var old) && old.Host == r.Host && old.Port == r.Port)
            {
                // Keep the last probe result, so adding a server doesn't blank the others while they re-check.
                card.Status = old.Status;
                card.StatusKind = old.StatusKind;
                card.Rtt = old.Rtt;
            }
            Routes.Add(card);
        }
        var pick = Routes.FirstOrDefault(r => r.Id == previous) ?? Routes.FirstOrDefault();
        if (pick is not null)
        {
            pick.IsSelected = true;
        }
        var own = merged.Count(r => r.Origin == RouteOrigin.Mine);
        var text = result.Warning is null ? $"Routes from the {result.Source}." : $"Routes from the {result.Source}. {result.Warning}";
        Source = own > 0 ? $"{text} Plus {own} of your own." : text;
        Motd = result.Motd;
        Maintenance = result.Maintenance;
        OnPropertyChanged(nameof(Selected));
        UpdateSummary();
    }

    /// <summary>Every route on the list, for the Add server dialog to check a new name or address against.</summary>
    public IReadOnlyList<KnownRoute> KnownRoutes() => Routes.Select(r => new KnownRoute(r.Name, r.Host, r.Port)).ToList();

    /// <summary>Saves a server of the player's own (replacing <paramref name="replacing"/> when editing it) and probes the list again.</summary>
    public Task SaveMineAsync(MyServer server, string? replacing)
    {
        var selectedName = Selected is { Origin: RouteOrigin.Mine } s0 ? s0.Name : null;
        _settings.Update(s => s with
        {
            MyServers = s.MyServers
                .Where(m => !string.Equals(m.Name, replacing ?? server.Name, StringComparison.OrdinalIgnoreCase))
                .Append(server)
                .ToList(),
        });
        if (_published is not null)
        {
            Apply();
        }
        // An edited server that was the pick stays the pick under its new name.
        if (replacing is not null && string.Equals(selectedName, replacing, StringComparison.OrdinalIgnoreCase)
            && Routes.FirstOrDefault(r => r.Origin == RouteOrigin.Mine && string.Equals(r.Name, server.Name, StringComparison.OrdinalIgnoreCase)) is { } renamed)
        {
            Select(renamed);
        }
        return CheckAllAsync(CancellationToken.None);
    }

    /// <summary>Takes one of the player's servers off the list.</summary>
    public void RemoveMine(string name)
    {
        _settings.Update(s => s with { MyServers = s.MyServers.Where(m => !string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList() });
        if (_published is not null)
        {
            Apply();
        }
    }

    public void Select(RouteCardViewModel route)
    {
        foreach (var r in Routes)
        {
            r.IsSelected = ReferenceEquals(r, route);
        }
        _settings.Update(s => s with { SelectedRouteId = route.Id });
        OnPropertyChanged(nameof(Selected));
        UpdateSummary();
    }

    public async Task CheckAllAsync(CancellationToken ct)
    {
        if (Routes.Count == 0 || !await _checkGate.WaitAsync(0, ct))
        {
            return;
        }
        IsChecking = true;
        try
        {
            if (SummaryKind == "neutral")
            {
                Summary = "Checking server…";
                SummaryKind = "checking";
            }
            var tasks = Routes.ToList().Select(async r =>
            {
                if (r.StatusKind is not ("ok" or "error" or "warn"))
                {
                    r.Status = "Checking…";
                    r.StatusKind = "checking";
                }
                try
                {
                    var (status, kind, rtt) = await _status.CheckAsync(r, ct);
                    r.Status = status;
                    r.StatusKind = kind;
                    r.Rtt = rtt;
                }
                catch (OperationCanceledException)
                {
                    r.Status = "Not checked";
                    r.StatusKind = "neutral";
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
        finally
        {
            IsChecking = false;
            _checkGate.Release();
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        var selected = Selected;
        var online = Routes.Where(r => r.StatusKind == "ok").ToList();
        if (Maintenance is not null)
        {
            (Summary, SummaryKind, SummaryDetail) = ("Server in maintenance", "warn", Maintenance);
        }
        else if (selected is not null && selected.StatusKind == "ok")
        {
            (Summary, SummaryKind, SummaryDetail) = ($"Server online{(selected.Rtt.Length > 0 ? " · " + selected.Rtt : string.Empty)}", "ok", $"via {selected.Name}");
        }
        else if (online.Count > 0)
        {
            (Summary, SummaryKind, SummaryDetail) = ("Server online", "ok", $"via {online[0].Name} (your pick is {selected?.Status.ToLowerInvariant() ?? "not set"})");
        }
        else if (Routes.Count == 0)
        {
            (Summary, SummaryKind, SummaryDetail) = ("No server routes", "neutral", "The pack lists no servers.");
        }
        else if (Routes.All(r => r.StatusKind is "checking" or "neutral"))
        {
            (Summary, SummaryKind, SummaryDetail) = ("Checking server…", "checking", string.Empty);
        }
        else
        {
            (Summary, SummaryKind, SummaryDetail) = ("Server unreachable", "error", "Open the Doctor to see why.");
        }
    }
}
