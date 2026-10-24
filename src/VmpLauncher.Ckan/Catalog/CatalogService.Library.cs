using System.Security.Cryptography;
using CKAN;
using VmpLauncher.Ckan.Repo;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.Hashing;

namespace VmpLauncher.Ckan.Catalog;

/// <param name="Mods">Distinct mods now offered from the library.</param>
/// <param name="Skipped">Library entries that were left out, each with the reason (for the log, never for a player).</param>
/// <param name="Changed">True when the catalog now offers something different from before this call.</param>
public sealed record LibraryLoadOutcome(int Mods, IReadOnlyList<string> Skipped, bool Changed);

/// <summary>
/// Vlad's mod library as a second CKAN repository. The launcher verified the signed index before it gets here; this
/// turns the modules into a local repository archive (never fetched from anywhere) and registers it next to the standard
/// catalog, so the same CKAN engine browses, resolves and installs them. The repository is added to each setup's registry
/// only in memory while browsing and while a change runs, and taken out again before the registry is saved, so a KSP folder
/// the player also manages with CKAN never ends up listing the launcher's private library file.
/// </summary>
public sealed partial class CatalogService
{
    public const string LibraryRepoName = "vlad-library";

    private Repository? _libraryRepo;
    private string? _libraryKey;
    private IReadOnlyDictionary<string, VladShowcase> _showcase = new Dictionary<string, VladShowcase>(StringComparer.Ordinal);

    /// <summary>True once a verified library is part of the catalog.</summary>
    public bool HasLibrary => _libraryRepo is not null;

    /// <summary>The standard repository, plus Vlad's library when one is loaded.</summary>
    private Repository[] ActiveRepos => _libraryRepo is { } lib ? new[] { DefaultRepo, lib } : new[] { DefaultRepo };

    /// <summary>
    /// Offers <paramref name="library"/> (null removes it). Cheap when the library did not change since the last call.
    /// Entries whose identifier already exists in the standard catalog are skipped: the library may add mods, never replace
    /// or shadow someone else's. Must run on the CKAN worker thread.
    /// </summary>
    public LibraryLoadOutcome UseLibrary(VladLibrary? library, CancellationToken ct = default)
    {
        if (library is null || library.Modules.Count == 0)
        {
            var had = _libraryRepo is not null;
            if (had)
            {
                _libraryRepo = null;
                _libraryKey = null;
                _showcase = new Dictionary<string, VladShowcase>(StringComparer.Ordinal);
                _lastContext = null;
            }
            return new LibraryLoadOutcome(0, Array.Empty<string>(), had);
        }

        EnsureLoaded();
        var key = Sha256Util.HashUtf8(string.Join("\n", library.Modules
            .OrderBy(m => m.Identifier, StringComparer.Ordinal).ThenBy(m => m.Version, StringComparer.Ordinal)
            .Select(m => m.Identifier + "@" + m.Version + ":" + Sha256Util.HashUtf8(m.Json))))[..16];
        if (key == _libraryKey && _libraryRepo is not null)
        {
            return new LibraryLoadOutcome(library.Modules.Select(m => m.Identifier).Distinct(StringComparer.Ordinal).Count(), Array.Empty<string>(), false);
        }

        var skipped = new List<string>();
        var standard = AllIdentifiers();
        var modules = new List<CkanModule>();
        foreach (var m in library.Modules)
        {
            if (standard.Contains(m.Identifier))
            {
                skipped.Add($"{m.Identifier} {m.Version}: the standard catalog already has a mod with this identifier");
                continue;
            }
            try
            {
                modules.Add(CkanModule.FromJson(m.Json));
            }
            catch (Exception ex) when (ex is Kraken or Newtonsoft.Json.JsonException or ArgumentException or FormatException)
            {
                skipped.Add($"{m.Identifier} {m.Version}: CKAN could not read it ({ex.Message})");
            }
        }
        if (modules.Count == 0)
        {
            UseLibrary(null, ct);
            return new LibraryLoadOutcome(0, skipped, true);
        }

        var libraryDir = Path.Combine(_paths.CkanDir, "library");
        var zip = PackRepoBuilder.Build(modules, Path.Combine(libraryDir, key + ".zip"));
        var repo = new Repository(LibraryRepoName, new Uri(zip));
        var user = new VmpCkanUser();
        var downloader = new NetAsyncDownloader(user, () => SHA256.Create(), Backend.ProfileSession.UserAgent, ct);
        RepositoryDataManager.UpdateResult result;
        try
        {
            result = _repoData.Update(new[] { repo }, _game, skipETags: true, downloader, user, Backend.ProfileSession.UserAgent);
        }
        catch (CancelledActionKraken)
        {
            throw new OperationCanceledException(ct);
        }
        if (result == RepositoryDataManager.UpdateResult.Failed)
        {
            skipped.Add("CKAN could not load the library repository: " + string.Join("; ", user.Errors));
            UseLibrary(null, ct);
            return new LibraryLoadOutcome(0, skipped, true);
        }

        var loaded = modules.Select(m => m.identifier).ToHashSet(StringComparer.Ordinal);
        _showcase = library.Showcase.Where(s => loaded.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        _libraryRepo = repo;
        _libraryKey = key;
        _lastContext = null;
        RemoveOldLibraryFiles(libraryDir, key);
        return new LibraryLoadOutcome(loaded.Count, skipped, true);
    }

    /// <summary>Puts the library into a registry's repository list, in memory only.</summary>
    private void AddLibrary(Registry registry)
    {
        if (_libraryRepo is not { } lib || registry.Repositories.ContainsKey(lib.name))
        {
            return;
        }
        var repos = new SortedDictionary<string, Repository>(registry.Repositories.ToDictionary(r => r.Key, r => r.Value));
        repos[lib.name] = lib;
        registry.RepositoriesSet(repos);
    }

    /// <summary>Takes the library out again and saves, so the registry file never mentions it. Best effort.</summary>
    private static void StripLibrary(RegistryManager manager)
    {
        try
        {
            if (!manager.registry.Repositories.ContainsKey(LibraryRepoName))
            {
                return;
            }
            var repos = new SortedDictionary<string, Repository>(manager.registry.Repositories.Where(r => r.Key != LibraryRepoName).ToDictionary(r => r.Key, r => r.Value));
            manager.registry.RepositoriesSet(repos);
            manager.Save(enforce_consistency: false);
        }
        catch (Exception ex) when (ex is Kraken or IOException or UnauthorizedAccessException)
        {
            // The mods are installed either way; a leftover repository entry only costs a harmless line in the registry.
        }
    }

    private static void RemoveOldLibraryFiles(string dir, string keepKey)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.zip").Where(f => !Path.GetFileNameWithoutExtension(f).Equals(keepKey, StringComparison.Ordinal)))
            {
                File.Delete(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old archives are a few KB; they can wait.
        }
    }
}
