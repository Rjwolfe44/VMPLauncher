using System.Diagnostics;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Security;

namespace VmpLauncher.Core.Transactions;

/// <summary>
/// Restores a profile to its snapshot. Files that appeared since the snapshot go to quarantine (never deleted);
/// files whose NTFS identity still matches the snapshot link are untouched; everything else is re-linked
/// from the snapshot. Then the CKAN registry and launcher state copies are put back.
/// The caller must have released CKAN's registry (DisposeInstance) before calling this.
/// </summary>
public static class RollbackEngine
{
    public sealed record Result(int Untouched, int Restored, int Quarantined, int StateFilesRestored, TimeSpan Elapsed, IReadOnlyList<string> Errors);

    private static readonly string[] ManagedRoots = { "GameData", "Ships", "Missions" };

    /// <param name="fallbackRules">Used only for snapshots written before the manifest recorded its own rules.</param>
    public static Result Restore(ProfilePaths profile, TransactionJournal journal, FileRules? fallbackRules, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var manifest = SnapshotEngine.Read(profile, journal.TxnId)
                       ?? throw new LauncherException(ErrorCode.RollbackFailed, $"Snapshot for transaction {journal.TxnId} is missing; cannot restore.", RecoveryHint.None);
        var snapDir = Path.Combine(profile.SnapshotsDir, journal.TxnId);
        var gameSnap = Path.Combine(snapDir, "game");
        // The snapshot skipped what its rules ignore; quarantining must ignore exactly the same files.
        var ruleSet = new FileRuleSet(manifest.Rules ?? fallbackRules ?? FileRules.Default);
        var errors = new List<string>();
        var isWindows = OperatingSystem.IsWindows();
        var byPath = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var enumeration = SafePath.RecurseSkippingReparsePoints();

        // 1. Quarantine files that are not in the snapshot (within managed roots, ignoring rules).
        var quarantineDir = Path.Combine(profile.QuarantineDir, journal.TxnId);
        var quarantined = 0;
        bool Quarantine(string abs, string rel, string what)
        {
            var dst = Path.Combine(quarantineDir, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(abs, dst, overwrite: true);
                quarantined++;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{what} {rel}: {ex.Message}");
                return false;
            }
        }
        foreach (var root in ManagedRoots)
        {
            var abs = Path.Combine(profile.GameDir, root);
            if (!Directory.Exists(abs) || SafePath.IsReparsePoint(abs))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(abs, "*", enumeration).ToList())
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(profile.GameDir, file).Replace('\\', '/');
                if (ruleSet.IsIgnored(rel) || byPath.ContainsKey(rel))
                {
                    continue;
                }
                Quarantine(file, rel, "quarantine");
            }
        }
        // Game-root files: only those a pack inventory owns are candidates, so stock files (KSP_x64.exe, hard-linked
        // from the player's install) are never moved. A candidate that exists but is not in the snapshot was created
        // by the transaction (a GameRoot install).
        foreach (var rel in manifest.RootFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (rel.Contains('/') || byPath.ContainsKey(rel) || ruleSet.IsIgnored(rel))
            {
                continue;
            }
            var abs = Path.Combine(profile.GameDir, rel);
            if (File.Exists(abs))
            {
                Quarantine(abs, rel, "quarantine");
            }
        }

        // 2. Restore snapshot files: same file id → untouched; otherwise re-link/copy.
        var untouched = 0;
        var restored = 0;
        foreach (var entry in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var abs = Path.Combine(profile.GameDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var snap = Path.Combine(gameSnap, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(snap))
            {
                errors.Add($"snapshot missing {entry.Path}");
                continue;
            }
            if (SafePath.HasReparsePointAbove(profile.GameDir, abs))
            {
                errors.Add($"restore {entry.Path}: a folder on the path is a junction or symbolic link");
                continue;
            }
            if (File.Exists(abs))
            {
                var same = false;
                if (isWindows && entry.FileId is not null)
                {
                    try { same = NativeFiles.FileIdString(abs) == entry.FileId; } catch (Exception) { }
                }
                else
                {
                    var fi = new FileInfo(abs);
                    same = fi.Length == entry.Size && fi.LastWriteTimeUtc.Ticks == entry.LastWriteTicks;
                }
                if (same)
                {
                    untouched++;
                    continue;
                }
                if (!Quarantine(abs, entry.Path, "move aside"))
                {
                    continue;
                }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                if (!(isWindows && NativeFiles.TryCreateHardLink(abs, snap, out _)))
                {
                    File.Copy(snap, abs, overwrite: true);
                }
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"restore {entry.Path}: {ex.Message}");
            }
        }

        // 3. CKAN registry + launcher state.
        var stateRestored = 0;
        foreach (var rel in manifest.CopiedStateFiles)
        {
            var src = rel.StartsWith("game/CKAN/", StringComparison.Ordinal)
                ? Path.Combine(snapDir, "ckan", Path.GetFileName(rel))
                : Path.Combine(snapDir, "state", Path.GetFileName(rel));
            var dst = Path.Combine(profile.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                var tmp = dst + ".tmp";
                File.Copy(src, tmp, overwrite: true);
                File.Move(tmp, dst, overwrite: true);
                stateRestored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"restore state {rel}: {ex.Message}");
            }
        }
        // A transaction that started from an empty profile had no state files: remove any half-written ones.
        if (!manifest.CopiedStateFiles.Any(f => f.EndsWith("installed.json", StringComparison.Ordinal)) && File.Exists(profile.InstalledStateFile))
        {
            File.Delete(profile.InstalledStateFile);
        }
        if (!manifest.CopiedStateFiles.Any(f => f.EndsWith("registry.json", StringComparison.Ordinal)))
        {
            var reg = Path.Combine(profile.CkanDir, "registry.json");
            if (File.Exists(reg))
            {
                Quarantine(reg, "registry.json", "quarantine");
            }
        }
        // CKAN temp dirs from the aborted transaction.
        if (Directory.Exists(profile.CkanDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(profile.CkanDir, "TxFileMgr-*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
        // Empty directories left behind under managed roots go; directories that existed before stay or are recreated
        // (CKAN registers directories, empty ones included, and its restored registry still lists them).
        var keep = new HashSet<string>(
            manifest.Directories.Select(d => Path.Combine(profile.GameDir, d.Replace('/', Path.DirectorySeparatorChar))),
            StringComparer.OrdinalIgnoreCase);
        foreach (var root in ManagedRoots)
        {
            PruneEmptyDirectories(Path.Combine(profile.GameDir, root), keepRoot: true, keep);
        }
        foreach (var dir in keep)
        {
            if (Directory.Exists(dir) || SafePath.HasReparsePointAbove(profile.GameDir, dir))
            {
                continue;
            }
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"recreate folder {Path.GetRelativePath(profile.GameDir, dir)}: {ex.Message}");
            }
        }
        return new Result(untouched, restored, quarantined, stateRestored, sw.Elapsed, errors);
    }

    /// <summary>Deletes empty directories below <paramref name="dir"/> (except those in <paramref name="keep"/>), never descending into junctions or symbolic links.</summary>
    public static void PruneEmptyDirectories(string dir, bool keepRoot, IReadOnlySet<string>? keep = null)
    {
        if (!Directory.Exists(dir) || SafePath.IsReparsePoint(dir))
        {
            return;
        }
        foreach (var sub in Directory.EnumerateDirectories(dir).ToList())
        {
            if (!SafePath.IsReparsePoint(sub))
            {
                PruneEmptyDirectories(sub, keepRoot: false, keep);
            }
        }
        if (!keepRoot && keep?.Contains(dir) != true && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try { Directory.Delete(dir); } catch (IOException) { }
        }
    }
}
