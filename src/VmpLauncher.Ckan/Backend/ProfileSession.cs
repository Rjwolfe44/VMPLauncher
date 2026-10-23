using System.Security.Cryptography;
using CKAN;
using CKAN.Games.KerbalSpaceProgram;
using CKAN.Configuration;
using CKAN.IO;
using VmpLauncher.Ckan.Locking;
using VmpLauncher.Ckan.Repo;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Profiles;

namespace VmpLauncher.Ckan.Backend;

/// <summary>
/// A CKAN session bound to one profile and one lock: the pack repository is the instance's only repository,
/// the registry manager is held for the session's lifetime, and every call runs on the caller's (worker) thread.
/// Dispose releases CKAN's <c>registry.locked</c>.
/// </summary>
public sealed class ProfileSession : IDisposable
{
    private readonly LauncherPaths _paths;
    private readonly RepositoryDataManager _repoData;
    private bool _disposed;

    public ProfileSession(LauncherPaths paths, ProfilePaths profile, PackLock lockDoc, string cacheDir, VmpCkanUser user)
    {
        _paths = paths;
        Profile = profile;
        Lock = lockDoc;
        User = user;
        Game = new KerbalSpaceProgram();
        Instance = new GameInstance(Game, profile.GameDir, profile.Root.GetHashCode().ToString("x", System.Globalization.CultureInfo.InvariantCulture), user);
        if (!Instance.Valid)
        {
            throw new LauncherException(ErrorCode.GameNotFound, $"The profile's game folder {profile.GameDir} is not a valid KSP install (seed it first).");
        }
        Cache = new NetModuleCache(cacheDir);
        try
        {
            VerifiedIndex = new VerifiedArtifactIndex(Path.Combine(cacheDir, "vmpl-verified.json"));

            // Lock → CKAN modules → pack repository zip
            Modules = lockDoc.Ckan.Modules.Select(RegistryLockBuilder.ToCkanModule).ToList();
            var lockSha8 = Sha256Util.HashUtf8(string.Join("\n", lockDoc.Ckan.Modules.Select(m => m.MetadataSha256)))[..8];
            var repoZip = PackRepoBuilder.Build(Modules, Path.Combine(profile.PackRepoDir, $"{lockSha8}.zip"));
            PackRepo = new Repository($"vmpack-{lockDoc.Pack.Id}", new Uri(repoZip));
            _repoData = new RepositoryDataManager(paths.CkanReposDir);

            RegistryManager = RegistryManager.Instance(Instance, _repoData, new[] { PackRepo });
            RegistryManager.registry.RepositoriesSet(new SortedDictionary<string, Repository> { [PackRepo.name] = PackRepo });
            var downloader = new NetAsyncDownloader(user, () => SHA256.Create(), UserAgent);
            var result = _repoData.Update(new[] { PackRepo }, Game, skipETags: true, downloader, user, UserAgent);
            if (result == RepositoryDataManager.UpdateResult.Failed)
            {
                throw new LauncherException(ErrorCode.InvalidLock, "CKAN could not load the pack's module metadata.");
            }
            var compatible = lockDoc.Game.CompatibleGameVersions.Select(CKAN.Versioning.GameVersion.Parse).ToList();
            if (compatible.Count > 0)
            {
                Instance.SetCompatibleVersions(compatible);
            }
        }
        catch
        {
            // Nobody gets a session to dispose when construction fails: release CKAN's registry.locked here.
            CKAN.RegistryManager.DisposeInstance(Instance);
            Cache.Dispose();
            throw;
        }
    }

    public const string UserAgent = "VmpLauncher";

    public ProfilePaths Profile { get; }
    public PackLock Lock { get; }
    public VmpCkanUser User { get; }
    public KerbalSpaceProgram Game { get; }
    public GameInstance Instance { get; }
    public NetModuleCache Cache { get; }
    public VerifiedArtifactIndex VerifiedIndex { get; }
    public IReadOnlyList<CkanModule> Modules { get; }
    public Repository PackRepo { get; }
    public RegistryManager RegistryManager { get; }
    public Registry Registry => RegistryManager.registry;

    public IReadOnlyList<CkanModule> Packages => Modules.Where(m => !m.IsMetapackage && !m.IsDLC).ToList();

    /// <summary>Test hook: called after each module CKAN finishes installing.</summary>
    public Action<CkanModule>? ModuleCompleted { get; set; }

