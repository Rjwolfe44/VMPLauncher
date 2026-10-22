using System.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Transactions;

/// <summary>
/// Pre-image of a profile before a mutation: every launcher-relevant game file hard-linked into
/// <c>snapshots\&lt;txn&gt;\game\</c> (≈25k links, seconds, ~0 bytes) plus copies of the CKAN registry and
/// launcher state. Safe because neither CKAN nor the launcher writes files in place: CKAN refuses to overwrite
/// and unlinks on removal; the launcher writes temp + rename. KSP must not be running.
/// </summary>
public static class SnapshotEngine
{
    /// <summary>Roots inside the game folder the launcher manages; stock/generated/per-player files inside them are skipped.</summary>
    private static readonly string[] ManagedRoots = { "GameData", "Ships", "Missions" };

    private static readonly string[] CkanStateFiles = { "registry.json", "compatible_ksp_versions.json" };

    public static SnapshotManifest Create(ProfilePaths profile, FileRules rules, string txnId, IEnumerable<string>? extraRootFiles, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var ruleSet = new FileRuleSet(rules);
        var snapDir = Path.Combine(profile.SnapshotsDir, txnId);
        var gameSnap = Path.Combine(snapDir, "game");
        Directory.CreateDirectory(gameSnap);
        var entries = new List<SnapshotEntry>();
        var isWindows = OperatingSystem.IsWindows();

        IEnumerable<string> files = ManagedRoots
            .Select(r => Path.Combine(profile.GameDir, r))
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories));
        foreach (var extra in extraRootFiles ?? Array.Empty<string>())
        {
            var abs = Path.Combine(profile.GameDir, extra);
            if (File.Exists(abs))
            {
                files = files.Append(abs);
            }
        }

        foreach (var abs in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(profile.GameDir, abs).Replace('\\', '/');
            if (ruleSet.IsIgnored(rel))
            {
                continue;
            }
            var fi = new FileInfo(abs);
            var dst = Path.Combine(gameSnap, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (isWindows && NativeFiles.TryCreateHardLink(dst, abs, out _))
            {
                // linked
            }
            else
            {
                File.Copy(abs, dst, overwrite: true);
            }
            entries.Add(new SnapshotEntry
            {
                Path = rel,
                Size = fi.Length,
                LastWriteTicks = fi.LastWriteTimeUtc.Ticks,
                FileId = isWindows ? TryFileId(abs) : null,
            });
        }

        var copied = new List<string>();
        var ckanSnap = Path.Combine(snapDir, "ckan");
        Directory.CreateDirectory(ckanSnap);
        foreach (var name in CkanStateFiles.Concat(Directory.Exists(profile.CkanDir) ? Directory.EnumerateFiles(profile.CkanDir, "installed-*.ckan").Select(Path.GetFileName)! : Array.Empty<string>()))
        {
            var src = Path.Combine(profile.CkanDir, name!);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(ckanSnap, name!), overwrite: true);
                copied.Add("game/CKAN/" + name);
            }
        }
        var stateSnap = Path.Combine(snapDir, "state");
        Directory.CreateDirectory(stateSnap);
        foreach (var src in new[] { profile.InstalledStateFile, profile.LockCopyFile, profile.InventoryCopyFile })
        {
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(stateSnap, Path.GetFileName(src)), overwrite: true);
                copied.Add("state/" + Path.GetFileName(src));
            }
        }

        var manifest = new SnapshotManifest { TxnId = txnId, Utc = DateTimeOffset.UtcNow, Files = entries, CopiedStateFiles = copied };
        LauncherJson.WriteFile(Path.Combine(snapDir, "manifest.json"), manifest);
        Debug.WriteLine($"snapshot {txnId}: {entries.Count} files in {sw.Elapsed}");
        return manifest;
    }

    public static SnapshotManifest? Read(ProfilePaths profile, string txnId)
    {
        var path = Path.Combine(profile.SnapshotsDir, txnId, "manifest.json");
        return File.Exists(path) ? LauncherJson.ReadFile<SnapshotManifest>(path, 256L * 1024 * 1024) : null;
    }

    public static void Delete(ProfilePaths profile, string txnId)
    {
        var dir = Path.Combine(profile.SnapshotsDir, txnId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? TryFileId(string path)
    {
        try
        {
            return NativeFiles.FileIdString(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
