using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Game;
using VmpLauncher.Net.Doctor;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace VmpLauncher.App.Services;

/// <summary>What a recovery action did: <see cref="Error"/> is null when it worked.</summary>
public sealed record RecoveryResult(RecoveryHint Action, string Message, UserFacingError? Error);

/// <summary>
/// The ways out of a stuck pack copy, shared by every error card and the Troubleshoot section: the error's own fix
/// (Repair, Restore, Reinstall, or the page that fixes it), Troubleshoot, and a report for the host. Pages listen to
/// <see cref="Completed"/> to clear their error and refresh. The error card template binds to <see cref="Instance"/>.
/// </summary>
public sealed partial class RecoveryActions : ObservableObject
{
    /// <summary>Set to anything to skip opening Explorer after "Send my logs" (UI automation and CI must not pop windows over a running game).</summary>
    public const string NoRevealVariable = "VMPL_NO_REVEAL";

    private readonly LauncherService _launcher;
    private readonly IAppNavigator _navigation;
    private readonly IContentDialogService _dialogs;
    private readonly ISnackbarService _snackbar;
    private readonly AppSettingsStore _settings;
    private readonly LauncherPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly ILogger<RecoveryActions> _log;

    public RecoveryActions(LauncherService launcher, IAppNavigator navigation, IContentDialogService dialogs, ISnackbarService snackbar, AppSettingsStore settings,
        LauncherPaths paths, ISecretStore secrets, ILogger<RecoveryActions> log)
    {
        _launcher = launcher;
        _navigation = navigation;
        _dialogs = dialogs;
        _snackbar = snackbar;
        _settings = settings;
        _paths = paths;
        _secrets = secrets;
        _log = log;
        Instance = this;
    }

    /// <summary>Set when the app starts (the service is created then), so XAML can bind to it with x:Static.</summary>
    public static RecoveryActions? Instance { get; private set; }

    public event Action<RecoveryResult>? Completed;

    /// <summary>True while a logs zip is being made; the button is off meanwhile, so a double click makes one zip.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendLogsCommand))]
    private bool _isSendingLogs;

    /// <summary>What "Send my logs" is doing right now ("Reading the game log… 42%"), for the card next to the button.</summary>
    [ObservableProperty]
    private string _logsStatus = string.Empty;

    [RelayCommand]
    private async Task FixAsync(UserFacingError? error)
    {
        switch (error?.Hint)
        {
            case RecoveryHint.Repair:
                await RepairAsync();
                break;
            case RecoveryHint.Restore:
                await RestoreAsync();
                break;
            case RecoveryHint.Reinstall:
                await ReinstallAsync();
                break;
            case RecoveryHint.ChooseAnotherFolder:
                _navigation.Navigate(AppPage.Welcome);
                break;
            case RecoveryHint.FreeDiskSpace:
                _navigation.Navigate(AppPage.Settings);
                break;
            case RecoveryHint.ProvideFile:
                _navigation.Navigate(AppPage.Home);
                break;
        }
    }

    /// <summary>Checks every pack file and puts back what changed or went missing; DLLs the pack doesn't know move to the copy's quarantine folder.</summary>
    public Task RepairAsync() => RunAsync(RecoveryHint.Repair, async () =>
    {
        var r = await _launcher.RepairAsync(true, CancellationToken.None);
        return r.FilesRestored + r.Quarantined == 0 ? "Checked every file: nothing needed fixing."
             : $"Repaired: {Plural(r.FilesRestored, "file")} put back{(r.Quarantined > 0 ? $", {Plural(r.Quarantined, "unknown DLL")} moved to the quarantine folder" : string.Empty)}.";
    });

    /// <summary>Finishes or undoes an operation that was interrupted (crash, power cut) by restoring its snapshot.</summary>
    public Task RestoreAsync() => RunAsync(RecoveryHint.Restore, async () =>
    {
        await _launcher.RecoverAsync(CancellationToken.None);
        return "Restored: the pack copy is back to its last good state.";
    });

