using System.Text.Json;
using CKAN;
using CKAN.Games;
using CKAN.IO;
using ICSharpCode.SharpZipLib.Zip;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Ckan.Locking;

/// <summary>
/// Computes the exact installed files (path, size, sha256) a CKAN module produces, straight from its cached archive
/// using CKAN's own <see cref="ModuleInstaller.FindInstallableFiles(CkanModule, string, IGame)"/>.
/// Results are cached on disk keyed by (artifact sha256, metadata sha256), so re-captures are fast.
/// </summary>
public sealed class ArchiveContentIndexer
{
    public sealed record ExpectedFile(string Path, long Size, string Sha256);

    private sealed class CacheFile
    {
        public Dictionary<string, List<ExpectedFile>> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly string _cachePath;
    private readonly CacheFile _cache;
    private readonly IGame _game;
    private bool _dirty;

    public ArchiveContentIndexer(string cachePath, IGame game)
    {
        _cachePath = cachePath;
        _game = game;
        _cache = File.Exists(cachePath) ? LauncherJson.ReadFile<CacheFile>(cachePath, 256L * 1024 * 1024) : new CacheFile();
    }

    public int CacheHits { get; private set; }
    public int CacheMisses { get; private set; }

    public IReadOnlyList<ExpectedFile> Index(CkanModule module, string archivePath, string metadataSha256, CancellationToken ct = default)
    {
        var artifactSha = module.download_hash?.sha256?.ToLowerInvariant() ?? Sha256Util.HashFile(archivePath, ct);
        var key = $"{artifactSha}:{metadataSha256}";
        if (_cache.Entries.TryGetValue(key, out var cached))
        {
            CacheHits++;
            return cached;
        }
        CacheMisses++;
        var result = new List<ExpectedFile>();
        using (var zip = new ZipFile(archivePath))
        {
            foreach (var f in ModuleInstaller.FindInstallableFiles(module, zip, _game))
            {
                ct.ThrowIfCancellationRequested();
                if (f.source.IsDirectory || f.destination.EndsWith(".ckan", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // CKAN never installs .ckan files from archives
                }
                using var s = zip.GetInputStream(f.source);
                var (sha, len) = Sha256Util.HashStreamCounted(s, 8L * 1024 * 1024 * 1024, ct);
                result.Add(new ExpectedFile(CKANPathUtils.NormalizePath(f.destination), len, sha));
            }
        }
        _cache.Entries[key] = result;
        _dirty = true;
        return result;
    }

    public void Save()
    {
        if (_dirty)
        {
            LauncherJson.WriteFile(_cachePath, _cache);
            _dirty = false;
        }
    }
}
