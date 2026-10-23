using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Security;
using Wpf.Ui.Controls;

namespace VmpLauncher.App.ViewModels;

public partial class RequirementRow : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private bool _ok;
    [ObservableProperty] private string _detail = string.Empty;
}

public sealed record PrivateNeedRow(string Name, string Version, string FileName, string Detail, string StateKind, string StateText);

public partial class HomeViewModel : ObservableObject
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    private readonly LauncherService _launcher;
    private readonly AppSettingsStore _settings;
    private readonly IAppNavigator _navigation;
    private readonly OperationRunner _runner;
    private readonly ServerStatusService _server;
    private readonly ILogger<HomeViewModel> _log;
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private OperationKind? _lastKind;
    private bool _autoUpdateTried;
    private ProfileState _state;

    public HomeViewModel(LauncherService launcher, AppSettingsStore settings, IAppNavigator navigation, OperationRunner runner, ServerStatusService server, ILogger<HomeViewModel> log)
    {
        _launcher = launcher;
        _settings = settings;
        _navigation = navigation;
        _runner = runner;
        _server = server;
        _log = log;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => SampleOperation();
        _timer.Start();
        launcher.PrivateMods.Changed += () => { if (_launcher.CurrentPack is not null) { UpdatePrivate(); UpdateState(); } };
        runner.Finished += _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateState);
    }

    // Pack facts
    [ObservableProperty] private string _packName = "VMP Train Career";
    [ObservableProperty] private string _packAuthor = string.Empty;
    [ObservableProperty] private string _packVersion = string.Empty;
    [ObservableProperty] private string _packDescription = string.Empty;
    [ObservableProperty] private string _modsCount = "–";
    [ObservableProperty] private string _modsDetail = string.Empty;
    [ObservableProperty] private string _downloadSize = "–";
    [ObservableProperty] private string _diskSize = "–";
    [ObservableProperty] private string _trustLine = "Checking signature";
    [ObservableProperty] private string _trustKind = "neutral";
    [ObservableProperty] private string? _whatsNew;
    [ObservableProperty] private string _lastCheck = string.Empty;

    // State and primary action
    [ObservableProperty] private string _stateBadge = "Loading";
    [ObservableProperty] private string _stateKind = "checking"; // neutral | ok | warn | error | checking
    [ObservableProperty] private string _stateHeadline = "Getting the latest pack…";
    [ObservableProperty] private string _primaryAction = "Install";
    [ObservableProperty] private SymbolRegular _primaryIcon = SymbolRegular.ArrowDownload24;
    [ObservableProperty] private bool _primaryEnabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private UserFacingError? _error;

    // Running operation (shown inside the hero)
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty] private string _opTitle = string.Empty;
    [ObservableProperty] private string _opMessage = string.Empty;
    [ObservableProperty] private int _opPercent;
    [ObservableProperty] private bool _opIndeterminate;
    [ObservableProperty] private string _opStats = string.Empty;

    // Private mods
    [ObservableProperty] private bool _showPrivateCard;
    [ObservableProperty] private bool _privateBlocking;
    [ObservableProperty] private string _privateHeadline = string.Empty;
    [ObservableProperty] private string _privateBody = string.Empty;
    [ObservableProperty] private bool _privateNeedsInvite;
    [ObservableProperty] private string _inviteText = string.Empty;
    public ObservableCollection<PrivateNeedRow> PrivateNeeds { get; } = new();

    public ObservableCollection<RequirementRow> Requirements { get; } = new();

    /// <summary>Called when the page becomes visible: refresh unless the data is fresh or a refresh is running
    /// (a second ExecuteAsync would cancel the first one's network call).</summary>
    public Task OnShownAsync() => !RefreshCommand.IsRunning && DateTimeOffset.UtcNow - _lastRefresh > StaleAfter ? RefreshCommand.ExecuteAsync(null) : Task.CompletedTask;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        Error = null;
        IsBusy = true;
        if (_launcher.CurrentPack is null)
        {
            (StateBadge, StateKind, StateHeadline) = ("Loading", "checking", "Getting the latest pack…");
        }
        try
        {
            var pack = await _launcher.RefreshPackAsync(ct);
            _lastRefresh = DateTimeOffset.UtcNow;
            if (pack is null)
            {
                (StateBadge, StateKind, StateHeadline) = ("Offline", "error", "Could not load the pack. Check your internet connection and try again.");
                PrimaryAction = "Retry";
                PrimaryIcon = SymbolRegular.ArrowSync24;
                PrimaryEnabled = true;
                _state = ProfileState.NoKsp;
                TrustLine = "No pack loaded";
                TrustKind = "error";
                return;
            }
            var l = pack.Lock;
            PackName = l.Pack.Name;
            PackAuthor = l.Pack.Author ?? string.Empty;
            PackVersion = l.Pack.Version;
            PackDescription = l.Pack.Description ?? string.Empty;
            var mods = l.Ckan.Modules.Count(m => m.Kind == Core.Locking.ModuleKindDto.Package);
            var custom = l.Ckan.Modules.Count(m => m.Origin == Core.Locking.ModuleOrigin.Custom);
            ModsCount = mods.ToString(System.Globalization.CultureInfo.CurrentCulture);
            ModsDetail = custom > 0 ? $"{custom} custom" : "all from CKAN";
            DownloadSize = Format.Bytes(l.Ckan.Modules.Where(m => m.Artifact is not null).Sum(m => m.Artifact!.Size));
            DiskSize = Format.Bytes(l.Inventory.TotalBytes);
            TrustLine = pack.Trust switch
            {
                TrustLevel.VerifiedPublisher => "Verified publisher",
                TrustLevel.Unverified => "Unsigned pack",
                _ => "Signature invalid",
            };
            TrustKind = pack.Trust switch { TrustLevel.VerifiedPublisher => "ok", TrustLevel.Unverified => "warn", _ => "error" };
            WhatsNew = PackCatalogService.WhatsNew(pack);
            UpdatePrivate();
            UpdateRequirements(pack);
            UpdateState();
            LastCheck = $"Checked {DateTime.Now:HH:mm} · {pack.Source}";
            _ = _server.LoadAsync(pack, force: false, CancellationToken.None);
            await MaybeAutoUpdateAsync();
            if (_state == ProfileState.Installed)
            {
                _ = CheckIntegrityAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Refresh failed");
            Error = ErrorCatalog.From(ex);
            (StateBadge, StateKind, StateHeadline) = ("Problem", "error", "The pack could not be checked.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckIntegrityAsync()
    {
        await _launcher.RefreshIntegrityAsync(CancellationToken.None);
        UpdateState();
    }

    /// <summary>Pack updates apply by themselves on launch when nothing needs the user (no KSP running, no missing private file).</summary>
    private async Task MaybeAutoUpdateAsync()
    {
        if (_autoUpdateTried || !_settings.Current.AutoUpdatePack || _state != ProfileState.UpdateAvailable || PrivateBlocking || _runner.IsBusy)
        {
            return;
        }
        _autoUpdateTried = true;
        if (OperatingSystem.IsWindows() && Core.Profiles.GameProcessGuard.FindRunning(_launcher.ProfilePaths.GameDir).Count > 0)
        {
            return;
        }
        _log.LogInformation("Auto-updating the pack to {Version}", PackVersion);
        await PrimaryAsync(CancellationToken.None);
    }

    private void UpdatePrivate()
    {
        var pack = _launcher.CurrentPack;
        PrivateNeeds.Clear();
        if (pack is null)
        {
            ShowPrivateCard = false;
            PrivateBlocking = false;
            return;
        }
        var needs = _launcher.PrivateMods.Needs(pack.Lock, _launcher.InstalledLock());
        foreach (var n in needs)
        {
            var (kind, text) = n.State switch
            {
                PrivateNeedState.Installed => ("ok", "Installed"),
                PrivateNeedState.Ready => ("ok", "Ready"),
                PrivateNeedState.WillDownload => ("ok", "Downloads automatically"),
                PrivateNeedState.NeedsInvite => ("warn", "Needs invite code"),
                _ => ("warn", "Needs the file"),
            };
            PrivateNeeds.Add(new PrivateNeedRow(n.Name, n.Module.Version, n.Artifact.FileName ?? n.Module.Identifier, n.Detail, kind, text));
        }
        PrivateBlocking = needs.Any(n => n.State is PrivateNeedState.NeedsInvite or PrivateNeedState.NeedsFile);
        PrivateNeedsInvite = needs.Any(n => n.State == PrivateNeedState.NeedsInvite);
        // Show the card only when it asks for something, or right before a first install so friends know what is coming.
        ShowPrivateCard = PrivateBlocking || (needs.Count > 0 && _launcher.GetStatus().State == ProfileState.NotInstalled);
        if (PrivateNeedsInvite)
        {
            PrivateHeadline = "Private mods need your invite code";
            PrivateBody = "This pack includes the host's private mods. Paste the invite code the host sent you once; the launcher then downloads them, and every future update of them, by itself.";
        }
        else if (PrivateBlocking)
        {
            PrivateHeadline = "Private file needed";
            PrivateBody = "Ask the host for the file below, then drop it anywhere on this page or pick it with Browse.";
        }
        else
        {
            PrivateHeadline = "Private mods ready";
            PrivateBody = "Your invite gives this PC access to the host's private mods. They download and update automatically.";
        }
    }

    private void UpdateRequirements(ResolvedPack pack)
    {
        Requirements.Clear();
        var s = _settings.Current;
        var ksp = s.SourceKspPath is null ? null : Game.KspLocator.Inspect(s.SourceKspPath);
        Requirements.Add(new RequirementRow { Label = "Your KSP install", Ok = ksp?.HasExecutable == true, Detail = ksp?.Path ?? "not chosen" });
        Requirements.Add(new RequirementRow { Label = $"KSP {pack.Lock.Game.KspVersion}", Ok = ksp?.MatchesBuild(pack.Lock.Game.BuildId64) == true, Detail = ksp?.BuildId is null ? "build unknown" : $"build {ksp.BuildId}" });
        Requirements.Add(new RequirementRow { Label = "Making History", Ok = ksp?.HasMakingHistory == true, Detail = ksp?.HasMakingHistory == true ? "installed" : "missing" });
        Requirements.Add(new RequirementRow { Label = "Breaking Ground", Ok = ksp?.HasBreakingGround == true, Detail = ksp?.HasBreakingGround == true ? "installed" : "missing" });
        var profileRoot = _launcher.ProfilePaths.Root;
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(profileRoot))!).AvailableFreeSpace; } catch (Exception) { }
        var installed = _launcher.GetStatus().Installed is not null;
        var needed = installed ? 2L * 1024 * 1024 * 1024 : pack.Lock.Inventory.TotalBytes + 2L * 1024 * 1024 * 1024;
        Requirements.Add(new RequirementRow { Label = "Disk space", Ok = free > needed, Detail = $"{Format.Bytes(free)} free on {Path.GetPathRoot(Path.GetFullPath(profileRoot))}" });
    }

    private void UpdateState()
    {
        var status = _launcher.GetStatus();
        _state = status.State;
        var blocked = PrivateBlocking && status.State is ProfileState.NotInstalled or ProfileState.UpdateAvailable;
        (StateBadge, StateKind, StateHeadline, PrimaryAction, PrimaryIcon, PrimaryEnabled) = status.State switch
        {
            ProfileState.NoKsp => ("Setup needed", "warn", "Tell the launcher where your Kerbal Space Program is.", "Set up", SymbolRegular.Settings24, true),
            ProfileState.NotInstalled => ("Not installed", "neutral", blocked ? "Add the private mods below, then install." : "Install the pack: about " + DownloadSize + " to download.", "Install", SymbolRegular.ArrowDownload24, !blocked),
            ProfileState.Installed => ($"Installed · {status.Installed!.PackVersion}", "ok", "Everything is up to date. Ready to fly.", "Play", SymbolRegular.Rocket24, true),
            ProfileState.UpdateAvailable => ("Update available", "warn", $"Version {status.Detail} is ready. Only changed mods are downloaded.", "Update", SymbolRegular.ArrowSync24, !blocked),
            ProfileState.Modified => ($"{status.ModifiedFiles} files differ", "warn", "Some files changed since the install. Repair puts them back.", "Repair", SymbolRegular.Wrench24, true),
            ProfileState.Interrupted => ("Interrupted", "error", status.Detail ?? "A previous operation was interrupted.", "Restore", SymbolRegular.History24, true),
            _ => ("Unknown", "neutral", string.Empty, "Refresh", SymbolRegular.ArrowSync24, true),
        };
        if (_runner.IsBusy)
        {
            PrimaryEnabled = false;
            (StateBadge, StateKind) = _runner.Snapshot?.Kind switch
            {
                OperationKind.Publish => ("Host tools busy", "checking"),
                OperationKind.Cleanup => ("Cleaning up", "checking"),
                _ => ("Working", "checking"),
            };
            if (_runner.Snapshot?.Kind == OperationKind.Publish)
            {
                StateHeadline = "Host tools is publishing or checking the pack. Install and Play are back when it finishes.";
            }
        }
    }

    private void SampleOperation()
    {
        var s = _runner.Snapshot;
        // Host publishing has its own page and log; the Home hero only shows the player's own installs and updates,
        // so its Cancel button can never stop a publish by accident.
        var shown = s is not null && s.Kind is not (OperationKind.Publish or OperationKind.Cleanup);
        if (IsOperationRunning != shown || (s is null) != (_lastKind is null) || s?.Kind != _lastKind)
        {
            _lastKind = s?.Kind;
            IsOperationRunning = shown;
            UpdateState();
        }
        if (!shown || s is null)
        {
            return;
        }
        OpTitle = s.Title;
        OpMessage = s.Message;
        OpIndeterminate = s.Percent <= 0;
        OpPercent = Math.Clamp(s.Percent, 0, 100);
        var parts = new List<string>();
        if (s.Percent > 0)
        {
            parts.Add($"{OpPercent}%");
        }
        if (s.BytesPerSecond is { } bps && bps > 0)
        {
            parts.Add(Format.Speed(bps));
        }
        if (!string.IsNullOrEmpty(s.TimeLeft))
        {
            parts.Add($"{s.TimeLeft} left");
        }
        parts.Add($"{DateTimeOffset.UtcNow - s.StartedUtc:m\\:ss} elapsed");
        OpStats = string.Join("  ·  ", parts);
    }

    [RelayCommand]
    private async Task PrimaryAsync(CancellationToken ct)
    {
        Error = null;
        try
        {
            switch (_state)
            {
                case ProfileState.NoKsp when _launcher.CurrentPack is null:
                    await RefreshAsync(ct);
                    return;
                case ProfileState.NoKsp:
                    _navigation.Navigate(AppPage.Welcome);
                    return;
                case ProfileState.Installed:
                    _navigation.Navigate(AppPage.Play);
                    return;
                case ProfileState.Interrupted:
                    PrimaryEnabled = false;
                    await _launcher.RecoverAsync(ct);
                    break;
                case ProfileState.Modified:
                    PrimaryEnabled = false;
                    await _launcher.RepairAsync(false, ct);
                    break;
                default:
                    PrimaryEnabled = false;
                    await _launcher.InstallOrUpdateAsync(ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Primary action failed");
            Error = ErrorCatalog.From(ex);
        }
        UpdatePrivate();
        UpdateState();
    }

    [RelayCommand]
    private void CancelOperation() => _runner.RequestCancel();

    [RelayCommand]
    private void ApplyInvite()
    {
        Error = null;
        try
        {
            var invite = _launcher.PrivateMods.ApplyInvite(InviteText, _launcher.PasswordKey);
            InviteText = string.Empty;
            UpdatePrivate();
            UpdateState();
            if (invite.ServerPassword is not null)
            {
                _log.LogInformation("Server password saved from invite");
            }
        }
        catch (LauncherException ex)
        {
            Error = ErrorCatalog.From(ex);
        }
    }

    [RelayCommand]
    private void BrowsePrivateFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the private mod zip the host sent you", Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
        {
            AcceptSideloadFile(dlg.FileName);
        }
    }

    /// <summary>A private package zip was dropped on the page or picked with Browse.</summary>
    public void AcceptSideloadFile(string path)
    {
        var pack = _launcher.CurrentPack;
        if (pack is null || !File.Exists(path))
        {
            return;
        }
        if (InviteCodeFile(path) is { } text)
        {
            InviteText = text;
            ApplyInvite();
            return;
        }
        try
        {
            _launcher.PrivateMods.AcceptFile(pack.Lock, path);
            Error = null;
        }
        catch (LauncherException ex)
        {
            Error = ErrorCatalog.From(ex);
        }
        UpdatePrivate();
        UpdateState();
    }

    /// <summary>A dropped .txt holding an invite code works too.</summary>
    private static string? InviteCodeFile(string path)
    {
        if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 64 * 1024)
        {
            return null;
        }
        var text = File.ReadAllText(path);
        return Core.Private.InviteCode.LooksLikeInvite(text) ? text : null;
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
    private void OpenMods() => _navigation.Navigate(AppPage.Mods);
}
