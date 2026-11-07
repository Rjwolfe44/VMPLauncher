using System.Diagnostics;
using System.IO.Enumeration;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Transactions;

/// <summary>
/// Pre-image of a profile before a mutation: every launcher-relevant game file hard-linked into
/// <c>snapshots\&lt;txn&gt;\game\</c> (≈25k links, ~0 bytes) plus copies of the CKAN registry and launcher state.
/// Safe because neither CKAN nor the launcher writes files in place: CKAN refuses to overwrite and unlinks on
/// removal; the launcher writes temp + rename. KSP must not be running.
/// </summary>
/// <remarks>
/// The snapshot covers every file, not only the ones a lock diff predicts will change: CKAN may also remove modules
/// the diff does not name (auto-installed dependencies, reverse dependencies), and a restore can only bring back what
/// was linked. The links are made on several threads (<see cref="FileParallelism"/>); a discarded snapshot is renamed
/// into the profile's trash at once and deleted later (<see cref="EmptyTrash"/>).
/// </remarks>
public static class SnapshotEngine
{
    /// <summary>Roots inside the game folder the launcher manages; stock/generated/per-player files inside them are skipped.</summary>
    private static readonly string[] ManagedRoots = { "GameData", "Ships", "Missions" };

    private static readonly string[] CkanStateFiles = { "registry.json", "compatible_ksp_versions.json" };

    public static string TrashDir(ProfilePaths profile) => Path.Combine(profile.Root, "trash");