    /// <summary>Asks first, then rebuilds the pack copy keeping the player's own files (<see cref="LauncherService.ReinstallAsync"/>).</summary>
    public async Task ReinstallAsync()
    {
        var answer = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Reinstall the pack copy?",
            Content = "The launcher builds a fresh copy of the pack and moves your own files into it: saves, craft, missions, screenshots, "
                    + "KSP's settings and the mods' settings. Everything else is rebuilt from the pack, using the downloads you already have.\n\n"
                    + "It takes a few minutes. If anything goes wrong, your current copy is put back as it was. Your own KSP folder is never touched.",
            PrimaryButtonText = "Reinstall",
            CloseButtonText = "Cancel",
        });
        if (answer != ContentDialogResult.Primary)
        {
            return;
        }
        await RunAsync(RecoveryHint.Reinstall, async () =>
        {
            await _launcher.ReinstallAsync(CancellationToken.None);
            return $"Reinstalled {_launcher.CurrentPack?.Lock.Pack.Version}: a fresh copy with your saves and settings.";
        });
    }

    private async Task RunAsync(RecoveryHint action, Func<Task<string>> work)
    {
        RecoveryResult result;
        try
        {
            result = new RecoveryResult(action, await work(), null);
            _snackbar.Show("Done", result.Message, ControlAppearance.Success, new SymbolIcon(SymbolRegular.CheckmarkCircle24), TimeSpan.FromSeconds(6));
        }
        catch (OperationCanceledException)
        {
            result = new RecoveryResult(action, "Cancelled.", null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recovery action {Action} failed", action);
            result = new RecoveryResult(action, string.Empty, ErrorCatalog.From(ex));
        }
        Completed?.Invoke(result);
    }

    [RelayCommand]
    private void Troubleshoot() => _navigation.Navigate(AppPage.Doctor);

    [RelayCommand]
    private void CopyReport(UserFacingError? error)
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildReport(error));
            _snackbar.Show("Report copied", "Paste it in a message to the host. Passwords and your user name are removed.", ControlAppearance.Info,
                new SymbolIcon(SymbolRegular.Copy24), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Clipboard busy");
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_paths.LogsDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _log.LogWarning(ex, "Could not open the logs folder");
        }
    }

    /// <summary>
    /// "Send my logs": one small zip on the Desktop (the game log, the launcher logs and a report, with the Windows user name
    /// and the secrets removed) to drop in Discord for the host; Explorer opens with it selected. It only reads, so it also
    /// runs while another operation is stuck; otherwise it shows up as an operation with progress.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendLogs))]
    private async Task SendLogsAsync()
    {
        IsSendingLogs = true;
        LogsStatus = "Collecting the logs…";
        try
        {
            var options = BuildLogBundleOptions();
            var progress = new Progress<LogBundleProgress>(p =>
            {
                LogsStatus = p.Percent is > 0 and < 100 ? $"{p.Message}… {p.Percent}%" : $"{p.Message}…";
                _launcher.Runner.Report(p.Message, p.Percent);
            });
            LogBundleResult result;
            try
            {
                result = await _launcher.Runner.RunAsync(OperationKind.Logs, "Collecting logs for the host", (_, ct) => Task.Run(() => LogBundle.Build(options, progress, ct), ct));
            }
            catch (LauncherException ex) when (ex.Code == ErrorCode.ProfileBusy)
            {
                // An install or repair is running (maybe the very one that is failing): reading logs does not touch it, so don't wait for it.
                result = await Task.Run(() => LogBundle.Build(options, progress, CancellationToken.None));
            }
            RevealInExplorer(result.Path);
            var message = $"Saved {Path.GetFileName(result.Path)} ({LogBundle.FormatBytes(result.ZipBytes)}). Drop it in Discord for the host.";
            if (result.Missing.Count > 0)
            {
                message += $" Not included: {string.Join(", ", result.Missing.Select(m => m.Split(':')[0]))}.";
            }
            _snackbar.Show("Logs ready", message, result.Missing.Count > 0 ? ControlAppearance.Caution : ControlAppearance.Success,
                new SymbolIcon(SymbolRegular.FolderZip24), TimeSpan.FromSeconds(14));
        }
        catch (OperationCanceledException)
        {
            _snackbar.Show("Cancelled", "No logs were saved.", ControlAppearance.Info, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Send my logs failed");
            _snackbar.Show("Could not collect the logs", ShortReason(ex), ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), TimeSpan.FromSeconds(10));
        }
        finally
        {
            IsSendingLogs = false;
            LogsStatus = string.Empty;
        }
    }

    private bool CanSendLogs() => !IsSendingLogs;

    private static string ShortReason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows would not let the launcher write to your Desktop. Try Copy report instead.",
        IOException io when (io.HResult & 0xFFFF) is 0x27 or 0x70 => "There is not enough free disk space for the zip.",
        IOException io => $"A file could not be read or written: {io.Message}",
        _ => $"Something went wrong ({ex.GetType().Name}). Try again, or use Copy report.",
    };

    private LogBundleOptions BuildLogBundleOptions()
    {
        var now = DateTimeOffset.Now;
        var s = _settings.Current;
        var player = s.PlayerName is { } name && VmpSettingsFile.PlayerNamePattern.IsMatch(name) ? name : "player";
        var facts = new List<KeyValuePair<string, string>>
        {
            new("Launcher", $"{AppInfo.Version}, {(s.UpdateChannel == "beta" ? "beta" : "stable")} channel"),
            new("Windows", Environment.OSVersion.Version.ToString()),
            new("Player name", player),
        };
        string? gameDir = null;
        try
        {
            gameDir = _launcher.ProfilePaths.GameDir;
            var status = _launcher.GetStatus();
            facts.Add(new("Pack", $"{status.Installed?.PackVersion ?? "not installed"} installed, {status.Pack?.Lock.Pack.Version ?? "not loaded"} newest ({status.State})"));
            facts.Add(new("VMP client", _launcher.InstalledClient()?.ExactBuild ?? "not installed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or LauncherException)
        {
            facts.Add(new("Pack", $"unknown ({ex.Message})"));
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new LogBundleOptions
        {
            OutputPath = Path.Combine(OutputFolder(), $"VMP-logs-{player}-{now:yyyyMMdd-HHmmss}.zip"),
            GameLogPath = gameDir is null ? null : Path.Combine(gameDir, "KSP.log"),
            PlayerLogPath = string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData", "LocalLow", "Squad", "Kerbal Space Program", "Player.log"),
            LauncherLogsDir = _paths.LogsDir,
            TraceDirs = gameDir is null ? [] : [Path.Combine(gameDir, "GameData", "VladMultiplayer", "Traces"), Path.Combine(gameDir, "GameData", "VladMultiplayer", "logs")],
            ReportText = BuildReport(null),
            UserNames = LogRedactor.CurrentUserNames(),
            Secrets = CollectSecrets(),
            Facts = facts,
            Now = now,
        };
    }

    /// <summary>The Desktop (wherever Windows or OneDrive keeps it), or Documents, or the launcher's own folder when neither exists.</summary>
    private string OutputFolder()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                return path;
            }
        }
        return _paths.DataRoot;
    }

    /// <summary>The arguments that make Explorer open the folder with <paramref name="file"/> selected.</summary>
    public static string ExplorerSelectArguments(string file) => $"/select,\"{file}\"";

    private void RevealInExplorer(string file)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NoRevealVariable)))
        {
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", ExplorerSelectArguments(file)) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not open Explorer on the logs zip");
        }
    }

    /// <summary>Plain-text report for the host: versions, pack state, the error and the end of today's log, with
    /// passwords, tokens and the Windows user name removed.</summary>
    public string BuildReport(UserFacingError? error)
    {
        var sb = new StringBuilder();
        var s = _settings.Current;
        sb.AppendLine($"VMP Launcher report, {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Launcher {AppInfo.Version}, {(s.UpdateChannel == "beta" ? "beta" : "stable")} channel, Windows {Environment.OSVersion.Version}");
        try
        {
            var status = _launcher.GetStatus();
            sb.AppendLine($"Pack: {status.State}{(status.Detail is null ? string.Empty : $" ({status.Detail})")}; installed {status.Installed?.PackVersion ?? "none"}; newest {status.Pack?.Lock.Pack.Version ?? "not loaded"} from {status.Pack?.Source ?? "-"}");
            if (status.Profile is { } profile)
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(profile.Root))!);
                sb.AppendLine($"Pack copy: {profile.GameDir} ({drive.AvailableFreeSpace / (1024 * 1024 * 1024)} GB free)");
            }
            sb.AppendLine($"Your KSP: {s.SourceKspPath ?? "not chosen"}");
            sb.AppendLine($"VMP client: {_launcher.InstalledClient()?.ExactBuild ?? "not installed"}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or LauncherException)
        {
            sb.AppendLine($"(status unavailable: {ex.Message})");
        }
        if (_launcher.Runner.Snapshot is { } running)
        {
            sb.AppendLine($"Running now: {running.Title}: {running.Message}");
        }
        if (_launcher.Runner.LastOutcome is { } last)
        {
            sb.AppendLine($"Last operation: {last.Title}: {(last.Succeeded ? "ok" : last.Cancelled ? "cancelled" : "failed")}, {last.Message}");
        }
        if (error is not null)
        {
            sb.AppendLine().AppendLine($"Error: {error.Title}: {error.Message}");
            if (!string.IsNullOrWhiteSpace(error.Details))
            {
                sb.AppendLine(error.Details);
            }
        }
        sb.AppendLine().AppendLine("Recent log:");
        foreach (var line in RecentLog(80))
        {
            sb.AppendLine(line);
        }
        return ReportRedactor.Redact(sb.ToString(), CollectSecrets());
    }

    /// <summary>The server password and the private-repo tokens this PC holds: never allowed into anything shared.</summary>
    private List<string> CollectSecrets()
    {
        var secrets = new List<string?> { _secrets.Get(_launcher.PasswordKey) };
        var repos = (_launcher.CurrentPack?.Lock.Ckan.Modules.Select(m => m.Artifact?.Private?.Repo).OfType<string>() ?? [])
            .Concat(_settings.Current.PrivateRepos)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos)
        {
            secrets.Add(_secrets.Get(PrivateModsService.TokenKey(repo)));
        }
        return secrets.OfType<string>().Where(s => s.Length > 0).Distinct().ToList();
    }

    private IEnumerable<string> RecentLog(int lines)
    {
        try
        {
            var file = new DirectoryInfo(_paths.LogsDir).EnumerateFiles("launcher-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is null)
            {
                return ["(no log yet)"];
            }
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return all.Skip(Math.Max(0, all.Length - lines)).Select(l => l.TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"(log unreadable: {ex.Message})"];
        }
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
