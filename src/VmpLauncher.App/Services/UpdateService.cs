using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace VmpLauncher.App.Services;

/// <summary>
/// Self-update through Velopack + GitHub Releases. Checks shortly after launch and every 6 hours and downloads in the
/// background. A downloaded update installs by itself when the launcher closes (and, as Velopack's fallback, on the
/// next start), so nobody has to click anything; "Restart now" just does it immediately. Never while an install,
/// update or repair is running. Development builds (not installed by Velopack) report "updates off".
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
    private string? _channel;

    public UpdateService(AppSettingsStore settings, OperationRunner runner, ILogger<UpdateService> log)
    {
        _settings = settings;
        // Switching the beta channel in Settings takes effect at once (no restart needed to look for the other feed).
        settings.Changed += s =>
        {
            if (_channel is not null && s.UpdateChannel != _channel)
            {
                log.LogInformation("Update channel changed to {Channel}", s.UpdateChannel);
                Start();
                if (!s.AutoCheckUpdates)
                {
                    _ = CheckAsync();
                }
            }
        };
        _runner = runner;
        _log = log;
    }

    [ObservableProperty] private string _status = "Not checked yet.";
    [ObservableProperty] private bool _updateReady;
    [ObservableProperty] private string? _availableVersion;
    [ObservableProperty] private bool _isDevelopmentBuild;

    public void Start()
    {
        _timer?.Dispose();
        _timer = null;
        _channel = _settings.Current.UpdateChannel;
        try
        {
            var beta = _channel == "beta";
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
                Status = $"Version {AvailableVersion} is downloaded and installs when you close the launcher.";
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
            Status = $"Version {AvailableVersion} is ready and installs when you close the launcher (or restart now).";
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

    /// <summary>
    /// Called while the launcher exits: hands a downloaded update to Velopack's updater, which waits for this process
    /// to end, installs silently, and does not relaunch. Skipped while an operation is running (the next start then
    /// applies it, which Velopack does by default).
    /// </summary>
    public void ApplyOnExit()
    {
        try
        {
            if (_manager is null || IsDevelopmentBuild || _runner.IsBusy)
            {
                return;
            }
            var asset = _pending?.TargetFullRelease ?? _pendingAsset ?? _manager.UpdatePendingRestart;
            if (asset is null)
            {
                return;
            }
            _log.LogInformation("Installing launcher {Version} on exit", asset.Version);
            _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            // Never block closing; Velopack applies the update on the next start instead.
            _log.LogWarning(ex, "Could not hand the update to the updater on exit");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _cts.Dispose();
    }
}
