using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.Services;
using VmpLauncher.Core;
using VmpLauncher.Net.Directory;
using VmpLauncher.Net.Doctor;

namespace VmpLauncher.App.ViewModels;

public partial class DoctorStepViewModel : ObservableObject
{
    public required string Id { get; init; }
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _statusKind = "checking";
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string? _details;
    [ObservableProperty] private string? _fixLabel;
    public DoctorFix? Fix { get; set; }
}

/// <summary>Connection Doctor page: runs the Net layer's checks and renders plain-language steps with one-click fixes.</summary>
public partial class DoctorViewModel : ObservableObject
{
    private readonly LauncherService _launcher;
    private readonly ISecretStore _secrets;
    private readonly IAppNavigator _navigation;
    private readonly LauncherPaths _paths;
    private readonly ILogger<DoctorViewModel> _log;
    private ConnectionDoctor? _lastDoctor;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public DoctorViewModel(LauncherService launcher, ISecretStore secrets, IAppNavigator navigation, LauncherPaths paths, ILogger<DoctorViewModel> log)
    {
        _launcher = launcher;
        _secrets = secrets;
        _navigation = navigation;
        _paths = paths;
        _log = log;
    }

    public ObservableCollection<DoctorStepViewModel> Steps { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _headline = "Checks your internet, the server routes, your password and your game files, and says what to fix.";
    [ObservableProperty] private string _headlineKind = "neutral";
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private UserFacingError? _error;

    [RelayCommand]
    private async Task RunAsync(CancellationToken ct)
    {
        Error = null;
        Steps.Clear();
        IsRunning = true;
        HasReport = false;
        Headline = "Checking…";
        HeadlineKind = "checking";
        try
        {
            var pack = _launcher.CurrentPack ?? await _launcher.RefreshPackAsync(ct);
            var fallback = BuildDirectoryFromPack(pack);
            var directoryUrl = pack?.Lock.Multiplayer.ServerDirectoryUrl;
            var doctor = new ConnectionDoctor(new ConnectionDoctorOptions
            {
                LoadServers = async token =>
                {
                    if (pack is not null && Uri.TryCreate(directoryUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    {
                        var client = new ServerDirectoryClient(_paths, pack.Lock.Pack.Id, Http, new CoreSignedDocumentVerifier());
                        return await client.FetchAsync(uri, new Uri(directoryUrl + ".sig"), token).ConfigureAwait(false);
                    }
                    return new ServerDirectoryResult(fallback, IsFromCache: false, DateTimeOffset.UtcNow, TimeSpan.Zero, null, "No live server list for this pack; using the presets frozen in the pack.");
                },
                PasswordProvider = _ => ValueTask.FromResult(_secrets.Get(_launcher.PasswordKey)),
                InstalledBuild = () => _launcher.InstalledClient()?.ExactBuild,
            });
            _lastDoctor = doctor;
            await foreach (var step in doctor.RunAsync(ct))
            {
                var existing = Steps.FirstOrDefault(s => s.Id == step.Id);
                if (existing is null)
                {
                    existing = new DoctorStepViewModel { Id = step.Id };
                    Steps.Add(existing);
                }
                existing.Title = step.Title;
                existing.Summary = step.Summary;
                existing.Details = step.Details;
                existing.Fix = step.Fix;
                existing.FixLabel = step.Fix switch
                {
                    DoctorFix.EnterPassword => "Enter password",
                    DoctorFix.UpdatePack => "Update pack",
                    DoctorFix.InstallZeroTier => "Get ZeroTier",
                    DoctorFix.JoinZeroTier => "Join network",
                    DoctorFix.AskHostAuthorize => "Copy my ZeroTier id",
                    DoctorFix.CheckFirewall => "Open firewall settings",
                    DoctorFix.RefreshServers => "Refresh",
                    _ => null,
                };
                existing.StatusKind = step.Status switch
                {
                    DoctorStatus.Pass => "ok",
                    DoctorStatus.Warn => "warn",
                    DoctorStatus.Fail => "error",
                    DoctorStatus.Skipped => "neutral",
                    _ => "checking",
                };
            }
            var fails = Steps.Count(s => s.StatusKind == "error");
            var warns = Steps.Count(s => s.StatusKind == "warn");
            var flagged = Steps.Where(s => s.StatusKind is "error" or "warn").Select(s => s.Title).ToList();
            var list = flagged.Count == 0 ? string.Empty : ": " + string.Join(", ", flagged.Take(3)) + (flagged.Count > 3 ? ", ..." : string.Empty);
            Headline = fails == 0 && warns == 0 ? "Everything looks good. You should be able to join."
                     : fails == 0 ? $"Playable. {Plural(warns, "thing")} worth a look{list}."
                     : $"{Plural(fails, "problem")} will stop you from joining{list}.";
            HeadlineKind = fails > 0 ? "error" : warns > 0 ? "warn" : "ok";
            HasReport = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Doctor failed");
            Error = ErrorCatalog.From(ex);
            Headline = "The checks could not run.";
            HeadlineKind = "error";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static ServerDirectory BuildDirectoryFromPack(ResolvedPack? pack)
    {
        var routes = new List<ServerRoute>();
        if (pack is not null)
        {
            var priority = 10;
            foreach (var s in pack.Lock.Multiplayer.Servers)
            {
                var kind = s.Address.EndsWith(".ply.gg", StringComparison.OrdinalIgnoreCase) ? RouteKind.Playit
                         : s.Address.StartsWith("10.", StringComparison.Ordinal) ? RouteKind.Zerotier
                         : s.Address.StartsWith("192.168.", StringComparison.Ordinal) ? RouteKind.Lan : RouteKind.Direct;
                routes.Add(new ServerRoute { Id = s.Name.ToLowerInvariant().Replace(' ', '-'), Name = s.Name, VmpEntryName = s.Name, Kind = kind, Host = s.Address, Port = s.Port, Priority = priority });
                priority += 10;
            }
        }
        return new ServerDirectory
        {
            PackId = pack?.Lock.Pack.Id ?? "unknown",
            Server = new ServerInfo { Id = pack?.Lock.Pack.Id ?? "unknown", DisplayName = pack?.Lock.Pack.Name ?? "Server", RequiredBuild = pack?.Lock.Multiplayer.RequiredClientVersion ?? string.Empty, PasswordRequired = true },
            Routes = routes,
        };
    }

    [RelayCommand]
    private void ApplyFix(DoctorStepViewModel step)
    {
        switch (step.Fix)
        {
            case DoctorFix.EnterPassword:
                _navigation.Navigate(AppPage.Play);
                break;
            case DoctorFix.UpdatePack:
                _navigation.Navigate(AppPage.Home);
                break;
            case DoctorFix.InstallZeroTier:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.zerotier.com/download/") { UseShellExecute = true });
                break;
            case DoctorFix.CheckFirewall:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("windowsdefender://network") { UseShellExecute = true });
                break;
            case DoctorFix.RefreshServers:
                _ = RunCommand.ExecuteAsync(null);
                break;
            default:
                if (step.Details is not null)
                {
                    try { System.Windows.Clipboard.SetText(step.Details); } catch (Exception) { }
                }
                break;
        }
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (_lastDoctor is null)
        {
            return;
        }
        try { System.Windows.Clipboard.SetText(_lastDoctor.BuildReport()); } catch (Exception) { }
    }
}
