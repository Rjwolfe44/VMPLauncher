using System.Security.Cryptography;
using CKAN;
using CKAN.Configuration;
using CKAN.Games.KerbalSpaceProgram;
using CKAN.Versioning;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Ckan.Catalog;

/// <summary>
/// Which KSP folder a catalog is built for. Pack setups pass the pack's pins and are read-only. <paramref name="ProfileRoot"/>
/// is the launcher's state folder for the setup; changes hold its profile lock.
/// </summary>
public sealed record CatalogTarget(string GameDir, string Name, IReadOnlyDictionary<string, string>? PackPins = null, string? PackGameVersion = null, string? ProfileRoot = null);

public sealed record CatalogUpdateOutcome(bool Changed, int Mods, int NewMods, TimeSpan Elapsed);

/// <summary>
/// The full CKAN catalog (KSP-CKAN/CKAN-meta), kept in the launcher's own repository cache so the player's CKAN data
/// is never touched. All members must run on the CKAN worker thread.
/// </summary>
public sealed partial class CatalogService : IDisposable
{
    private sealed class State
    {
        public DateTimeOffset? UpdatedUtc { get; set; }
        public List<string> Known { get; set; } = new();
        public List<string> New { get; set; } = new();
    }

    private readonly LauncherPaths _paths;
    private readonly KerbalSpaceProgram _game = new();
    private readonly RepositoryDataManager _repoData;
    private readonly NetModuleCache _cache;
    private readonly VmpCkanUser _user = new();
    private readonly string _stateFile;
    private State _state;
    private HashSet<string> _new;
    private bool _loaded;

    /// <param name="repository">Tests: a local repository instead of KSP-CKAN/CKAN-meta.</param>
    /// <param name="reposDir">Tests: where parsed repository data and the catalog state live.</param>
    public CatalogService(LauncherPaths paths, string cacheDir, Repository? repository = null, string? reposDir = null)
    {
        _paths = paths;
        _repoData = new RepositoryDataManager(reposDir ?? paths.CkanReposDir);
        _cache = new NetModuleCache(cacheDir);
        DefaultRepo = repository ?? Repository.DefaultGameRepo(_game);
        _stateFile = Path.Combine(reposDir ?? paths.CkanDir, "catalog-state.json");
        _state = ReadState();
        _new = _state.New.ToHashSet(StringComparer.Ordinal);
    }

    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(3);

    public Repository DefaultRepo { get; }

    public DateTimeOffset? UpdatedUtc => _state.UpdatedUtc;

    public bool IsStale => _state.UpdatedUtc is not { } u || DateTimeOffset.UtcNow - u > StaleAfter;

    /// <summary>True once catalog data is in memory (from disk or a download).</summary>
    public bool HasData => _loaded && _repoData.GetAllAvailableModules(new[] { DefaultRepo }).Any();

    private State ReadState()
    {
        try
        {
            return File.Exists(_stateFile) ? LauncherJson.ReadFile<State>(_stateFile) : new State();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new State();
        }
    }

