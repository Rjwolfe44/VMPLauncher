using System.Text.Json;
using CKAN;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Profiles;

namespace VmpLauncher.Ckan.Backend;

/// <summary>
/// Reuses archives a player already has in a normal CKAN client's download cache instead of downloading them again.
/// CKAN names cached files "&lt;first 8 hex of SHA-1(download URL)&gt;-&lt;file&gt;" in every cache, so a needed module's
/// file is found by name, then proven by size and SHA-256 against the signed lock before it is used. Found files are
/// hard-linked into the launcher's cache (same drive: no extra disk space, and the other CKAN keeps its copy) or
/// copied when the caches are on different drives. Nothing in the other cache is ever modified or moved.
/// </summary>
public static class CkanCacheImporter
{
    public sealed record Result(int Imported, long Bytes, int Linked, int Copied, int Rejected, IReadOnlyList<string> Sources);

    /// <summary>
    /// The CKAN download caches on this PC: the folder from CKAN's own config.json and the default location, minus the
    /// launcher's cache itself.
    /// </summary>
    public static IReadOnlyList<string> DiscoverCaches(string launcherCacheDir)
    {
        var candidates = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ckanConfig = Path.Combine(local, "CKAN", "config.json");
        try
        {
            if (File.Exists(ckanConfig))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ckanConfig));
                if (doc.RootElement.TryGetProperty("DownloadCacheDir", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()))
                {
                    candidates.Add(d.GetString()!);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // unreadable CKAN config: fall back to the default location
        }
        candidates.Add(Path.Combine(local, "CKAN", "downloads"));
        var own = Normalize(launcherCacheDir);
        return candidates
            .Select(Normalize)
            .Where(c => !string.Equals(c, own, StringComparison.OrdinalIgnoreCase) && Directory.Exists(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Brings every archive the lock needs and the launcher cache lacks over from <paramref name="sourceCaches"/>.</summary>
    public static Result Import(PackLock lockDoc, string launcherCacheDir, IReadOnlyList<string> sourceCaches, IProgress<(int Done, int Total, string Name)>? progress, CancellationToken ct)
    {
        if (sourceCaches.Count == 0)
        {
            return new Result(0, 0, 0, 0, 0, sourceCaches);
        }
        Directory.CreateDirectory(launcherCacheDir);
        var index = new VerifiedArtifactIndex(Path.Combine(launcherCacheDir, "vmpl-verified.json"));
        var existing = new HashSet<string>(
            new DirectoryInfo(launcherCacheDir).EnumerateFiles().Where(f => f.Name.Length > 9 && f.Name[8] == '-').Select(f => f.Name[..8]),
            StringComparer.OrdinalIgnoreCase);
        // name prefix -> files in the source caches (one directory listing per cache, not one per module)
        var sourceFiles = sourceCaches
            .SelectMany(dir => new DirectoryInfo(dir).EnumerateFiles())
            .Where(f => f.Name.Length > 9 && f.Name[8] == '-')
            .GroupBy(f => f.Name[..8], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var wanted = lockDoc.Ckan.Modules
            .Where(m => m.Artifact is { Distribution: "download" } && m.Artifact.Size > 0)
            .Select(m => (Module: m, Prefixes: Prefixes(m).ToList()))
            .Where(x => x.Prefixes.Count > 0 && !x.Prefixes.Any(existing.Contains))
            .ToList();

        int imported = 0, linked = 0, copied = 0, rejected = 0, done = 0;
        long bytes = 0;
        foreach (var (module, prefixes) in wanted)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done++, wanted.Count, module.Identifier));
            var artifact = module.Artifact!;
            var match = prefixes.SelectMany(p => sourceFiles.TryGetValue(p, out var list) ? list : Enumerable.Empty<FileInfo>())
                .FirstOrDefault(f => f.Length == artifact.Size);
            if (match is null)
            {
                continue;
            }
            var dest = Path.Combine(launcherCacheDir, match.Name);
            var tmp = dest + ".import";
            try
            {
                var isLink = OperatingSystem.IsWindows() && NativeFiles.TryCreateHardLink(tmp, match.FullName, out _);
                if (!isLink)
                {
                    File.Copy(match.FullName, tmp, overwrite: true);
                }
                // Prove the bytes before CKAN ever sees the file under its cache name.
                if (!Sha256Util.HexEquals(Sha256Util.HashFile(tmp, ct), artifact.Sha256))
                {
                    File.Delete(tmp);
                    rejected++;
                    continue;
                }
                File.Move(tmp, dest, overwrite: false);
                index.Sha256Of(dest, ct); // records the verified hash, so the install does not hash it again
                imported++;
                bytes += artifact.Size;
                if (isLink)
                {
                    linked++;
                }
                else
                {
                    copied++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // locked or vanished in the other cache: this one simply downloads as usual
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
        index.Save();
        progress?.Report((wanted.Count, wanted.Count, string.Empty));
        return new Result(imported, bytes, linked, copied, rejected, sourceCaches);
    }

    private static IEnumerable<string> Prefixes(LockedModule m)
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
        return urls.Distinct(StringComparer.Ordinal)
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? NetFileCache.CreateURLHash(uri) : null)
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
