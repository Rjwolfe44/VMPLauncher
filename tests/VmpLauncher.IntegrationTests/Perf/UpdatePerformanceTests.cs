using System.Diagnostics;
using System.IO;
using VmpLauncher.Core.Profiles;
using VmpLauncher.IntegrationTests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VmpLauncher.IntegrationTests.Perf;

/// <summary>Runs only when VMPL_PERF_ROOT names a scratch folder (several GB, on the drive the profiles live on).</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VMPL_PERF_ROOT")))
        {
            Skip = "Performance harness: set VMPL_PERF_ROOT to a scratch folder to run it.";
        }
    }
}

/// <summary>
/// Measures install and update phases on a profile shaped like a real one (tens of thousands of files, GBs of
/// textures) through the real ProfileOperations and CKAN. Prints every phase line and appends them to
/// VMPL_PERF_ROOT\results.txt under VMPL_PERF_LABEL. Knobs: VMPL_PERF_FILES, VMPL_PERF_MODULES, VMPL_PERF_BIG_MB,
/// VMPL_PERF_UPDATES.
/// </summary>
[Collection("ckan")]
public class UpdatePerformanceTests(ITestOutputHelper output)
{
    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    [PerfFact]
    public void Update_phase_timings_on_a_realistic_profile()
    {
        var root = Environment.GetEnvironmentVariable("VMPL_PERF_ROOT")!;
        var label = Environment.GetEnvironmentVariable("VMPL_PERF_LABEL") ?? "run";
        var spec = new PerfUniverse.Spec(Env("VMPL_PERF_MODULES", 210), Env("VMPL_PERF_FILES", 28_000), Env("VMPL_PERF_BIG_MB", 3_000) * 1024L * 1024, BigFiles: Env("VMPL_PERF_BIG_FILES", 40), ChangedModules: 4,
            PeDir: Environment.GetEnvironmentVariable("VMPL_PERF_PE_DIR"));
        var sw = Stopwatch.StartNew();
        var universe = new PerfUniverse(Path.Combine(root, "universe"), spec);
        var lines = new List<string> { $"== {label} {DateTime.Now:u} {spec}; universe ready in {sw.Elapsed.TotalSeconds:0.0} s" };

        using var w = new TestWorld("perf");
        var served = Path.Combine(w.Root, "archives");
        foreach (var zip in universe.ArchiveFiles)
        {
            NativeFiles.CreateHardLink(Path.Combine(served, Path.GetFileName(zip)), zip);
        }
        sw.Restart();
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", universe.Modules(w.Server.BaseUrl, v2: false));
        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", universe.Modules(w.Server.BaseUrl, v2: true));
        lines.Add($"locks written in {sw.Elapsed.TotalSeconds:0.0} s");

        void Run(string what, Action action)
        {
            w.Log.GetStringBuilder().Clear();
            var t = Stopwatch.StartNew();
            action();
            lines.Add($"-- {what}: {t.Elapsed.TotalSeconds:0.0} s");
            lines.AddRange(w.Log.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains(" phase", StringComparison.Ordinal) || l.StartsWith("Update plan", StringComparison.Ordinal)
                || l.StartsWith("Snapshot:", StringComparison.Ordinal) || l.StartsWith("CKAN apply finished", StringComparison.Ordinal) || l.StartsWith("Verified ", StringComparison.Ordinal)).Select(l => "   " + l));
        }