    /// <summary>Loads the cached catalog from disk (no network). Cheap after the first call.</summary>
    public void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        _repoData.Prepopulate(new[] { DefaultRepo }, null);
        _loaded = true;
    }

    /// <summary>Downloads the catalog when it changed upstream (ETag) or when <paramref name="force"/> is set.</summary>
    public CatalogUpdateOutcome Update(bool force, IProgress<CkanProgress>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EnsureLoaded();
        var user = new VmpCkanUser(progress: progress);
        var downloader = new NetAsyncDownloader(user, () => SHA256.Create(), Backend.ProfileSession.UserAgent, ct);
        RepositoryDataManager.UpdateResult result;
        try
        {
            result = _repoData.Update(new[] { DefaultRepo }, _game, skipETags: force, downloader, user, Backend.ProfileSession.UserAgent);
        }
        catch (DownloadErrorsKraken ex)
        {
            throw new LauncherException(ErrorCode.NetworkUnavailable, "Could not download the CKAN mod list.", RecoveryHint.Retry, ex, new[] { ex.Message });
        }
        catch (CancelledActionKraken)
        {
            throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();
        if (result == RepositoryDataManager.UpdateResult.Failed)
        {
            throw new LauncherException(ErrorCode.NetworkUnavailable, "Could not download the CKAN mod list.", RecoveryHint.Retry, details: user.Errors);
        }
        var idents = AllIdentifiers();
        var newMods = 0;
        if (result == RepositoryDataManager.UpdateResult.Updated || _state.Known.Count == 0)
        {
            // "New" means new since the previous refresh; the very first download marks nothing as new.
            var known = _state.Known.ToHashSet(StringComparer.Ordinal);
            var added = known.Count == 0 ? new List<string>() : idents.Where(i => !known.Contains(i)).ToList();
            if (added.Count > 0)
            {
                _state.New = added;
                _new = added.ToHashSet(StringComparer.Ordinal);
            }
            newMods = added.Count;
            _state.Known = idents.ToList();
        }
        _state.UpdatedUtc = DateTimeOffset.UtcNow;
        LauncherJson.WriteFile(_stateFile, _state);
        return new CatalogUpdateOutcome(result == RepositoryDataManager.UpdateResult.Updated, idents.Count, newMods, sw.Elapsed);
    }

    private SortedSet<string> AllIdentifiers()
        => new(_repoData.GetAllAvailableModules(new[] { DefaultRepo })
                        .Select(am => am.AllAvailable().FirstOrDefault()?.identifier)
                        .OfType<string>(),
               StringComparer.Ordinal);

    private sealed record Context(Registry Registry, GameInstance? Instance, GameVersionCriteria Criteria, StabilityToleranceConfig Stability, bool ReadOnly, string? ReadOnlyReason)
    {
        public IReadOnlyCollection<Repository> Repos => Registry.Repositories.Values.ToList();
    }

    private Context Open(CatalogTarget target)
    {
        EnsureLoaded();
        if (target.PackPins is not null)
        {
            // Pack setups: the pack's own registry only knows the pack's repository, so browse the full catalog against
            // an empty registry and take "installed" from the pack's pins.
            var inst = TryInstance(target);
            var crit = inst?.VersionCriteria()
                       ?? new GameVersionCriteria(GameVersion.TryParse(target.PackGameVersion, out var v) ? v : GameVersion.Parse("1.12.5"));
            var stab = inst?.StabilityToleranceConfig ?? new StabilityToleranceConfig(Path.Combine(_paths.CkanDir, "stability_tolerance.json"));
            return new Context(new Registry(_repoData, DefaultRepo), inst, crit, stab, true, "This setup follows a pack, so its mods match the server. Make a custom copy to change them.");
        }
        var instance = TryInstance(target)
                       ?? throw new LauncherException(ErrorCode.GameNotFound, $"{target.GameDir} is not a KSP folder the launcher can read.", RecoveryHint.ChooseAnotherFolder);
        var locked = instance.IsMaybeLocked;
        // A folder CKAN holds open before it ever saved a registry has no mod list yet: browse it as empty.
        var registry = RegistryManager.ReadOnlyRegistry(instance, _repoData)
                       ?? (locked ? new Registry(_repoData, DefaultRepo) : CreateRegistry(instance));
        if (registry.Repositories.Count == 0)
        {
            registry.RepositoriesSet(new SortedDictionary<string, Repository> { [DefaultRepo.name] = DefaultRepo });
        }
        return new Context(registry, instance, instance.VersionCriteria(), instance.StabilityToleranceConfig, locked,
            locked ? "CKAN has this KSP folder open. Close CKAN to change its mods here." : null);
    }

    private GameInstance? TryInstance(CatalogTarget target)
    {
        if (!File.Exists(Path.Combine(target.GameDir, "KSP_x64.exe")))
        {
            return null;
        }
        var inst = new GameInstance(_game, target.GameDir, target.Name, _user);
        return inst.Valid ? inst : null;
    }

    private Registry CreateRegistry(GameInstance instance)
    {
        // A fresh copy has no CKAN registry yet: create one (scanning the DLLs already there) and release the lock.
        try
        {
            var mgr = RegistryManager.Instance(instance, _repoData, new[] { DefaultRepo });
            mgr.Save(enforce_consistency: false);
            return mgr.registry;
        }
        catch (RegistryInUseKraken)
        {
            return new Registry(_repoData, DefaultRepo); // CKAN opened it just now
        }
        finally
        {
            RegistryManager.DisposeInstance(instance);
        }
    }

    public CatalogSnapshot Build(CatalogTarget target)
    {
        var ctx = Open(target);
        var registry = ctx.Registry;
        var installed = registry.InstalledModules.Where(im => !im.Module.IsDLC).ToDictionary(im => im.identifier, StringComparer.Ordinal);
        var dlls = registry.InstalledDlls.ToHashSet(StringComparer.Ordinal);
        var shown = new Dictionary<string, (CkanModule Module, bool Compatible)>(StringComparer.Ordinal);
        foreach (var m in registry.CompatibleModules(ctx.Stability, ctx.Criteria))
        {
            if (!m.IsDLC)
            {
                shown[m.identifier] = (m, true);
            }
        }
        foreach (var m in registry.IncompatibleModules(ctx.Stability, ctx.Criteria))
        {
            if (!m.IsDLC)
            {
                shown.TryAdd(m.identifier, (m, false));
            }
        }
        foreach (var im in installed.Values)
        {
            shown.TryAdd(im.identifier, (im.Module, im.Module.IsCompatible(ctx.Criteria)));
        }

        var entries = new List<CatalogEntry>(shown.Count);
        foreach (var (ident, (module, compatible)) in shown)
        {
            entries.Add(ToEntry(ctx, module, compatible, installed.GetValueOrDefault(ident), dlls.Contains(ident), target.PackPins));
        }
        var tags = entries.SelectMany(e => e.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new CatalogSnapshot(entries, tags, ShortVersion(ctx.Instance?.Version()?.ToString() ?? target.PackGameVersion ?? "1.12.5"), _state.UpdatedUtc, ctx.ReadOnly, ctx.ReadOnlyReason);
    }

    /// <summary>"1.12.5.3190" → "1.12.5": players know their KSP by version, not build.</summary>
    private static string ShortVersion(string v)
    {
        var parts = v.Split('.');
        return parts.Length > 3 ? string.Join('.', parts.Take(3)) : v;
    }

    private CatalogEntry ToEntry(Context ctx, CkanModule module, bool compatible, InstalledModule? installed, bool autodetected, IReadOnlyDictionary<string, string>? pins)
    {
        var ident = module.identifier;
        string? installedVersion = installed?.Module.version.ToString();
        var hasUpdate = false;
        var inPack = false;
        if (pins is not null)
        {
            inPack = pins.TryGetValue(ident, out installedVersion);
        }
        else if (installed is not null && ctx.Instance is not null)
        {
            try
            {
                hasUpdate = ctx.Registry.HasUpdate(ident, ctx.Stability, ctx.Instance, new HashSet<string>(), false, out _);
            }
            catch (Kraken)
            {
                hasUpdate = false;
            }
        }
        var res = module.resources;
        return new CatalogEntry
        {
            Identifier = ident,
            Name = string.IsNullOrWhiteSpace(module.name) ? ident : module.name.Trim(),
            Abstract = OneLine(module.@abstract),
            Authors = string.Join(", ", module.author ?? new List<string>()),
            Tags = module.Tags?.Order(StringComparer.OrdinalIgnoreCase).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Downloads = _repoData.GetDownloadCount(ctx.Repos, ident) ?? 0,
            DownloadSize = module.download_size,
            InstallSize = module.install_size,
            ReleaseDate = module.release_date,
            LatestVersion = module.version.ToString(),
            InstalledVersion = installedVersion,
            IsInstalled = installedVersion is not null,
            IsAutoInstalled = installed?.AutoInstalled ?? false,
            IsAutodetected = autodetected && installed is null,
            HasUpdate = hasUpdate,
            IsCompatible = compatible,
            GameVersions = SafeVersions(module),
            IsCached = module.download is { Count: > 0 } && _cache.IsMaybeCachedZip(module),
            Kind = module.kind.ToString(),
            License = module.license is { Count: > 0 } l ? string.Join(", ", l.Select(x => x.ToString())) : null,
            SpaceDock = res?.spacedock,
            Homepage = res?.homepage,
            Repository = res?.repository,
            Forum = res?.discussions,
            BugTracker = res?.bugtracker,
            IsInPack = inPack,
            IsNew = _new.Contains(ident),
        };
    }

    private string SafeVersions(CkanModule module)
    {
        try
        {
            return module.CompatibleGameVersions(_game);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    private static string OneLine(string? s) => string.IsNullOrWhiteSpace(s) ? string.Empty : System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    public ModDetails Details(CatalogTarget target, string identifier)
    {
        var ctx = Open(target);
        var registry = ctx.Registry;
        List<CkanModule> versions;
        try
        {
            versions = registry.AvailableByIdentifier(identifier).ToList();
        }
        catch (ModuleNotFoundKraken)
        {
            versions = new List<CkanModule>();
        }
        var installed = registry.InstalledModule(identifier);
        var module = versions.FirstOrDefault(m => m.IsCompatible(ctx.Criteria)) ?? versions.FirstOrDefault() ?? installed?.Module
                     ?? throw new LauncherException(ErrorCode.InvalidPack, $"{identifier} is not in the CKAN catalog.");
        var entry = ToEntry(ctx, module, module.IsCompatible(ctx.Criteria), installed, registry.InstalledDlls.Contains(identifier), target.PackPins);
        var installedVersion = entry.InstalledVersion;
        var rows = versions.Select(m => new ModVersionRow(
                m.version.ToString(),
                SafeVersions(m),
                m.release_date,
                m.download_size,
                m.IsCompatible(ctx.Criteria),
                m.version.ToString() == installedVersion,
                (m.release_status ?? ReleaseStatus.stable).ToString()))
            .ToList();
        var installedIdents = target.PackPins is { } pins
            ? pins.Keys.ToHashSet(StringComparer.Ordinal)
            : registry.InstalledModules.Select(im => im.identifier).Concat(registry.InstalledDlls).ToHashSet(StringComparer.Ordinal);
        var relations = new List<RelationRow>();
        void Add(string kind, List<RelationshipDescriptor>? list)
        {
            foreach (var r in list ?? new List<RelationshipDescriptor>())
            {
                var name = r is ModuleRelationshipDescriptor mr ? mr.name : null;
                if (kind == "conflicts" && name == module.identifier)
                {
                    continue; // CKAN's "only one version at a time" idiom, not a real conflict
                }
                var satisfied = r is ModuleRelationshipDescriptor m1
                    ? installedIdents.Contains(m1.name)
                    : r is AnyOfRelationshipDescriptor any && (any.any_of ?? new List<RelationshipDescriptor>()).OfType<ModuleRelationshipDescriptor>().Any(x => installedIdents.Contains(x.name));
                relations.Add(new RelationRow(kind, r.ToString() ?? string.Empty, name, satisfied));
            }
        }
        Add("depends", module.depends);
        Add("recommends", module.recommends);
        Add("suggests", module.suggests);
        Add("supports", module.supports);
        Add("conflicts", module.conflicts);
        return new ModDetails(entry, module.description, rows, relations);
    }

    public void Dispose() => _cache.Dispose();
}
