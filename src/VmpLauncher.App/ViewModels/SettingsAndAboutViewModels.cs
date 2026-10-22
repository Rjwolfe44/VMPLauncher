using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;
using VmpLauncher.Core;

namespace VmpLauncher.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _settings;
    private readonly LauncherPaths _paths;

    public SettingsViewModel(AppSettingsStore settings, LauncherPaths paths)
    {
        _settings = settings;
        _paths = paths;
        var s = settings.Current;
        SourceKspPath = s.SourceKspPath ?? string.Empty;
        ProfileRoot = s.ProfileRoot ?? Path.Combine(paths.ProfilesDir, s.ActiveProfileId);
        CacheDir = s.CacheDirOverride ?? paths.CacheDir;
        LocalPackDir = s.LocalPackDir ?? string.Empty;
        CatalogUrl = s.CatalogUrl ?? PackCatalogService.DefaultCatalogUrl;
        BorderlessWindow = s.BorderlessWindow;
        MinimizeWhilePlaying = s.MinimizeWhilePlaying;
        DisclaimerAccepted = s.DisclaimerAccepted;
        AutoCheckUpdates = s.AutoCheckUpdates;
        BetaChannel = s.UpdateChannel == "beta";
        TextScalePercent = (int)Math.Round(s.TextScale * 100);
    }

    public string DataRoot => _paths.DataRoot;
    public string LogsDir => _paths.LogsDir;

    [ObservableProperty] private string _sourceKspPath = string.Empty;
    [ObservableProperty] private string _profileRoot = string.Empty;
    [ObservableProperty] private string _cacheDir = string.Empty;
    [ObservableProperty] private string _localPackDir = string.Empty;
    [ObservableProperty] private string _catalogUrl = string.Empty;
    [ObservableProperty] private bool _borderlessWindow;
    [ObservableProperty] private bool _minimizeWhilePlaying;
    [ObservableProperty] private bool _disclaimerAccepted;
    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private bool _betaChannel;
    [ObservableProperty] private int _textScalePercent = 100;
    [ObservableProperty] private string _cacheSize = string.Empty;

    partial void OnBorderlessWindowChanged(bool value) => _settings.Update(s => s with { BorderlessWindow = value });
    partial void OnMinimizeWhilePlayingChanged(bool value) => _settings.Update(s => s with { MinimizeWhilePlaying = value });
    partial void OnDisclaimerAcceptedChanged(bool value) => _settings.Update(s => s with { DisclaimerAccepted = value });
    partial void OnAutoCheckUpdatesChanged(bool value) => _settings.Update(s => s with { AutoCheckUpdates = value });
    partial void OnBetaChannelChanged(bool value) => _settings.Update(s => s with { UpdateChannel = value ? "beta" : "win" });
    partial void OnTextScalePercentChanged(int value) => _settings.Update(s => s with { TextScale = Math.Clamp(value, 100, 200) / 100.0 });
    partial void OnLocalPackDirChanged(string value) => _settings.Update(s => s with { LocalPackDir = string.IsNullOrWhiteSpace(value) ? null : value });
    partial void OnCatalogUrlChanged(string value) => _settings.Update(s => s with { CatalogUrl = string.IsNullOrWhiteSpace(value) || value == PackCatalogService.DefaultCatalogUrl ? null : value });

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
        var size = await Task.Run(() => Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0L);
        CacheSize = Format.Bytes(size);
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
        "Bundled components: CKAN core (MIT, KSP-CKAN contributors) · WPF-UI (MIT, lepo.co) · Velopack (MIT) · " +
        "CommunityToolkit.Mvvm (MIT, .NET Foundation) · Lidgren.Network (MIT, Michael Lidgren) · Microsoft.Extensions (MIT).\n" +
        "Kerbal Space Program and its DLC are property of their owners and are never redistributed by this launcher.";

    [RelayCommand]
    private void OpenRepo() => Process.Start(new ProcessStartInfo("https://github.com/Rjwolfe44/VMPLauncher") { UseShellExecute = true });
}
