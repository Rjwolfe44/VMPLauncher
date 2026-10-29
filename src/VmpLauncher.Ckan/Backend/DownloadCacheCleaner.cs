using System.Text.Json;
using System.Text.RegularExpressions;
using CKAN;
using VmpLauncher.Core.Locking;

namespace VmpLauncher.Ckan.Backend;

/// <summary>
/// Frees disk space in the launcher's own download cache: every archive that neither the current pack nor the
/// installed version still references is removed (they would only be needed again for a downgrade). CKAN names cached
/// archives "&lt;first 8 hex of SHA-1(download URL)&gt;-&lt;file&gt;", so the keep-set is computed from each module's
/// download URLs, including the placeholder URL private packages are cached under. Private zips in
/// <c>cache\private</c> are kept by SHA-256. Never point this at a cache shared with a normal CKAN client.
/// </summary>
public static partial class DownloadCacheCleaner
{
    public sealed record Plan(string CacheDir, IReadOnlyList<string> Remove, long RemoveBytes, int KeptFiles, long KeptBytes);

    [GeneratedRegex("^[0-9A-Fa-f]{8}-.+", RegexOptions.CultureInvariant)]
    private static partial Regex CachedArchive();

    public static Plan Analyze(string cacheDir, IEnumerable<PackLock> keep)
    {
        var keepPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keepPrivate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lk in keep)
        {
            foreach (var m in lk.Ckan.Modules)
            {
                foreach (var url in DownloadUrls(m))
                {
                    keepPrefixes.Add(NetFileCache.CreateURLHash(url));
                }
                if (m.Artifact is { Distribution: "private-sideload" } a)
                {
                    keepPrivate.Add(a.Sha256.ToLowerInvariant() + ".zip");
                }
            }
            foreach (var o in lk.Overrides)
            {
                foreach (var u in o.Content?.Artifact?.Urls ?? Array.Empty<string>())
                {
                    if (Uri.TryCreate(u, UriKind.Absolute, out var uri))
                    {
                        keepPrefixes.Add(NetFileCache.CreateURLHash(uri));
                    }
                }
            }
        }

        var remove = new List<string>();
        long removeBytes = 0, keptBytes = 0;
        var kept = 0;
        if (Directory.Exists(cacheDir))
        {
            foreach (var f in new DirectoryInfo(cacheDir).EnumerateFiles())
            {
                if (!CachedArchive().IsMatch(f.Name))
                {
                    continue; // index files and anything that is not a CKAN cache entry
                }
                if (keepPrefixes.Contains(f.Name[..8]))
                {
                    kept++;
                    keptBytes += f.Length;
                }
                else
                {
                    remove.Add(f.FullName);
                    removeBytes += f.Length;
                }
            }
        }
        var privateDir = Path.Combine(cacheDir, "private");
        if (Directory.Exists(privateDir))
        {
            foreach (var f in new DirectoryInfo(privateDir).EnumerateFiles("*.zip"))
            {
                if (keepPrivate.Contains(f.Name))
                {
                    kept++;
                    keptBytes += f.Length;
                }
                else
                {
                    remove.Add(f.FullName);
                    removeBytes += f.Length;
                    if (File.Exists(f.FullName + ".ok"))
                    {
                        remove.Add(f.FullName + ".ok");
                    }
                }
            }
        }
        return new Plan(cacheDir, remove, removeBytes, kept, keptBytes);
    }

    /// <summary>Deletes the planned files (each re-checked to be inside the cache folder); returns what was freed.</summary>
    public static (int Files, long Bytes) Execute(Plan plan, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(plan.CacheDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var index = new VerifiedArtifactIndex(Path.Combine(plan.CacheDir, "vmpl-verified.json"));
        var files = 0;
        long bytes = 0;
        foreach (var path in plan.Remove)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                var fi = new FileInfo(full);
                if (!fi.Exists)
                {
                    continue;
                }
                var size = fi.Length;
                fi.Delete();
                index.Forget(full);
                files++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use or read-only: leave it for next time
            }
        }
        index.Save();
        return (files, bytes);
    }

    /// <summary>Every URL CKAN may have cached the module under: the lock's artifact URLs and the metadata's "download".</summary>
    private static IEnumerable<Uri> DownloadUrls(LockedModule m)
    {
        var urls = new List<string>(m.Artifact?.Urls ?? Array.Empty<string>());
        if (m.Metadata.ValueKind == JsonValueKind.Object && m.Metadata.TryGetProperty("download", out var d))
        {
            if (d.ValueKind == JsonValueKind.String)
            {
                urls.Add(d.GetString()!);
            }
            else if (d.ValueKind == JsonValueKind.Array)
            {
                urls.AddRange(d.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            }
        }
        foreach (var u in urls.Distinct(StringComparer.Ordinal))
        {
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri))
            {
                yield return uri;
            }
        }
    }
}