    public RelationshipResolverOptions ResolverOptions()
    {
        var opts = RelationshipResolverOptions.DependsOnlyOpts(Instance.StabilityToleranceConfig);
        opts.with_recommends = Lock.Ckan.ResolverOptions.WithRecommends;
        opts.with_suggests = Lock.Ckan.ResolverOptions.WithSuggests;
        opts.with_all_suggests = false;
        opts.allow_incompatible = Lock.Ckan.ResolverOptions.AllowIncompatible;
        opts.proceed_with_inconsistencies = false;
        opts.without_toomanyprovides_kraken = false;
        return opts;
    }

    /// <summary>Step 3 of the version chain: the resolver over the full pinned set must return exactly that set.</summary>
    public void AssertExactResolution()
    {
        RelationshipResolver resolver;
        try
        {
            resolver = new RelationshipResolver(Modules, null, ResolverOptions(), Registry, Game, Instance.VersionCriteria());
        }
        catch (DependenciesNotSatisfiedKraken ex)
        {
            throw new LauncherException(ErrorCode.DependencyConflict, "The pack's dependencies cannot be satisfied from its own pinned modules.", RecoveryHint.AskHost, ex, new[] { ex.Message });
        }
        catch (InconsistentKraken ex)
        {
            throw new LauncherException(ErrorCode.DependencyConflict, "The pack contains conflicting mods.", RecoveryHint.AskHost, ex, new[] { ex.Message });
        }
        if (!resolver.IsConsistent)
        {
            throw new LauncherException(ErrorCode.DependencyConflict, "The pack contains conflicting mods.", RecoveryHint.AskHost, details: resolver.ConflictDescriptions.ToList());
        }
        var expected = Lock.Ckan.Modules.ToDictionary(m => m.Identifier, m => m, StringComparer.Ordinal);
        var resolved = resolver.ModList().ToList();
        var problems = new List<string>();
        foreach (var m in resolved)
        {
            if (!expected.TryGetValue(m.identifier, out var lm))
            {
                problems.Add($"resolver added {m.identifier} {m.version} which is not in the lock");
            }
            else if (lm.Version != m.version.ToString() || !Sha256Util.HexEquals(lm.MetadataSha256, JsonCanonicalizer.Sha256(m.ToJson())))
            {
                problems.Add($"resolver picked {m.identifier} {m.version}, lock pins {lm.Version}");
            }
        }
        foreach (var lm in Lock.Ckan.Modules.Where(m => m.Kind != ModuleKindDto.Dlc))
        {
            if (!resolved.Any(m => m.identifier == lm.Identifier))
            {
                problems.Add($"lock pins {lm.Identifier} {lm.Version} but the resolver dropped it");
            }
        }
        if (problems.Count > 0)
        {
            throw new LauncherException(ErrorCode.ResolverMismatch, "The dependency resolver did not reproduce the locked module set.", RecoveryHint.AskHost, details: problems);
        }
    }

    /// <summary>Which archives are already present and verified, and which need downloading or sideloading.</summary>
    public PrefetchPlan PlanPrefetch(CancellationToken ct = default)
    {
        var verified = new List<CkanModule>();
        var toDownload = new List<CkanModule>();
        var sideload = new List<(CkanModule Module, ArtifactRef Artifact)>();
        var corrupt = new List<(CkanModule Module, string Actual, string Expected)>();
        foreach (var lm in Lock.Ckan.Modules.Where(m => m.Kind == ModuleKindDto.Package))
        {
            ct.ThrowIfCancellationRequested();
            var module = Modules.First(m => m.identifier == lm.Identifier);
            var artifact = lm.Artifact ?? throw new LauncherException(ErrorCode.InvalidLock, $"{lm.Identifier} has no artifact in the lock.");
            var cached = Cache.GetCachedFilename(module);
            if (cached is not null)
            {
                var sha = VerifiedIndex.Sha256Of(cached, ct);
                if (Sha256Util.HexEquals(sha, artifact.Sha256))
                {
                    verified.Add(module);
                    continue;
                }
                corrupt.Add((module, sha, artifact.Sha256));
                Cache.Purge(module);
                VerifiedIndex.Forget(cached);
            }
            if (artifact.Distribution == "private-sideload" || artifact.Urls.Count == 0)
            {
                sideload.Add((module, artifact));
            }
            else
            {
                toDownload.Add(module);
            }
        }
        VerifiedIndex.Save();
        return new PrefetchPlan(verified, toDownload, sideload, corrupt);
    }