        Run("install v1", () => w.Worker.Run(() => w.Ops().Install(w.Request(v1))));
        var updates = Env("VMPL_PERF_UPDATES", 3);
        for (var i = 0; i < updates; i++)
        {
            var target = i % 2 == 0 ? v2 : v1;
            // As the app does it: the snapshot goes to the trash at the commit and is deleted after the update finished.
            Run($"update {i + 1} to v{(i % 2 == 0 ? 2 : 1)}", () => w.Worker.Run(() => w.Ops().Update(w.Request(target) with { DeferSnapshotDeletion = true })));
            Run("   deferred snapshot deletion (after the update reported finished)", () => w.Worker.Run(() => w.Ops().EmptyTrash(w.Profile)));
        }
        if (Environment.GetEnvironmentVariable("VMPL_PERF_BREAKDOWN") == "1")
        {
            Breakdown(w.Profile, lines);
        }
        // Scaling of the per-file phases with the thread count, on the installed profile (warm cache).
        var dops = (Environment.GetEnvironmentVariable("VMPL_PERF_DOPS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        if (dops.Count > 0)
        {
            var lockDoc = Core.Serialization.LauncherJson.ReadFile<Core.Locking.PackLock>(w.Profile.LockCopyFile);
            var inventory = Core.Inventory.InventoryFile.Read(w.Profile.InventoryCopyFile, lockDoc.Inventory.Sha256);
            var state = InstalledState.TryRead(w.Profile)!;
            lines.Add("-- scaling (threads: snapshot create / delete / verify incremental / verify full)");
            try
            {
                foreach (var dop in dops)
                {
                    Core.Transactions.FileParallelism.Override = dop;
                    var txn = "perf-" + dop;
                    var t = Stopwatch.StartNew();
                    var m = Core.Transactions.SnapshotEngine.Create(w.Profile, lockDoc.FileRules, txn, null);
                    var create = t.Elapsed.TotalSeconds;
                    t.Restart();
                    Core.Transactions.SnapshotEngine.Delete(w.Profile, txn);
                    var delete = t.Elapsed.TotalSeconds;
                    t.Restart();
                    var inc = new Core.Transactions.InstallVerifier().Verify(w.Profile, inventory, lockDoc.FileRules, Core.Transactions.InstallVerifier.Mode.Incremental, state);
                    var incremental = t.Elapsed.TotalSeconds;
                    t.Restart();
                    var full = new Core.Transactions.InstallVerifier().Verify(w.Profile, inventory, lockDoc.FileRules, Core.Transactions.InstallVerifier.Mode.Full, null);
                    var fullS = t.Elapsed.TotalSeconds;
                    lines.Add($"   {dop,2} threads: {create,6:0.00} s / {delete,6:0.00} s / {incremental,6:0.00} s ({inc.FilesHashed} hashed) / {fullS,6:0.00} s  ({m.Files.Count:N0} files, full clean={full.IsClean})");
                }
            }
            finally
            {
                Core.Transactions.FileParallelism.Override = null;
            }
        }
        foreach (var l in lines)
        {
            output.WriteLine(l);
        }
        File.AppendAllLines(Path.Combine(root, "results.txt"), lines);
    }

    /// <summary>What one snapshot costs per kind of file-system call, at the adaptive thread count.</summary>
    private static void Breakdown(ProfilePaths profile, List<string> lines)
    {
        var threads = Core.Transactions.FileParallelism.For(profile.GameDir);
        var gameData = Path.Combine(profile.GameDir, "GameData");
        var t = Stopwatch.StartNew();
        var files = Directory.EnumerateFiles(gameData, "*", SearchOption.AllDirectories).ToList();
        var dirs = Directory.EnumerateDirectories(gameData, "*", SearchOption.AllDirectories).ToList();
        var walk = t.Elapsed.TotalSeconds;
        var mirror = Path.Combine(profile.Root, "breakdown");
        t.Restart();
        foreach (var d in dirs)
        {
            Directory.CreateDirectory(Path.Combine(mirror, Path.GetRelativePath(gameData, d)));
        }
        var mkdirs = t.Elapsed.TotalSeconds;
        double Par(Action<int> body)
        {
            var sw = Stopwatch.StartNew();
            Core.Transactions.ParallelFiles.For(files.Count, threads, body, CancellationToken.None);
            return sw.Elapsed.TotalSeconds;
        }
        var links = Par(i => NativeFiles.CreateHardLink(Path.Combine(mirror, Path.GetRelativePath(gameData, files[i])), files[i]));
        var attrOpen = Par(i => NativeFiles.TryGetFileStat(files[i], out _));
        var readOpen = Par(i =>
        {
            using var h = File.OpenHandle(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        });
        var fileInfo = Par(i => _ = new FileInfo(files[i]).Length);
        t.Restart();
        Directory.Delete(mirror, recursive: true);
        var deleteOld = t.Elapsed.TotalSeconds;
        lines.Add($"-- breakdown at {threads} threads, {files.Count:N0} files, {dirs.Count:N0} folders: walk {walk:0.00} s, mkdirs (1 thread) {mkdirs:0.00} s, " +
                  $"hard links {links:0.00} s, attribute-only open+stat {attrOpen:0.00} s, read open {readOpen:0.00} s, FileInfo {fileInfo:0.00} s, recursive delete (1 thread) {deleteOld:0.00} s");
    }
}
