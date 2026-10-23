using Microsoft.Extensions.Logging;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Ckan.Catalog;
using VmpLauncher.Core;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.Locking;

namespace VmpLauncher.App.Services;

/// <summary>
/// The general mod manager: the full CKAN catalog for any setup, mod details, and applying changes. The catalog is
/// only loaded the first time the Mods page needs it (it is several hundred MB in memory), and every CKAN call runs on
/// the shared CKAN thread.
/// </summary>
public sealed class ModManagerService
{
    private readonly LauncherPaths _paths;
    private readonly CkanWorker _worker;
    private readonly LauncherService _launcher;
    private readonly OperationRunner _runner;
    private readonly ILogger<ModManagerService> _log;
    private CatalogService? _catalog;
    private Task<CatalogUpdateOutcome>? _updating;

    public ModManagerService(LauncherPaths paths, CkanWorker worker, LauncherService launcher, OperationRunner runner, ILogger<ModManagerService> log)
    {
        _paths = paths;
        _worker = worker;
        _launcher = launcher;
        _runner = runner;
        _log = log;
    }

    /// <summary>Raised (on any thread) after the catalog was refreshed or a setup's mods changed.</summary>
    public event Action? Changed;

    public DateTimeOffset? CatalogUpdatedUtc { get; private set; }

    public bool IsUpdatingCatalog => _updating is { IsCompleted: false };

    private Task<T> OnCatalog<T>(Func<CatalogService, T> work, CancellationToken ct)
        => _worker.RunAsync(() =>
        {
            _catalog ??= new CatalogService(_paths, _launcher.CacheDir);
            var result = work(_catalog);
            CatalogUpdatedUtc = _catalog.UpdatedUtc;
            return result;
        }, ct);

    /// <summary>Downloads the catalog when it changed upstream; concurrent callers share one download.</summary>
    public Task<CatalogUpdateOutcome> UpdateCatalogAsync(bool force, IProgress<Ckan.Users.CkanProgress>? progress, CancellationToken ct)
    {
        if (_updating is { IsCompleted: false } running)
        {
            return running;
        }
        _updating = Run();
        return _updating;

        async Task<CatalogUpdateOutcome> Run()
        {
            var outcome = await OnCatalog(c => c.Update(force, progress, ct), ct).ConfigureAwait(false);
            _log.LogInformation("Catalog refresh: {Changed}, {Mods} mods, {New} new, {Seconds:F1} s", outcome.Changed ? "updated" : "unchanged", outcome.Mods, outcome.NewMods, outcome.Elapsed.TotalSeconds);
            Changed?.Invoke();
            return outcome;
        }
    }

    /// <summary>Loads the catalog from disk, downloading it the first time; tells the caller whether it is stale.</summary>
    public async Task<(bool HasData, bool Stale)> EnsureCatalogAsync(IProgress<Ckan.Users.CkanProgress>? progress, CancellationToken ct)
    {
        var (has, stale) = await OnCatalog(c =>
        {
            c.EnsureLoaded();
            return (c.HasData, c.IsStale);
        }, ct).ConfigureAwait(false);
        if (!has)
        {
            await UpdateCatalogAsync(false, progress, ct).ConfigureAwait(false);
            return (true, false);
        }
        return (true, stale);
    }

    public CatalogTarget TargetFor(SetupInfo setup)
    {
        if (!setup.IsPack)
        {
            return new CatalogTarget(setup.GameDir, setup.Name, ProfileRoot: setup.Root);
        }
        var lockDoc = _launcher.InstalledLock() ?? _launcher.CurrentPack?.Lock;
        var pins = lockDoc?.Ckan.Modules.Where(m => m.Kind != ModuleKindDto.Dlc).ToDictionary(m => m.Identifier, m => m.Version, StringComparer.Ordinal)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return new CatalogTarget(setup.GameDir, setup.Name, pins, lockDoc?.Game.KspVersion);
    }

    public Task<CatalogSnapshot> SnapshotAsync(SetupInfo setup, CancellationToken ct)
        => OnCatalog(c => c.Build(TargetFor(setup)), ct);

    public Task<ModDetails> DetailsAsync(SetupInfo setup, string identifier, CancellationToken ct)
        => OnCatalog(c => c.Details(TargetFor(setup), identifier), ct);

    public Task<ChangePlan> PreviewAsync(SetupInfo setup, ChangeRequest request, CancellationToken ct)
        => OnCatalog(c => c.Preview(TargetFor(setup), request), ct);

    /// <summary>Applies a reviewed change as one operation (progress in the activity strip, cancellable).</summary>
    public async Task<ApplyReport> ApplyAsync(SetupInfo setup, ChangeRequest request, string title)
    {
        try
        {
            return await _runner.RunAsync(OperationKind.Mods, title, (progress, ct) => OnCatalog(c => c.Apply(TargetFor(setup), request, progress, ct), ct)).ConfigureAwait(true);
        }
        finally
        {
            Changed?.Invoke();
        }
    }
}