    /// <summary>Downloads through CKAN (resume, mirrors, hash check on store), then re-verifies with our own hash.</summary>
    public void Download(IReadOnlyList<CkanModule> modules, IProgress<CkanProgress>? progress, CancellationToken ct)
    {
        if (modules.Count == 0)
        {
            return;
        }
        var downloader = new NetAsyncModulesDownloader(User, Cache, UserAgent, ct);
        try
        {
            downloader.DownloadModules(modules);
        }
        catch (ModuleDownloadErrorsKraken ex)
        {
            throw new LauncherException(ErrorCode.ArtifactUnavailable, "Some mod downloads failed.", RecoveryHint.Retry, ex, new[] { ex.Message });
        }
        catch (CancelledActionKraken)
        {
            throw new OperationCanceledException(ct);
        }
        foreach (var m in modules)
        {
            var path = Cache.GetCachedFilename(m) ?? throw new LauncherException(ErrorCode.ArtifactUnavailable, $"{m.identifier} did not end up in the cache after download.", RecoveryHint.Retry);
            var expected = Lock.Ckan.Modules.First(l => l.Identifier == m.identifier).Artifact!.Sha256;
            var actual = VerifiedIndex.Sha256Of(path, ct);
            if (!Sha256Util.HexEquals(actual, expected))
            {
                Cache.Purge(m);
                VerifiedIndex.Forget(path);
                throw new LauncherException(ErrorCode.ArtifactCorrupt, $"The downloaded archive for {m.identifier} does not match the pack's hash.", RecoveryHint.Retry,
                    details: new[] { $"expected {expected}", $"actual {actual}", string.Join(", ", m.download?.Select(u => u.ToString()) ?? Array.Empty<string>()) });
            }
        }
        VerifiedIndex.Save();
    }

    /// <summary>Stores a privately provided archive after proving it matches the lock.</summary>
    public void Sideload(CkanModule module, ArtifactRef artifact, string filePath, CancellationToken ct = default)
    {
        var sha = Sha256Util.HashFile(filePath, ct);
        if (!Sha256Util.HexEquals(sha, artifact.Sha256))
        {
            throw new LauncherException(ErrorCode.ArtifactCorrupt, $"{Path.GetFileName(filePath)} is not the file this pack expects for {module.identifier}.", RecoveryHint.ProvideFile,
                details: new[] { $"expected {artifact.Sha256}", $"actual {sha}" });
        }
        Cache.Store(module, filePath, null, validate: true);
        var stored = Cache.GetCachedFilename(module);
        if (stored is not null)
        {
            VerifiedIndex.Sha256Of(stored, ct);
            VerifiedIndex.Save();
        }
    }

    /// <summary>Installs the given modules (already cached) with CKAN's installer in one CKAN transaction.</summary>
    public void InstallAll(IReadOnlyList<CkanModule> modules, IProgress<CkanProgress>? progress, CancellationToken ct)
    {
        var installer = new ModuleInstaller(Instance, Cache, new JsonConfiguration(_paths.CkanConfigFile), User, ct);
        installer.InstallProgress += (m, done, total) => progress?.Report(new CkanProgress($"Installing {m.name}", total == 0 ? 0 : (int)(done * 100 / total), null));
        installer.OneComplete += m =>
        {
            progress?.Report(new CkanProgress($"Installed {m.name} {m.version}", 100, null));
            ModuleCompleted?.Invoke(m);
        };
        HashSet<string>? configOnlyDirs = null;
        var autoInstalled = new HashSet<CkanModule>(Lock.Ckan.Modules.Where(m => m.AutoInstalled).Select(m => Modules.First(x => x.identifier == m.Identifier)));
        try
        {
            installer.InstallList(modules, ResolverOptions(), RegistryManager, ref configOnlyDirs, deduper: null, userAgent: UserAgent, downloader: null, autoInstalled: autoInstalled, ConfirmPrompt: false);
        }
        catch (CancelledActionKraken)
        {
            throw new OperationCanceledException(ct);
        }
        catch (FileExistsKraken ex)
        {
            throw new LauncherException(ErrorCode.InstallFailed, $"A file already exists where {ex.owningModule?.Module.name ?? "a mod"} wants to install: {ex.filename}", RecoveryHint.Repair, ex);
        }
        catch (Kraken ex)
        {
            throw new LauncherException(ErrorCode.InstallFailed, "CKAN could not complete the installation.", RecoveryHint.Retry, ex, new[] { ex.Message });
        }
    }

