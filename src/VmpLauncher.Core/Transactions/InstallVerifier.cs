using System.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Security;

namespace VmpLauncher.Core.Transactions;

/// <summary>Compares a profile's game folder against a lock inventory. Pure file work; no CKAN.</summary>
public sealed class InstallVerifier
{
    public sealed record FileProblem(string Path, string Owner, string Kind, string Detail);

    public sealed record Result(
        int FilesChecked,
        int FilesHashed,
        IReadOnlyList<FileProblem> Missing,
        IReadOnlyList<FileProblem> Modified,
        IReadOnlyList<FileProblem> MutableDrift,
        IReadOnlyList<string> UnknownFiles,
        IReadOnlyList<string> UnknownDlls,
        TimeSpan Elapsed,
        IReadOnlyList<InstalledFileRecord> Records)
    {
        public bool IsClean => Missing.Count == 0 && Modified.Count == 0 && UnknownDlls.Count == 0;
    }

    public enum Mode
    {
        /// <summary>Size + mtime + file id against installed.json; hash only when any differs.</summary>
        Quick,
        /// <summary>Hash every owned file.</summary>
        Full,
        /// <summary>
        /// After an update: reuse the hash recorded at the last commit only when size, mtime and NTFS file id all still
        /// match it (the same file, untouched since it was hashed); hash everything else, including every file CKAN or an
        /// override wrote and any file without a recorded id. Seconds instead of reading the whole install.
        /// </summary>
        Incremental,
    }

    private readonly IProgress<(int Done, int Total)>? _progress;

    public InstallVerifier(IProgress<(int Done, int Total)>? progress = null)
    {
        _progress = progress;
    }

    public Result Verify(ProfilePaths profile, InventoryDocument inventory, FileRules rules, Mode mode, InstalledState? previous = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var ruleSet = new FileRuleSet(rules);
        var missing = new List<FileProblem>();
        var modified = new List<FileProblem>();
        var drift = new List<FileProblem>();
        var records = new List<InstalledFileRecord>(inventory.Files.Count);
        var prev = previous?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var hashed = 0;
        var total = inventory.Files.Count;
        var done = 0;
        var isWindows = OperatingSystem.IsWindows();

        var results = new (InventoryEntry entry, InstalledFileRecord? record, FileProblem? problem, bool hashedOne)[total];
        ParallelFiles.For(total, FileParallelism.For(profile.GameDir), i =>
        {
            results[i] = VerifyOne(profile, inventory.Files[i], mode, prev, isWindows, ct);
            var d = Interlocked.Increment(ref done);
            if ((d & 255) == 0)
            {
                _progress?.Report((d, total));
            }
        }, ct);
        _progress?.Report((total, total));

        foreach (var (entry, record, problem, hashedOne) in results)
        {
            if (hashedOne)
            {
                hashed++;
            }
            if (record is not null)
            {
                records.Add(record);
            }
            if (problem is null)
            {
                continue;
            }
            switch (problem.Kind)
            {
                case "missing": missing.Add(problem); break;
                case "drift": drift.Add(problem); break;
                default: modified.Add(problem); break;
            }
        }

        // Unknown files: anything in GameData/Ships/Missions not in the inventory and not ignored by the rules.
        var known = new HashSet<string>(inventory.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        var unknownDlls = new List<string>();
        foreach (var root in new[] { "GameData" })
        {
            var abs = Path.Combine(profile.GameDir, root);
            if (!Directory.Exists(abs) || SafePath.IsReparsePoint(abs))
            {
                continue;
            }
            // Junctions and symbolic links are not followed: what they point at is not part of the profile.
            foreach (var file in Directory.EnumerateFiles(abs, "*", SafePath.RecurseSkippingReparsePoints()))
            {
                var rel = Path.GetRelativePath(profile.GameDir, file).Replace('\\', '/');
                if (known.Contains(rel) || ruleSet.IsIgnored(rel))
                {
                    continue;
                }
                unknown.Add(rel);
                if (rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    unknownDlls.Add(rel);
                }
            }
        }
        return new Result(total, hashed, missing, modified, drift, unknown, unknownDlls, sw.Elapsed, records);
    }

    private static (InventoryEntry entry, InstalledFileRecord? record, FileProblem? problem, bool hashedOne) VerifyOne(
        ProfilePaths profile, InventoryEntry entry, Mode mode, Dictionary<string, InstalledFileRecord>? prev, bool isWindows, CancellationToken ct)
    {
        var abs = Path.Combine(profile.GameDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!TryStat(abs, isWindows, out var length, out var ticks, out var fileId))
        {
            return (entry, null, new FileProblem(entry.Path, entry.Owner, "missing", "file not found"), false);
        }
        string? sha = null;
        var needHash = mode == Mode.Full;
        if (!needHash)
        {
            // Incremental trusts a recorded hash only for the very same file: same size, same time and same file id.
            var trusted = prev is not null && prev.TryGetValue(entry.Path, out var p) && p.Size == length && p.LastWriteTicks == ticks
                          && (mode == Mode.Incremental ? p.FileId is not null && p.FileId == fileId : p.FileId is null || fileId is null || p.FileId == fileId);
            if (trusted)
            {
                sha = prev![entry.Path].Sha256;
            }
            else
            {
                needHash = true;
            }
        }
        var didHash = false;
        if (needHash)
        {
            sha = Sha256Util.HashFile(abs, ct);
            didHash = true;
        }
        FileProblem? problem = null;
        if (length != entry.Size || !Sha256Util.HexEquals(sha, entry.Sha256))
        {
            problem = new FileProblem(entry.Path, entry.Owner, entry.Class == FileClass.MutableOwned ? "drift" : "modified",
                $"size {length} vs {entry.Size}, sha {(sha is null ? "?" : sha[..12])} vs {entry.Sha256[..12]}");
        }
        var record = new InstalledFileRecord { Path = entry.Path, Size = length, LastWriteTicks = ticks, FileId = fileId, Sha256 = sha!, Owner = entry.Owner };
        return (entry, record, problem, didHash);
    }

    /// <summary>Size, time and file id from one attribute-only open on Windows (no read access, so nothing to scan).</summary>
    private static bool TryStat(string abs, bool isWindows, out long length, out long ticks, out string? fileId)
    {
        if (isWindows && OperatingSystem.IsWindows())
        {
            try
            {
                if (!NativeFiles.TryGetFileStat(abs, out var stat))
                {
                    length = ticks = 0;
                    fileId = null;
                    return false;
                }
                (length, ticks, fileId) = (stat.Size, stat.LastWriteTicks, stat.FileId);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // fall back to the plain file info below, without a file id
            }
        }
        var fi = new FileInfo(abs);
        (length, ticks, fileId) = fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks, (string?)null) : (0L, 0L, null);
        return fi.Exists;
    }
}
