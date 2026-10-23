using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Core.Transactions;
using VmpLauncher.Game;

namespace VmpLauncher.App.Services;

public enum ProfileState
{
    NoKsp,
    NotInstalled,
    Installed,
    UpdateAvailable,
    Modified,
    Interrupted,
}

public sealed record ProfileStatus(ProfileState State, ProfilePaths? Profile, InstalledState? Installed, ResolvedPack? Pack, int ModifiedFiles, string? Detail);

/// <summary>
/// Facade the ViewModels talk to. Owns the CKAN worker thread, the active profile and the current pack, and turns
/// Core operations into UI-safe tasks with progress through <see cref="OperationRunner"/>.
/// </summary>
public sealed class LauncherService : IDisposable
{
    private readonly LauncherPaths _paths;
    private readonly AppSettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly PackCatalogService _catalog;
    private readonly PrivateModsService _private;
    private readonly OperationRunner _runner;
    private readonly ILogger<LauncherService> _log;
    private readonly VmpLauncher.Ckan.Backend.CkanWorker _worker = new();

    public LauncherService(LauncherPaths paths, AppSettingsStore settings, ISecretStore secrets, PackCatalogService catalog, PrivateModsService privateMods, OperationRunner runner, ILogger<LauncherService> log)
    {
        _paths = paths;
        _settings = settings;
        _secrets = secrets;
        _catalog = catalog;
        _private = privateMods;
        _runner = runner;
        _log = log;
    }

    public ResolvedPack? CurrentPack { get; private set; }

    /// <summary>Raised on the UI thread after the pack was refreshed or an operation changed the profile.</summary>
    public event Action? StateChanged;

    private InstallVerifier.Result? _lastVerify;
    private string? _lastVerifyLock;

