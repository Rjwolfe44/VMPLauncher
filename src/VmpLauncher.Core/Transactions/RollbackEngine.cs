using System.Diagnostics;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;

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

    public static Result Restore(ProfilePaths profile, TransactionJournal journal, FileRules rules, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var manifest = SnapshotEngine.Read(profile, journal.TxnId)
                       ?? throw new LauncherException(ErrorCode.RollbackFailed, $"Snapshot for transaction {journal.TxnId} is missing; cannot restore.", RecoveryHint.None);
        var snapDir = Path.Combine(profile.SnapshotsDir, journal.TxnId);
        var gameSnap = Path.Combine(snapDir, "game");
        var ruleSet = new FileRuleSet(rules);
        var errors = new List<string>();
        var isWindows = OperatingSystem.IsWindows();
        var byPath = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        // 1. Quarantine files that are not in the snapshot (within managed roots, ignoring rules).
        var quarantineDir = Path.Combine(profile.QuarantineDir, journal.TxnId);
        var quarantined = 0;
        foreach (var root in new[] { "GameData", "Ships", "Missions" })
        {
            var abs = Path.Combine(profile.GameDir, root);
            if (!Directory.Exists(abs))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories).ToList())
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(profile.GameDir, file).Replace('\\', '/');
                if (ruleSet.IsIgnored(rel) || byPath.ContainsKey(rel))
                {
                    continue;
                }
                var dst = Path.Combine(quarantineDir, rel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Move(file, dst, overwrite: true);
                    quarantined++;
                }
                catch (IOException ex)
                {
                    errors.Add($"quarantine {rel}: {ex.Message}");
                }
            }
        }
        // Root files that the snapshot knows but are not under a managed root (GameRoot installs) are handled below.

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
                try
                {
                    var q = Path.Combine(quarantineDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(q)!);
                    File.Move(abs, q, overwrite: true);
                    quarantined++;
                }
                catch (IOException ex)
                {
                    errors.Add($"move aside {entry.Path}: {ex.Message}");
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
            catch (IOException ex)
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
                File.Move(reg, Path.Combine(quarantineDir, "registry.json"), overwrite: true);
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
        // Empty directories left behind under managed roots.
        foreach (var root in new[] { "GameData", "Ships", "Missions" })
        {
            PruneEmptyDirectories(Path.Combine(profile.GameDir, root), keepRoot: true);
        }
        return new Result(untouched, restored, quarantined, stateRestored, sw.Elapsed, errors);
    }

    public static void PruneEmptyDirectories(string dir, bool keepRoot)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (var sub in Directory.EnumerateDirectories(dir).ToList())
        {
            PruneEmptyDirectories(sub, keepRoot: false);
        }
        if (!keepRoot && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try { Directory.Delete(dir); } catch (IOException) { }
        }
    }
}