    /// <summary>Removes modules by identifier. <paramref name="installing"/> tells CKAN which replacements are coming so reverse dependencies are not cascaded.</summary>
    public void Uninstall(IReadOnlyList<string> identifiers, IReadOnlyList<CkanModule> installing, CancellationToken ct)
    {
        var installer = new ModuleInstaller(Instance, Cache, new JsonConfiguration(_paths.CkanConfigFile), User, ct);
        HashSet<string>? configOnlyDirs = null;
        try
        {
            installer.UninstallList(identifiers, ref configOnlyDirs, RegistryManager, ConfirmPrompt: false, installing: installing);
        }
        catch (CancelledActionKraken)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Kraken ex)
        {
            throw new LauncherException(ErrorCode.InstallFailed, "CKAN could not remove the outdated modules.", RecoveryHint.Retry, ex, new[] { ex.Message });
        }
    }

    /// <summary>
    /// Lock modules (packages and metapackages) the registry does not list, e.g. auto-installed dependencies or reverse
    /// dependencies CKAN removed along with the modules an update uninstalled.
    /// </summary>
    public IReadOnlyList<CkanModule> ModulesMissingFromRegistry()
        => Lock.Ckan.Modules
            .Where(lm => lm.Kind != ModuleKindDto.Dlc && Registry.InstalledModule(lm.Identifier) is null)
            .Select(lm => Modules.First(m => m.identifier == lm.Identifier))
            .ToList();

    /// <summary>
    /// Override targets against what CKAN actually installed: a replace or disable must hit a file its named owner
    /// installed; an add must not hit a file any module installed.
    /// </summary>
    public void AssertOverrideTargets(IEnumerable<LockOverride> overrides)
    {
        var problems = new List<string>();
        foreach (var o in overrides)
        {
            var owner = Registry.FileOwner(o.Path)?.identifier;
            if (o.Op is "replace" or "disable" && !string.Equals(owner, o.Owner, StringComparison.Ordinal))
            {
                problems.Add($"override {o.Id} ({o.Op}) targets {o.Path}, which CKAN installed for {owner ?? "no module"}, not {o.Owner}");
            }
            else if (o.Op == "add" && owner is not null)
            {
                problems.Add($"override {o.Id} adds {o.Path}, which CKAN installed for {owner}");
            }
        }
        if (problems.Count > 0)
        {
            throw new LauncherException(ErrorCode.InvalidLock, "The pack's overrides do not match the files CKAN installed.", RecoveryHint.AskHost, details: problems);
        }
    }

    public void Rescan()
    {
        RegistryManager.ScanUnmanagedFiles();
        RegistryManager.ScanDlc();
        RegistryManager.Save();
    }

    /// <summary>Post-apply checks of the registry against the lock (chain step 5, registry half).</summary>
    public IReadOnlyList<string> VerifyRegistryAgainstLock(IReadOnlyCollection<string> inventoryOwnedPaths)
    {
        var problems = new List<string>();
        var installed = Registry.InstalledModules.Where(m => !m.Module.IsDLC).ToDictionary(m => m.identifier, m => m, StringComparer.Ordinal);
        foreach (var lm in Lock.Ckan.Modules.Where(m => m.Kind != ModuleKindDto.Dlc))
        {
            if (!installed.TryGetValue(lm.Identifier, out var im))
            {
                problems.Add($"{lm.Identifier} {lm.Version} is not installed according to the registry");
            }
            else if (im.Module.version.ToString() != lm.Version)
            {
                problems.Add($"{lm.Identifier}: registry has {im.Module.version}, lock pins {lm.Version}");
            }
        }
        foreach (var im in installed.Values)
        {
            if (!Lock.Ckan.Modules.Any(l => l.Identifier == im.identifier))
            {
                problems.Add($"registry has extra module {im.identifier} {im.Module.version}");
            }
        }
        var registryFiles = Registry.InstalledFileInfo()
            .Select(r => r.relPath)
            .Where(p => !Directory.Exists(Instance.ToAbsoluteGameDir(p)))
            .ToHashSet(Platform.PathComparer);
        var inv = inventoryOwnedPaths.ToHashSet(Platform.PathComparer);
        var onlyRegistry = registryFiles.Except(inv, Platform.PathComparer).Take(10).ToList();
        var onlyInventory = inv.Except(registryFiles, Platform.PathComparer).Take(10).ToList();
        if (onlyRegistry.Count > 0 || onlyInventory.Count > 0)
        {
            problems.Add($"registry file list differs from inventory: {registryFiles.Count} vs {inv.Count}; registry-only e.g. {string.Join(", ", onlyRegistry)}; inventory-only e.g. {string.Join(", ", onlyInventory)}");
        }
        return problems;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        RegistryManager.DisposeInstance(Instance);
        Cache.Dispose();
    }
}

public sealed record PrefetchPlan(
    IReadOnlyList<CkanModule> Verified,
    IReadOnlyList<CkanModule> ToDownload,
    IReadOnlyList<(CkanModule Module, ArtifactRef Artifact)> Sideload,
    IReadOnlyList<(CkanModule Module, string Actual, string Expected)> Corrupt)
{
    public long BytesToDownload => ToDownload.Sum(m => m.download_size);
}