    /// <summary>
    /// Quick integrity check of the installed profile (size/time/file id, hashing only what looks changed). A profile
    /// that fails it is reported as Modified so Home offers Repair instead of Play.
    /// </summary>
    public async Task RefreshIntegrityAsync(CancellationToken ct)
    {
        if (_runner.IsBusy)
        {
            return;
        }
        try
        {
            var installedLock = InstalledState.TryRead(ProfilePaths)?.LockSha256;
            var r = await QuickVerifyAsync(ct).ConfigureAwait(false);
            _lastVerify = r;
            _lastVerifyLock = installedLock;
            if (r is { IsClean: false })
            {
                _log.LogWarning("Quick verify: {Missing} missing, {Modified} modified, {Dlls} unknown DLLs", r.Missing.Count, r.Modified.Count, r.UnknownDlls.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or LauncherException)
        {
            _log.LogWarning(ex, "Quick verify failed");
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess())
        {
            StateChanged?.Invoke();
        }
        else
        {
            d.BeginInvoke(() => StateChanged?.Invoke());
        }
    }
    public OperationRunner Runner => _runner;
    public LauncherPaths Paths => _paths;

    public string PackId => _settings.Current.ActiveProfileId;

    public ProfilePaths ProfilePaths
    {
        get
        {
            var s = _settings.Current;
            return s.ProfileRoot is { } root ? new ProfilePaths(root) : new ProfileStore(_paths).Get(s.ActiveProfileId);
        }
    }

    public string CacheDir => _settings.Current.CacheDirOverride ?? _paths.CacheDir;

    public string PasswordKey => $"server-password:{PackId}";

    public async Task<ResolvedPack?> RefreshPackAsync(CancellationToken ct)
    {
        CurrentPack = await _catalog.ResolveAsync(PackId, ct).ConfigureAwait(false);
        RaiseStateChanged();
        return CurrentPack;
    }

    public ProfileStatus GetStatus()
    {
        var s = _settings.Current;
        if (s.SourceKspPath is null || !File.Exists(Path.Combine(s.SourceKspPath, "KSP_x64.exe")))
        {
            return new ProfileStatus(ProfileState.NoKsp, null, null, CurrentPack, 0, "Choose your KSP folder first.");
        }
        var profile = ProfilePaths;
        if (!profile.Exists)
        {
            return new ProfileStatus(ProfileState.NotInstalled, profile, null, CurrentPack, 0, null);
        }
        if (TransactionJournal.FindIncomplete(profile) is { } j)
        {
            return new ProfileStatus(ProfileState.Interrupted, profile, InstalledState.TryRead(profile), CurrentPack, 0, $"A previous {j.Operation} was interrupted.");
        }
        var installed = InstalledState.TryRead(profile);
        if (installed is null)
        {
            return new ProfileStatus(ProfileState.NotInstalled, profile, null, CurrentPack, 0, null);
        }
        if (CurrentPack is not null && !Core.Hashing.Sha256Util.HexEquals(CurrentPack.LockSha256, installed.LockSha256) && CurrentPack.Lock.Pack.Version != installed.PackVersion)
        {
            return new ProfileStatus(ProfileState.UpdateAvailable, profile, installed, CurrentPack, 0, $"{installed.PackVersion} → {CurrentPack.Lock.Pack.Version}");
        }
        if (_lastVerify is { IsClean: false } v && _lastVerifyLock is not null && Core.Hashing.Sha256Util.HexEquals(_lastVerifyLock, installed.LockSha256))
        {
            return new ProfileStatus(ProfileState.Modified, profile, installed, CurrentPack, v.Missing.Count + v.Modified.Count + v.UnknownDlls.Count, null);
        }
        return new ProfileStatus(ProfileState.Installed, profile, installed, CurrentPack, 0, null);
    }

    /// <summary>Quick verification (size/mtime/file id), hashing only files that look changed. Seconds, not minutes.</summary>
    public async Task<InstallVerifier.Result?> QuickVerifyAsync(CancellationToken ct)
    {
        var profile = ProfilePaths;
        var state = InstalledState.TryRead(profile);
        if (state is null || !File.Exists(profile.LockCopyFile))
        {
            return null;
        }
        return await Task.Run(() =>
        {
            var lockDoc = LauncherJson.ReadFile<PackLock>(profile.LockCopyFile);
            var inventory = InventoryFile.Read(profile.InventoryCopyFile, lockDoc.Inventory.Sha256);
            return new InstallVerifier().Verify(profile, inventory, lockDoc.FileRules, InstallVerifier.Mode.Quick, state, ct);
        }, ct).ConfigureAwait(false);
    }

    public PrivateModsService PrivateMods => _private;

    public async Task<ProfileOperations.Report> InstallOrUpdateAsync(CancellationToken outer)
    {
        var pack = CurrentPack ?? throw new LauncherException(ErrorCode.InvalidPack, "No pack is loaded.");
        var status = GetStatus();
        var isUpdate = status.State is ProfileState.UpdateAvailable or ProfileState.Installed or ProfileState.Modified;
        try
        {
            return await _runner.RunAsync(isUpdate ? OperationKind.Update : OperationKind.Install, isUpdate ? $"Updating to {pack.Lock.Pack.Version}" : $"Installing {pack.Lock.Pack.Name}", async (progress, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, outer);
                var token = linked.Token;
                // Private packages first: nothing in the profile changes until every file is verified locally.
                var sideloads = await _private.PrepareAsync(pack.Lock, isUpdate ? InstalledLock() : null, progress, token).ConfigureAwait(false);
                return await _worker.RunAsync(() =>
                {
                    var profile = EnsureProfileSeeded(pack, progress);
                    var ops = new ProfileOperations(_paths, new ProgressWriter(progress), progress);
                    var request = new ProfileOperations.Request(profile, new ProfileOperations.LockSource(pack.LockPath, pack.InventoryPath), CacheDir, Offline: false, sideloads);
                    return isUpdate ? ops.Update(request, token) : ops.Install(request, token);
                }, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            _lastVerify = null;
            RaiseStateChanged();
        }
    }

    public async Task<ProfileOperations.RepairReport> RepairAsync(bool quarantineUnknownDlls, CancellationToken outer)
    {
        try
        {
            return await _runner.RunAsync(OperationKind.Repair, "Repairing", async (progress, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, outer);
                var token = linked.Token;
                // Repair restores from the installed lock; its private archives come from the private cache or the invite.
                var installed = InstalledLock();
                var sideloads = installed is null ? null : await _private.PrepareAsync(installed, null, progress, token).ConfigureAwait(false);
                return await _worker.RunAsync(() => new ProfileOperations(_paths, new ProgressWriter(progress), progress).Repair(ProfilePaths, CacheDir, quarantineUnknownDlls, token, sideloads), token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            _lastVerify = null;
            RaiseStateChanged();
        }
    }

    public async Task<RollbackEngine.Result?> RecoverAsync(CancellationToken outer)
    {
        try
        {
            return await _runner.RunAsync(OperationKind.Recover, "Restoring previous state", async (progress, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, outer);
                var token = linked.Token;
                return await _worker.RunAsync(() => new ProfileOperations(_paths, new ProgressWriter(progress), progress).Recover(ProfilePaths, token), token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            _lastVerify = null;
            RaiseStateChanged();
        }
    }

    private ProfilePaths EnsureProfileSeeded(ResolvedPack pack, IProgress<VmpLauncher.Ckan.Users.CkanProgress> progress)
    {
        var profile = ProfilePaths;
        if (profile.Exists)
        {
            return profile;
        }
        var source = _settings.Current.SourceKspPath ?? throw new LauncherException(ErrorCode.GameNotFound, "Choose your KSP folder first.", RecoveryHint.ChooseAnotherFolder);
        var game = pack.Lock.Game;
        var buildId = ProfileSeeder.ValidateSource(source, game.BuildId64, game.RequiredDlc.Select(d => (d.Folder, d.PolicyName)));
        ProfilePathValidator.Validate(profile.Root, pack.Lock.Inventory.MaxRelPathLength, source, CacheDir);
        var hardLink = ProfilePathValidator.SameVolume(source, profile.Root);
        var inventory = InventoryFile.Read(pack.InventoryPath, pack.Lock.Inventory.Sha256);
        progress.Report(new VmpLauncher.Ckan.Users.CkanProgress(hardLink ? "Linking your KSP files into the profile…" : "Copying your KSP files into the profile…", 0, null));
        profile.EnsureCreated();
        var result = new ProfileSeeder().Seed(new ProfileSeeder.Plan(source, profile.GameDir, hardLink, inventory.Files.Select(f => f.Path).ToList()));
        profile.WriteManifest(new ProfileManifest
        {
            Id = _settings.Current.ActiveProfileId,
            Name = pack.Lock.Pack.Name,
            PackId = pack.Lock.Pack.Id,
            Seed = new SeedInfo { FromPath = source, BuildId64 = buildId, Mode = hardLink ? "hardlink" : "copy", BytesLinked = result.BytesLinked, BytesCopied = result.BytesCopied, FilesLinked = result.FilesLinked, FilesCopied = result.FilesCopied },
            CacheDir = _settings.Current.CacheDirOverride,
        });
        if (_settings.Current.ProfileRoot is { } root)
        {
            new ProfileStore(_paths).Register(_settings.Current.ActiveProfileId, root);
        }
        _log.LogInformation("Seeded profile {Root}: {Linked} linked, {Copied} copied", profile.Root, result.FilesLinked, result.FilesCopied);
        return profile;
    }

    /// <summary>Writes the player's VMP settings (name, routes, password) into the profile and starts KSP.</summary>
    public Process Launch(string? routeEntryName, IEnumerable<VmpSettingsFile.ServerEntry> routes)
    {
        var profile = ProfilePaths;
        if (OperatingSystem.IsWindows())
        {
            GameProcessGuard.ThrowIfRunning(profile.GameDir);
        }
        var s = _settings.Current;
        var settingsPath = VmpSettingsFile.PathFor(profile.GameDir);
        var doc = VmpSettingsFile.LoadOrCreate(settingsPath);
        if (!string.IsNullOrWhiteSpace(s.PlayerName))
        {
            VmpSettingsFile.SetPlayerName(doc, s.PlayerName);
        }
        if (s.DisclaimerAccepted)
        {
            VmpSettingsFile.SetDisclaimerAccepted(doc, true);
        }
        VmpSettingsFile.SetMtu(doc, 1200);
        var password = _secrets.Get(PasswordKey);
        foreach (var r in routes)
        {
            VmpSettingsFile.UpsertServer(doc, r with { Password = password });
        }
        VmpSettingsFile.Save(doc, settingsPath);
        var client = LmpClientInspector.Inspect(profile.GameDir);
        // -vmpjoin is always passed (older clients ignore it); the log says whether this client will act on it.
        _log.LogInformation("Launching KSP from {GameDir}; -vmpjoin {Route} (client {Build}, auto-join {Supported})", profile.GameDir, routeEntryName ?? "(none)", client?.ExactBuild, client?.SupportsAutoJoin == true ? "supported" : "not supported");
        return KspLauncher.Launch(new KspLauncher.LaunchSpec(profile.GameDir, routeEntryName, s.BorderlessWindow, Array.Empty<string>()));
    }

    private (string Path, DateTime Stamp, PackLock Lock)? _installedLockCache;

    /// <summary>The lock the profile was last installed from (its private copy), or null.</summary>
    public PackLock? InstalledLock()
    {
        try
        {
            var file = ProfilePaths.LockCopyFile;
            if (!File.Exists(file))
            {
                return null;
            }
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_installedLockCache is { } c && c.Path == file && c.Stamp == stamp)
            {
                return c.Lock;
            }
            var lk = LauncherJson.ReadFile<PackLock>(file);
            _installedLockCache = (file, stamp, lk);
            return lk;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public LmpClientInfo? InstalledClient() => ProfilePaths.Exists ? LmpClientInspector.Inspect(ProfilePaths.GameDir) : null;

    private readonly Dictionary<string, (long Size, long Mtime, string DashHex)> _dllHashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pre-flight: compare the profile's DLLs with the server policy shipped in the pack export (KSPModControl.xml next to the lock).</summary>
    public Task<Core.Policy.ModControlChecker.Result?> PolicyCheckAsync(CancellationToken ct)
    {
        var pack = CurrentPack;
        var profile = ProfilePaths;
        if (pack is null || !profile.Exists)
        {
            return Task.FromResult<Core.Policy.ModControlChecker.Result?>(null);
        }
        var policyFile = Path.Combine(Path.GetDirectoryName(pack.LockPath)!, "KSPModControl.xml");
        if (!File.Exists(policyFile))
        {
            return Task.FromResult<Core.Policy.ModControlChecker.Result?>(null);
        }
        return Task.Run(() =>
        {
            var policy = Core.Policy.ModControlPolicy.Parse(policyFile);
            lock (_dllHashCache)
            {
                return (Core.Policy.ModControlChecker.Result?)Core.Policy.ModControlChecker.Check(profile.GameDir, policy, _dllHashCache, ct);
            }
        }, ct);
    }

    public void Dispose() => _worker.Dispose();

    /// <summary>Routes CKAN/Core log lines into the operation's recent-lines list.</summary>
    private sealed class ProgressWriter(IProgress<VmpLauncher.Ckan.Users.CkanProgress> progress) : TextWriter
    {
        private readonly System.Text.StringBuilder _line = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value)
        {
            if (value == '\n')
            {
                var text = _line.ToString().TrimEnd('\r');
                _line.Clear();
                if (text.Length > 0)
                {
                    progress.Report(new VmpLauncher.Ckan.Users.CkanProgress(text, -1, null));
                }
            }
            else
            {
                _line.Append(value);
            }
        }
    }
}
