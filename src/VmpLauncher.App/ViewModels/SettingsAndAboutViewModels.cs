using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.App.ViewModels;

public partial class PrivateRepoRow : ObservableObject
{
    public required string Repo { get; init; }
    [ObservableProperty] private string _status = "Not checked";
    [ObservableProperty] private string _statusKind = "neutral";
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _settings;
    private readonly LauncherPaths _paths;
    private readonly LauncherService _launcher;
    private readonly IAppNavigator _navigation;

    public SettingsViewModel(AppSettingsStore settings, LauncherPaths paths, LauncherService launcher, IAppNavigator navigation)
    {
        _settings = settings;
        _paths = paths;
        _launcher = launcher;
        _navigation = navigation;
        var s = settings.Current;
        _sourceKspPath = s.SourceKspPath ?? "(not chosen)";
        _profileRoot = launcher.ProfilePaths.Root;
        _cacheDir = s.CacheDirOverride ?? paths.CacheDir;
        _localPackDir = s.LocalPackDir ?? string.Empty;
        _catalogUrl = s.CatalogUrl ?? PackCatalogService.DefaultCatalogUrl;
        _borderlessWindow = s.BorderlessWindow;
        _minimizeWhilePlaying = s.MinimizeWhilePlaying;
        _disclaimerAccepted = s.DisclaimerAccepted;
        _autoCheckUpdates = s.AutoCheckUpdates;
        _autoUpdatePack = s.AutoUpdatePack;
        _betaChannel = s.UpdateChannel == "beta";
        _textScalePercent = (int)Math.Round(s.TextScale * 100);
        launcher.PrivateMods.Changed += LoadRepos;
        LoadRepos();
    }

    public string DataRoot => _paths.DataRoot;
    public string LogsDir => _paths.LogsDir;
    public ObservableCollection<PrivateRepoRow> PrivateRepos { get; } = new();

    [ObservableProperty] private string _sourceKspPath;
    [ObservableProperty] private string _profileRoot;
    [ObservableProperty] private string _cacheDir;
    [ObservableProperty] private string _localPackDir;
    [ObservableProperty] private string _catalogUrl;
    [ObservableProperty] private bool _borderlessWindow;
    [ObservableProperty] private bool _minimizeWhilePlaying;
    [ObservableProperty] private bool _disclaimerAccepted;
    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private bool _autoUpdatePack;
    [ObservableProperty] private bool _betaChannel;
    [ObservableProperty] private int _textScalePercent = 100;
    [ObservableProperty] private string _cacheSize = string.Empty;
    [ObservableProperty] private string _cleanupInfo = string.Empty;
    [ObservableProperty] private bool _canCleanup;
    [ObservableProperty] private string _inviteText = string.Empty;
    [ObservableProperty] private string _inviteResult = string.Empty;
    [ObservableProperty] private string _inviteResultKind = "neutral";
    [ObservableProperty] private bool _hasPrivateRepos;

