using System.Collections.Concurrent;
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
    private readonly AppSettingsStore _settings;
    private readonly LibraryService _library;
    private readonly ConcurrentDictionary<string, Task<ModDetails>> _details = new(StringComparer.Ordinal);
    private CatalogService? _catalog;
    private Task<CatalogUpdateOutcome>? _updating;

    public ModManagerService(LauncherPaths paths, CkanWorker worker, LauncherService launcher, OperationRunner runner, AppSettingsStore settings, LibraryService library, ILogger<ModManagerService> log)
    {
        _paths = paths;
        _worker = worker;
        _launcher = launcher;
        _runner = runner;
        _settings = settings;
        _library = library;
        _log = log;
        Changed += () => _details.Clear();
    }

    /// <summary>The verified library behind the "Vlad's mods" tab, or null when there is none.</summary>
    public LibraryStatus? Library => _library.Status();

    /// <summary>Why the tab has nothing to show, when it doesn't.</summary>
    public string? LibraryProblem => _library.Problem;

    /// <summary>
    /// Brings Vlad's library up to date and into the catalog. Never throws: a library that is missing, offline or refused only
    /// leaves the tab empty (with the reason), and the rest of the Mods page works as before.
    /// </summary>
    public async Task EnsureLibraryAsync(bool force, CancellationToken ct)
    {
        try
        {
            var result = await _library.GetAsync(force, ct).ConfigureAwait(false);
            var outcome = await OnCatalog(c => c.UseLibrary(result is null ? null : LibraryService.ToCatalogLibrary(result), ct), ct).ConfigureAwait(false);
            foreach (var s in outcome.Skipped)
            {
                _log.LogWarning("Vlad's mods: skipped {Entry}", s);
            }
            if (outcome.Changed)
            {
                _log.LogInformation("Vlad's mods: {Mods} mod(s) in the catalog", outcome.Mods);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Vlad's mods could not be loaded into the catalog");
        }
    }

    /// <summary>Raised (on any thread) after the catalog was refreshed or a setup's mods changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// For players who use the Mods page: load the catalog from disk shortly after start (on the CKAN thread, while
    /// they look at Home), so the page opens instantly. Players who never open it never pay its memory.
    /// </summary>
    public async Task WarmUpAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (!_settings.Current.ModsOpened || _runner.IsBusy)
            {
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await OnCatalog(c =>
            {
                c.EnsureLoaded();
                return 0;
            }, CancellationToken.None).ConfigureAwait(false);
            _log.LogInformation("Mod catalog warmed up in {Seconds:F1} s", sw.Elapsed.TotalSeconds);
            await EnsureLibraryAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Warming up the mod catalog failed");
        }
    }

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
            await EnsureLibraryAsync(force, ct).ConfigureAwait(false);
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

    public async Task<CatalogSnapshot> SnapshotAsync(SetupInfo setup, CancellationToken ct)
    {
        await EnsureLibraryAsync(false, ct).ConfigureAwait(false);
        return await OnCatalog(c => c.Build(TargetFor(setup)), ct).ConfigureAwait(false);
    }

    /// <summary>A mod's details; remembered until the catalog or the setup's mods change.</summary>
    public Task<ModDetails> DetailsAsync(SetupInfo setup, string identifier, CancellationToken ct)
    {
        var key = setup.Id + "|" + identifier;
        if (_details.TryGetValue(key, out var cached) && !cached.IsFaulted && !cached.IsCanceled)
        {
            return cached;
        }
        var task = OnCatalog(c => c.Details(TargetFor(setup), identifier), ct);
        _details[key] = task;
        return task;
    }

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