    /// <param name="rootFiles">
    /// Pack-owned files directly in the game folder (from the inventories before and after the transaction). Those that
    /// exist are snapshotted; all are recorded so a restore can quarantine the ones the transaction created.
    /// </param>
    public static SnapshotManifest Create(ProfilePaths profile, FileRules rules, string txnId, IEnumerable<string>? rootFiles, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var ruleSet = new FileRuleSet(rules);
        var snapDir = Path.Combine(profile.SnapshotsDir, txnId);
        var gameSnap = Path.Combine(snapDir, "game");
        Directory.CreateDirectory(gameSnap);
        var isWindows = OperatingSystem.IsWindows();

        // One walk of the managed roots. Junctions and symbolic links are neither followed nor recorded: their targets
        // are not the profile's files.
        var directories = new List<string>();
        var files = new List<string>();
        foreach (var root in ManagedRoots)
        {
            var abs = Path.Combine(profile.GameDir, root);
            if (!Directory.Exists(abs) || SafePath.IsReparsePoint(abs))
            {
                continue;
            }
            var walk = new FileSystemEnumerable<(string Path, bool IsDirectory)>(abs, (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory), SafePath.RecurseSkippingReparsePoints());
            foreach (var (path, isDirectory) in walk)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(profile.GameDir, path).Replace('\\', '/');
                if (isDirectory)
                {
                    directories.Add(rel);
                }
                else if (!ruleSet.IsIgnored(rel))
                {
                    files.Add(rel);
                }
            }
        }
        var roots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in rootFiles ?? Array.Empty<string>())
        {
            if (!SafePath.TryNormalize(candidate, out var rel, out _) || rel.Contains('/') || !roots.Add(rel))
            {
                continue;
            }
            var abs = Path.Combine(profile.GameDir, rel);
            if (File.Exists(abs) && !SafePath.IsReparsePoint(abs) && !ruleSet.IsIgnored(rel))
            {
                files.Add(rel);
            }
        }

        // Folders first (parents before children, as the walk returned them), then the links on several threads.
        foreach (var dir in ManagedRoots.Concat(directories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(gameSnap, dir.Replace('/', Path.DirectorySeparatorChar)));
        }
        var entries = new SnapshotEntry?[files.Count];
        ParallelFiles.For(files.Count, FileParallelism.For(profile.GameDir), i =>
        {
            var rel = files[i];
            var abs = Path.Combine(profile.GameDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(gameSnap, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!(OperatingSystem.IsWindows() && NativeFiles.TryCreateHardLink(dst, abs, out _)))
            {
                File.Copy(abs, dst, overwrite: true);
            }
            entries[i] = Describe(abs, rel, isWindows);
        }, ct);

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

        var manifest = new SnapshotManifest
        {
            TxnId = txnId, Utc = DateTimeOffset.UtcNow, Files = entries.Where(e => e is not null).Select(e => e!).ToList(), CopiedStateFiles = copied,
            Rules = rules, RootFiles = roots.ToList(), Directories = directories,
        };
        // Written last: a snapshot folder without a manifest is a partial snapshot and is never restored from.
        LauncherJson.WriteFile(Path.Combine(snapDir, "manifest.json"), manifest);
        Debug.WriteLine($"snapshot {txnId}: {manifest.Files.Count} files in {sw.Elapsed}");
        return manifest;
    }

    /// <summary>Size, time and file id of the original, read through one attribute-only handle (the link shares them).</summary>
    private static SnapshotEntry Describe(string abs, string rel, bool isWindows)
    {
        try
        {
            if (isWindows && OperatingSystem.IsWindows() && NativeFiles.TryGetFileStat(abs, out var stat))
            {
                return new SnapshotEntry { Path = rel, Size = stat.Size, LastWriteTicks = stat.LastWriteTicks, FileId = stat.FileId };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no file id: a restore then compares size and time instead
        }
        var fi = new FileInfo(abs);
        return new SnapshotEntry { Path = rel, Size = fi.Length, LastWriteTicks = fi.LastWriteTimeUtc.Ticks, FileId = null };
    }

    public static SnapshotManifest? Read(ProfilePaths profile, string txnId)
    {
        var path = Path.Combine(profile.SnapshotsDir, txnId, "manifest.json");
        return File.Exists(path) ? LauncherJson.ReadFile<SnapshotManifest>(path, 256L * 1024 * 1024) : null;
    }

    /// <summary>Deletes a snapshot now (on several threads).</summary>
    public static void Delete(ProfilePaths profile, string txnId, CancellationToken ct = default)
    {
        var dir = Path.Combine(profile.SnapshotsDir, txnId);
        if (Directory.Exists(dir))
        {
            DeleteTree(dir, FileParallelism.For(dir), ct);
        }
    }

    /// <summary>
    /// Takes a snapshot out of use at once: renames it into the profile's trash folder, to be deleted by
    /// <see cref="EmptyTrash"/>. Only for a snapshot nothing can restore from any more (its transaction is committed or
    /// rolled back). When the rename is impossible (a file inside is open) the snapshot is deleted in place instead.
    /// </summary>
    public static void Discard(ProfilePaths profile, string txnId)
    {
        var dir = Path.Combine(profile.SnapshotsDir, txnId);
        if (!Directory.Exists(dir))
        {
            return;
        }
        var trash = TrashDir(profile);
        if (TrashIsUnsafe(profile, null))
        {
            Delete(profile, txnId); // never move anything into a trash folder that leads elsewhere
            return;
        }
        try
        {
            Directory.CreateDirectory(trash);
            Directory.Move(dir, Path.Combine(trash, txnId + "-" + Guid.NewGuid().ToString("N")[..8]));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fall through: delete in place (throws if that fails too, so the caller can warn)
        }
        Delete(profile, txnId);
    }

    /// <summary>True when discarded snapshots are waiting to be deleted.</summary>
    /// <param name="log">Told (once per run) when the trash folder is a junction or symbolic link and is left alone.</param>
    public static bool HasTrash(ProfilePaths profile, Action<string>? log = null)
        => Directory.Exists(TrashDir(profile)) && !TrashIsUnsafe(profile, log) && Directory.EnumerateFileSystemEntries(TrashDir(profile)).Any();

    private static int _warnedUnsafeTrash;

    /// <summary>
    /// A trash folder that is itself a junction or symbolic link points outside the profile: nothing is deleted through
    /// it or moved into it. Logged once per run.
    /// </summary>
    private static bool TrashIsUnsafe(ProfilePaths profile, Action<string>? log)
    {
        var trash = TrashDir(profile);
        if (!SafePath.IsReparsePoint(trash))
        {
            return false;
        }
        if (log is not null && Interlocked.Exchange(ref _warnedUnsafeTrash, 1) == 0)
        {
            log($"Warning: {trash} is a junction or symbolic link; old snapshots are not deleted through it. Remove that link to let the launcher clean up.");
        }
        return true;
    }

    /// <summary>
    /// Deletes discarded snapshots (after an operation, or left over by a crash or a closed app). Returns how many
    /// folders went. Deleting a snapshot link never touches the game's copy of the file: both are names of one file.
    /// </summary>
    public static int EmptyTrash(ProfilePaths profile, Action<string>? log = null, CancellationToken ct = default)
    {
        var trash = TrashDir(profile);
        if (!Directory.Exists(trash) || TrashIsUnsafe(profile, log))
        {
            return 0;
        }
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(trash).ToList())
        {
            ct.ThrowIfCancellationRequested();
            DeleteTree(dir, FileParallelism.For(dir), ct);
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Deletes a folder tree: its files on <paramref name="threads"/> threads, then the folders deepest first.
    /// Never descends into junctions or symbolic links (the link itself is removed, its target is left alone).
    /// </summary>
    internal static void DeleteTree(string root, int threads, CancellationToken ct)
    {
        if (SafePath.IsReparsePoint(root))
        {
            Directory.Delete(root);
            return;
        }
        var files = new List<string>();
        var dirs = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        // Attributes are never changed: a snapshot link shares them with the game's copy of the file.
        var walk = new FileSystemEnumerable<(string Path, bool IsDirectory)>(root, (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory), options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };
        foreach (var (path, isDirectory) in walk)
        {
            (isDirectory ? dirs : files).Add(path);
        }
        ParallelFiles.For(files.Count, threads, i => File.Delete(files[i]), ct);
        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();
            Directory.Delete(dir); // a junction is removed as a link; folders are empty by now
        }
        Directory.Delete(root);
    }
}
