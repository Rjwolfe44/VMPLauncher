using Microsoft.Extensions.Logging;
using VmpLauncher.Core.Profiles;

namespace VmpLauncher.App.Services;

public enum PackCheckOutcome
{
    /// <summary>The installed pack is the newest (or nothing is installed yet: Home handles first installs).</summary>
    UpToDate,
    /// <summary>A newer pack was found and installed.</summary>
    Updated,
    /// <summary>A newer pack exists but was not installed (automatic updates off, KSP running, or another operation busy).</summary>
    UpdateNotApplied,
    /// <summary>A newer pack exists and installing it failed.</summary>
    Failed,
    /// <summary>Skipped: checked recently enough.</summary>
    Skipped,
}

public sealed record PackCheckResult(PackCheckOutcome Outcome, string Message, UserFacingError? Error = null);

/// <summary>
/// Looks for a newer pack at the moments it matters (before Play, when KSP closes, when the player comes back to the
/// launcher) instead of polling. A check is three small catalog requests; the big files only download when the pack
/// really changed. GitHub's raw files are cached for up to five minutes, so a pack published a moment ago can take
/// that long to show up.
/// </summary>
public sealed class PackUpdateService
{
    private readonly LauncherService _launcher;
    private readonly AppSettingsStore _settings;
    private readonly OperationRunner _runner;
    private readonly ILogger<PackUpdateService> _log;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private Task<PackCheckResult>? _running;

    public PackUpdateService(LauncherService launcher, AppSettingsStore settings, OperationRunner runner, ILogger<PackUpdateService> log)
    {
        _launcher = launcher;
        _settings = settings;
        _runner = runner;
        _log = log;
    }

    /// <summary>
    /// Checks unless a check finished within <paramref name="minAge"/>, and installs a newer pack when
    /// <paramref name="apply"/> is set, automatic updates are on and KSP is not running. Concurrent callers share one check.
    /// </summary>
    public Task<PackCheckResult> CheckAsync(TimeSpan minAge, bool apply, CancellationToken ct)
    {
        if (_running is { IsCompleted: false } running)
        {
            return running;
        }
        if (DateTimeOffset.UtcNow - _lastCheck < minAge && _launcher.GetStatus().State != ProfileState.UpdateAvailable)
        {
            return Task.FromResult(new PackCheckResult(PackCheckOutcome.Skipped, "Checked a moment ago."));
        }
        _running = RunAsync(apply, ct);
        return _running;
    }

    private async Task<PackCheckResult> RunAsync(bool apply, CancellationToken ct)
    {
        if (_runner.IsBusy)
        {
            return new PackCheckResult(PackCheckOutcome.UpdateNotApplied, "Another operation is running.");
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await _launcher.RefreshPackAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Slow or no network: play with what is installed; the next moment checks again.
                return new PackCheckResult(PackCheckOutcome.UpToDate, "Could not reach the pack catalog; using the installed pack.");
            }
            _lastCheck = DateTimeOffset.UtcNow;
            var status = _launcher.GetStatus();
            if (status.State != ProfileState.UpdateAvailable)
            {
                return new PackCheckResult(PackCheckOutcome.UpToDate, "The pack is up to date.");
            }
            var version = status.Pack?.Lock.Pack.Version ?? "a newer version";
            if (!apply || !_settings.Current.AutoUpdatePack)
            {
                return new PackCheckResult(PackCheckOutcome.UpdateNotApplied, $"Pack {version} is out.");
            }
            if (OperatingSystem.IsWindows() && GameProcessGuard.FindRunning(_launcher.ProfilePaths.GameDir).Count > 0)
            {
                return new PackCheckResult(PackCheckOutcome.UpdateNotApplied, $"Pack {version} is out; it installs when KSP closes.");
            }
            if (_runner.IsBusy)
            {
                return new PackCheckResult(PackCheckOutcome.UpdateNotApplied, "Another operation is running.");
            }
            _log.LogInformation("Installing pack {Version} before playing / after a check", version);
            await _launcher.InstallOrUpdateAsync(ct).ConfigureAwait(true);
            return new PackCheckResult(PackCheckOutcome.Updated, $"Updated to pack {version}.");
        }
        catch (OperationCanceledException)
        {
            return new PackCheckResult(PackCheckOutcome.UpdateNotApplied, "The update was cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pack check or update failed");
            var error = ErrorCatalog.From(ex);
            return new PackCheckResult(PackCheckOutcome.Failed, error.Message, error);
        }
    }
}
