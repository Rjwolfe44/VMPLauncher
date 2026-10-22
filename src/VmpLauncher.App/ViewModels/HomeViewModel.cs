using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Security;
using Wpf.Ui;

namespace VmpLauncher.App.ViewModels;

public partial class RequirementRow : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private bool _ok;
    [ObservableProperty] private string _detail = string.Empty;
}

public partial class HomeViewModel : ObservableObject
{
    private readonly LauncherService _launcher;
    private readonly AppSettingsStore _settings;
    private readonly INavigationService _navigation;
    private readonly ILogger<HomeViewModel> _log;

    public HomeViewModel(LauncherService launcher, AppSettingsStore settings, INavigationService navigation, ILogger<HomeViewModel> log)
    {
        _launcher = launcher;
        _settings = settings;
        _navigation = navigation;
        _log = log;
    }

    [ObservableProperty] private string _packName = "VMP Train Career";
    [ObservableProperty] private string _packAuthor = "Rjwolfe44";
    [ObservableProperty] private string _packVersion = string.Empty;
    [ObservableProperty] private string _packDescription = string.Empty;
    [ObservableProperty] private string _stateBadge = "Loading…";
    [ObservableProperty] private string _stateKind = "neutral"; // neutral | ok | warn | error
    [ObservableProperty] private string _primaryAction = "Install";
    [ObservableProperty] private bool _primaryEnabled;
    [ObservableProperty] private string _factsLine = string.Empty;
    [ObservableProperty] private string _trustLine = string.Empty;
    [ObservableProperty] private string _trustKind = "neutral";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private UserFacingError? _error;
    [ObservableProperty] private bool _needsSideload;
    [ObservableProperty] private string _sideloadHint = string.Empty;
    [ObservableProperty] private string _lastCheck = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<RequirementRow> Requirements { get; } = new();