    partial void OnBorderlessWindowChanged(bool value) => _settings.Update(s => s with { BorderlessWindow = value });
    partial void OnMinimizeWhilePlayingChanged(bool value) => _settings.Update(s => s with { MinimizeWhilePlaying = value });
    partial void OnDisclaimerAcceptedChanged(bool value) => _settings.Update(s => s with { DisclaimerAccepted = value });
    partial void OnAutoCheckUpdatesChanged(bool value) => _settings.Update(s => s with { AutoCheckUpdates = value });
    partial void OnAutoUpdatePackChanged(bool value) => _settings.Update(s => s with { AutoUpdatePack = value });
    partial void OnBetaChannelChanged(bool value) => _settings.Update(s => s with { UpdateChannel = value ? "beta" : "win" });
    partial void OnTextScalePercentChanged(int value) => _settings.Update(s => s with { TextScale = Math.Clamp(value, 100, 200) / 100.0 });
    partial void OnLocalPackDirChanged(string value) => _settings.Update(s => s with { LocalPackDir = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    partial void OnCatalogUrlChanged(string value) => _settings.Update(s => s with { CatalogUrl = string.IsNullOrWhiteSpace(value) || value.Trim() == PackCatalogService.DefaultCatalogUrl ? null : value.Trim() });

    private void LoadRepos()
    {
        PrivateRepos.Clear();
        foreach (var repo in _settings.Current.PrivateRepos)
        {
            PrivateRepos.Add(new PrivateRepoRow { Repo = repo, Status = _launcher.PrivateMods.HasAccess(repo) ? "Invite saved" : "No access", StatusKind = _launcher.PrivateMods.HasAccess(repo) ? "ok" : "warn" });
        }
        HasPrivateRepos = PrivateRepos.Count > 0;
    }

    [RelayCommand]
    private async Task CheckReposAsync(CancellationToken ct)
    {
        foreach (var row in PrivateRepos)
        {
            row.Status = "Checking…";
            row.StatusKind = "checking";
            var r = await _launcher.PrivateMods.CheckAccessAsync(row.Repo, ct);
            row.Status = r.Message;
            row.StatusKind = r.State == Core.Private.GitHubPrivateReleases.AccessState.Ok ? "ok" : "error";
        }
    }

    [RelayCommand]
    private void ApplyInvite()
    {
        try
        {
            var invite = _launcher.PrivateMods.ApplyInvite(InviteText, _launcher.PasswordKey);
            InviteText = string.Empty;
            InviteResultKind = "ok";
            InviteResult = (invite.Repo, invite.ServerPassword) switch
            {
                ({ } repo, { }) => $"Saved: private mods from {repo} and the server password.",
                ({ } repo, null) => $"Saved: private mods from {repo}.",
                (null, { }) => "Saved: the server password.",
                _ => "The invite was empty.",
            };
        }
        catch (LauncherException ex)
        {
            InviteResultKind = "error";
            InviteResult = ex.Message;
        }
    }

    [RelayCommand]
    private void ForgetRepo(PrivateRepoRow row) => _launcher.PrivateMods.Forget(row.Repo);

    [RelayCommand]
    private void RunSetup() => _navigation.Navigate(AppPage.Welcome);

    [RelayCommand]
    private void OpenLogs() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.LogsDir}\"") { UseShellExecute = true });

    [RelayCommand]
    private void OpenData() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.DataRoot}\"") { UseShellExecute = true });

    [RelayCommand]
    private void BrowseLocalPack()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose an exported pack folder (contains *.vmpack.lock.json)" };
        if (dlg.ShowDialog() == true)
        {
            LocalPackDir = dlg.FolderName;
        }
    }

    [RelayCommand]
    private void ClearLocalPack() => LocalPackDir = string.Empty;

    [RelayCommand]
    private void BrowseCache()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the download cache folder (an existing CKAN cache works)" };
        if (dlg.ShowDialog() == true)
        {
            CacheDir = dlg.FolderName;
            _settings.Update(s => s with { CacheDirOverride = string.Equals(dlg.FolderName, _paths.CacheDir, StringComparison.OrdinalIgnoreCase) ? null : dlg.FolderName });
        }
    }

    [RelayCommand]
    private async Task MeasureCacheAsync()
    {
        var dir = CacheDir;
        CacheSize = "measuring…";
        CanCleanup = false;
        var size = await Task.Run(() =>
        {
            try
            {
                return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0L;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return -1L;
            }
        });
        CacheSize = size < 0 ? "could not measure" : Format.Bytes(size);
        if (!_launcher.OwnsCache)
        {
            CleanupInfo = "This folder is shared with another program (e.g. your own CKAN), so the launcher leaves it alone.";
            return;
        }
        var plan = await _launcher.AnalyzeCacheAsync();
        if (plan is null)
        {
            CleanupInfo = "Open Home once so the launcher knows which downloads the pack still needs.";
            return;
        }
        CanCleanup = plan.Remove.Count > 0;
        CleanupInfo = plan.Remove.Count == 0
            ? "Nothing to clean up: every download is still used by the pack."
            : $"{Format.Bytes(plan.RemoveBytes)} in {plan.Remove.Count} old download(s) is no longer used by the pack or your install.";
    }

    [RelayCommand]
    private async Task CleanupCacheAsync(CancellationToken ct)
    {
        CanCleanup = false;
        try
        {
            var (files, bytes) = await _launcher.CleanCacheAsync(ct);
            CleanupInfo = $"Freed {Format.Bytes(bytes)} ({files} file(s)).";
        }
        catch (OperationCanceledException)
        {
            CleanupInfo = "Cleanup cancelled.";
        }
        catch (LauncherException ex)
        {
            CleanupInfo = ex.Message;
        }
        await MeasureCacheCommand.ExecuteAsync(null);
    }
}

public partial class AboutViewModel : ObservableObject
{
    public AboutViewModel(UpdateService updates)
    {
        Updates = updates;
    }

    public UpdateService Updates { get; }

    [RelayCommand]
    private Task CheckUpdatesAsync() => Updates.CheckAsync();

    [RelayCommand]
    private void RestartToUpdate() => Updates.TryRestartToUpdate();

    public string Version => AppInfo.Version;
    public string CkanVersion => Ckan.CkanBootstrap.CoreVersion;
    public string Runtime => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string Os => Environment.OSVersion.VersionString;

    public string Notices =>
        "VMP Launcher is free software by Rjwolfe44.\n" +
        "Bundled components: CKAN core (MIT, KSP-CKAN contributors), WPF-UI (MIT, lepo.co), Velopack (MIT), " +
        "CommunityToolkit.Mvvm (MIT, .NET Foundation), Lidgren.Network (MIT, Michael Lidgren), Microsoft.Extensions (MIT).\n" +
        "Kerbal Space Program and its DLC belong to their owners and are never redistributed by this launcher.";

    [RelayCommand]
    private void OpenRepo() => Process.Start(new ProcessStartInfo("https://github.com/Rjwolfe44/VMPLauncher") { UseShellExecute = true });
}
