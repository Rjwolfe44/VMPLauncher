using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace VmpLauncher.App.Services;

/// <summary>
/// Self-update through Velopack + GitHub Releases. Checks shortly after launch and every 6 hours, downloads in the
/// background, and only ever applies when the user clicks "Restart to update" while no operation is running.
/// Development builds (not installed by Velopack) report "updates off".
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    public const string RepoUrl = "https://github.com/Rjwolfe44/VMPLauncher";

    private readonly AppSettingsStore _settings;
    private readonly OperationRunner _runner;
    private readonly ILogger<UpdateService> _log;
    private readonly CancellationTokenSource _cts = new();
    private UpdateManager? _manager;
    private UpdateInfo? _pending;
    private VelopackAsset? _pendingAsset;
    private Timer? _timer;

    public UpdateService(AppSettingsStore settings, OperationRunner runner, ILogger<UpdateService> log)
    {
        _settings = settings;
        _runner = runner;
        _log = log;
    }

    [ObservableProperty] private string _status = "Not checked yet.";
    [ObservableProperty] private bool _updateReady;
    [ObservableProperty] private string? _availableVersion;
    [ObservableProperty] private bool _isDevelopmentBuild;

    public void Start()
    {
        try
        {
            var beta = _settings.Current.UpdateChannel == "beta";
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, beta), new UpdateOptions { ExplicitChannel = beta ? "beta" : null });
            if (!_manager.IsInstalled)
            {
                IsDevelopmentBuild = true;
                Status = "Development build: automatic updates are off.";
                return;
            }
            if (_manager.UpdatePendingRestart is { } pendingAsset)
            {
                _pendingAsset = pendingAsset;
                AvailableVersion = pendingAsset.Version.ToString();
                UpdateReady = true;
                Status = $"Version {AvailableVersion} is downloaded. Restart to update.";
                return;
            }
            if (_settings.Current.AutoCheckUpdates)
            {
                _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromHours(6));
            }
            else
            {
                Status = "Automatic checks are off.";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update manager unavailable");
            IsDevelopmentBuild = true;
            Status = "Updates unavailable in this build.";
        }
    }

    public async Task CheckAsync()
    {
        if (_manager is null || IsDevelopmentBuild)
        {
            return;
        }
        try
        {
            Status = "Checking for updates…";
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                Status = $"Up to date ({_manager.CurrentVersion}). Checked {DateTime.Now:HH:mm}.";
                return;
            }
            AvailableVersion = info.TargetFullRelease.Version.ToString();
            Status = $"Downloading {AvailableVersion}…";
            await _manager.DownloadUpdatesAsync(info, p => Status = $"Downloading {AvailableVersion}… {p}%", _cts.Token).ConfigureAwait(false);
            _pending = info;
            UpdateReady = true;
            Status = $"Version {AvailableVersion} is ready. Restart to update.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Update check failed");
            Status = "Could not check for updates (offline?).";
        }
    }

    /// <summary>Applies the downloaded update and restarts. Refused while an install/update/repair is running.</summary>
    public bool TryRestartToUpdate()
    {
        if (_manager is null || _runner.IsBusy)
        {
            return false;
        }
        if (_pending is not null)
        {
            _manager.ApplyUpdatesAndRestart(_pending);
            return true;
        }
        if (_pendingAsset is not null)
        {
            _manager.ApplyUpdatesAndRestart(_pendingAsset);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _cts.Dispose();
    }
}