    private ProfileState _state;
    private readonly Dictionary<string, string> _sideloads = new(StringComparer.Ordinal);

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        Error = null;
        IsBusy = true;
        try
        {
            var pack = await _launcher.RefreshPackAsync(ct);
            if (pack is null)
            {
                StateBadge = "Pack unavailable";
                StateKind = "error";
                PrimaryEnabled = false;
                TrustLine = "Could not load the pack (offline and nothing cached).";
                TrustKind = "error";
                return;
            }
            PackName = pack.Lock.Pack.Name;
            PackAuthor = pack.Lock.Pack.Author ?? string.Empty;
            PackVersion = pack.Lock.Pack.Version;
            PackDescription = pack.Lock.Pack.Description ?? string.Empty;
            var mods = pack.Lock.Ckan.Modules.Count(m => m.Kind == Core.Locking.ModuleKindDto.Package);
            var custom = pack.Lock.Ckan.Modules.Count(m => m.Origin == Core.Locking.ModuleOrigin.Custom);
            var download = pack.Lock.Ckan.Modules.Where(m => m.Artifact is not null).Sum(m => m.Artifact!.Size);
            FactsLine = $"{mods} mods ({custom} custom) · {Format.Bytes(download)} to download · {Format.Bytes(pack.Lock.Inventory.TotalBytes)} on disk";
            TrustLine = pack.Trust switch
            {
                TrustLevel.VerifiedPublisher => $"Verified publisher · from {pack.Source}",
                TrustLevel.Unverified => $"Unverified pack (unsigned) · from {pack.Source}",
                _ => $"Signature invalid: {pack.TrustReason}",
            };
            TrustKind = pack.Trust switch { TrustLevel.VerifiedPublisher => "ok", TrustLevel.Unverified => "warn", _ => "error" };
            var sideloadModules = pack.Lock.Ckan.Modules.Where(m => m.Artifact?.Distribution == "private-sideload").ToList();
            NeedsSideload = sideloadModules.Count > 0 && sideloadModules.Any(m => !_sideloads.ContainsKey(m.Identifier));
            SideloadHint = NeedsSideload ? "Private package needed: " + string.Join(", ", sideloadModules.Where(m => !_sideloads.ContainsKey(m.Identifier)).Select(m => m.Artifact!.FileName)) + " — drop the file here (ask the host)." : string.Empty;
            UpdateRequirements(pack);
            UpdateState();
            LastCheck = $"checked {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Refresh failed");
            Error = ErrorCatalog.From(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateRequirements(ResolvedPack pack)
    {
        Requirements.Clear();
        var s = _settings.Current;
        var ksp = s.SourceKspPath is null ? null : Game.KspLocator.Inspect(s.SourceKspPath);
        Requirements.Add(new RequirementRow { Label = "Your KSP install", Ok = ksp?.HasExecutable == true, Detail = ksp?.Path ?? "not chosen" });
        Requirements.Add(new RequirementRow { Label = $"KSP {pack.Lock.Game.KspVersion} (build {pack.Lock.Game.BuildId64})", Ok = ksp?.MatchesBuild(pack.Lock.Game.BuildId64) == true, Detail = ksp?.BuildId is null ? "unknown" : $"build {ksp.BuildId}" });
        Requirements.Add(new RequirementRow { Label = "Making History", Ok = ksp?.HasMakingHistory == true, Detail = ksp?.HasMakingHistory == true ? "installed" : "missing" });
        Requirements.Add(new RequirementRow { Label = "Breaking Ground", Ok = ksp?.HasBreakingGround == true, Detail = ksp?.HasBreakingGround == true ? "installed" : "missing" });
        var profileRoot = _launcher.ProfilePaths.Root;
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(profileRoot)!).AvailableFreeSpace; } catch (Exception) { }
        var needed = pack.Lock.Inventory.TotalBytes + 2L * 1024 * 1024 * 1024;
        Requirements.Add(new RequirementRow { Label = "Disk space", Ok = free > needed, Detail = $"{Format.Bytes(free)} free, ~{Format.Bytes(needed)} needed on {Path.GetPathRoot(profileRoot)}" });
    }

    private void UpdateState()
    {
        var status = _launcher.GetStatus();
        _state = status.State;
        (StateBadge, StateKind, PrimaryAction, PrimaryEnabled) = status.State switch
        {
            ProfileState.NoKsp => ("Choose your KSP", "warn", "Set up", true),
            ProfileState.NotInstalled => ("Not installed", "neutral", "Install", !NeedsSideload),
            ProfileState.Installed => ($"Installed {status.Installed!.PackVersion}", "ok", "Play", true),
            ProfileState.UpdateAvailable => ($"Update available {status.Detail}", "warn", "Update", !NeedsSideload),
            ProfileState.Modified => ($"{status.ModifiedFiles} files differ", "warn", "Repair", true),
            ProfileState.Interrupted => ("Interrupted", "error", "Restore", true),
            _ => ("Unknown", "neutral", "Refresh", true),
        };
    }

    [RelayCommand]
    private async Task PrimaryAsync(CancellationToken ct)
    {
        Error = null;
        try
        {
            switch (_state)
            {
                case ProfileState.NoKsp:
                    _navigation.Navigate(typeof(Views.Pages.WelcomePage));
                    return;
                case ProfileState.Installed:
                    _navigation.Navigate(typeof(Views.Pages.PlayPage));
                    return;
                case ProfileState.Interrupted:
                    await _launcher.RecoverAsync(ct);
                    break;
                case ProfileState.Modified:
                    _navigation.Navigate(typeof(Views.Pages.InstallPage));
                    await _launcher.RepairAsync(false, ct);
                    break;
                default:
                    _navigation.Navigate(typeof(Views.Pages.InstallPage));
                    await _launcher.InstallOrUpdateAsync(_sideloads, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Primary action failed");
            Error = ErrorCatalog.From(ex);
        }
        UpdateState();
    }

    /// <summary>A private package zip was dropped on the page.</summary>
    public void AcceptSideloadFile(string path)
    {
        var pack = _launcher.CurrentPack;
        if (pack is null)
        {
            return;
        }
        var name = Path.GetFileName(path);
        var match = pack.Lock.Ckan.Modules.FirstOrDefault(m => m.Artifact?.Distribution == "private-sideload" && string.Equals(m.Artifact.FileName, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Error = new UserFacingError("Not the file this pack needs", $"{name} is not one of the private packages this pack asks for.", SideloadHint, string.Empty, RecoveryHint.ProvideFile);
            return;
        }
        var sha = Core.Hashing.Sha256Util.HashFile(path);
        if (!Core.Hashing.Sha256Util.HexEquals(sha, match.Artifact!.Sha256))
        {
            Error = new UserFacingError("Wrong file version", $"{name} does not match the version this pack expects. Ask the host for the current one.", null, $"expected {match.Artifact.Sha256}\nactual {sha}", RecoveryHint.ProvideFile);
            return;
        }
        _sideloads[match.Identifier] = path;
        Error = null;
        NeedsSideload = pack.Lock.Ckan.Modules.Any(m => m.Artifact?.Distribution == "private-sideload" && !_sideloads.ContainsKey(m.Identifier));
        SideloadHint = NeedsSideload ? SideloadHint : $"{name} accepted.";
        UpdateState();
    }

    [RelayCommand]
    private void OpenProfileFolder()
    {
        var p = _launcher.ProfilePaths;
        if (Directory.Exists(p.GameDir))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p.GameDir}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        if (Error is not null)
        {
            try { System.Windows.Clipboard.SetText(Error.Details); } catch (Exception) { }
        }
    }
}
