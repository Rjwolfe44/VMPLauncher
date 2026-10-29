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

        var parallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var results = new (InventoryEntry entry, InstalledFileRecord? record, FileProblem? problem, bool hashedOne)[total];
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, total), new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
            {
                results[i] = VerifyOne(profile, inventory.Files[i], mode, prev, isWindows, ct);
                var d = Interlocked.Increment(ref done);
                if ((d & 255) == 0)
                {
                    _progress?.Report((d, total));
                }
            }
        });

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

    private static (InventoryEntry entry, InstalledFileRecord? record, FileProblem? problem, bool hashedOne) VerifyOne(
        ProfilePaths profile, InventoryEntry entry, Mode mode, Dictionary<string, InstalledFileRecord>? prev, bool isWindows, CancellationToken ct)
    {
        var abs = Path.Combine(profile.GameDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        var fi = new FileInfo(abs);
        if (!fi.Exists)
        {
            return (entry, null, new FileProblem(entry.Path, entry.Owner, "missing", "file not found"), false);
        }
        var ticks = fi.LastWriteTimeUtc.Ticks;
        var fileId = OperatingSystem.IsWindows() ? TryFileId(abs) : null;
        string? sha = null;
        var needHash = mode == Mode.Full;
        if (!needHash)
        {
            if (prev is not null && prev.TryGetValue(entry.Path, out var p) && p.Size == fi.Length && p.LastWriteTicks == ticks && (p.FileId is null || fileId is null || p.FileId == fileId))
            {
                sha = p.Sha256;
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
        if (fi.Length != entry.Size || !Sha256Util.HexEquals(sha, entry.Sha256))
        {
            problem = new FileProblem(entry.Path, entry.Owner, entry.Class == FileClass.MutableOwned ? "drift" : "modified",
                $"size {fi.Length} vs {entry.Size}, sha {(sha is null ? "?" : sha[..12])} vs {entry.Sha256[..12]}");
        }
        var record = new InstalledFileRecord { Path = entry.Path, Size = fi.Length, LastWriteTicks = ticks, FileId = fileId, Sha256 = sha!, Owner = entry.Owner };
        return (entry, record, problem, didHash);
    }
}
