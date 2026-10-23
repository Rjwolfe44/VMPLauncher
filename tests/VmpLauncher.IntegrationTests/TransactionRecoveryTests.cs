using System.IO;
using System.Text;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Transactions;
using VmpLauncher.IntegrationTests.Fixtures;
using Xunit;

namespace VmpLauncher.IntegrationTests;

/// <summary>Regression tests for commit/rollback/recover/repair edge cases.</summary>
[Collection("ckan")]
public class TransactionRecoveryTests
{
    private static readonly string[] Stock = { "GameData/Squad/Parts/stock.cfg", "GameData/SquadExpansion/MakingHistory/readme.txt", "GameData/SquadExpansion/Serenity/readme.txt" };
    private static readonly string[] StockRootFiles = { "KSP_x64.exe", "buildID64.txt", "readme.txt", "settings.cfg" };

    [Fact]
    public void Cleanup_failure_after_commit_keeps_the_committed_install()
    {
        using var w = new TestWorld("postcommit");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("F") });
        FileStream? blocker = null;
        // A file held open inside the snapshot folder makes deleting the snapshot fail after the commit.
        var ops = w.Ops(_ => blocker ??= new FileStream(Path.Combine(Directory.EnumerateDirectories(w.Profile.SnapshotsDir).Single(), "blocker.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None));
        try
        {
            var report = w.Worker.Run(() => ops.Install(w.Request(lockPath)));
            Assert.Contains(report.Warnings, x => x.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            blocker?.Dispose();
        }
        Assert.Equal(TxnState.Committed, TransactionJournal.ReadAll(w.Profile).Single().State);
        Assert.Equal("1", InstalledState.TryRead(w.Profile)!.PackVersion);
        Assert.True(File.Exists(w.GamePath("GameData/A/A.dll")));
        Assert.True(File.Exists(w.Profile.LockCopyFile));
        Assert.Null(TransactionJournal.FindIncomplete(w.Profile));
    }

    [Fact]
    public void Recover_closes_a_journal_interrupted_while_snapshotting()
    {
        using var w = new TestWorld("recover-snap");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A", "1.0"), w.Repo.Get("F") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        // A crash while linking the snapshot: journal at Snapshotting, snapshot folder without manifest.
        var txnId = TransactionJournal.NewId();
        var partial = Path.Combine(w.Profile.SnapshotsDir, txnId, "game", "GameData");
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "half.txt"), "x");
        new TransactionJournal { TxnId = txnId, Operation = "update", State = TxnState.Snapshotting, SnapshotDir = Path.Combine(w.Profile.SnapshotsDir, txnId) }.Write(w.Profile);
        Assert.NotNull(TransactionJournal.FindIncomplete(w.Profile));

        var rb = w.Worker.Run(() => w.Ops().Recover(w.Profile));
        Assert.NotNull(rb);
        Assert.Empty(rb!.Errors);
        Assert.Null(TransactionJournal.FindIncomplete(w.Profile));
        Assert.False(Directory.Exists(Path.Combine(w.Profile.SnapshotsDir, txnId)));
        Assert.True(File.Exists(w.GamePath("GameData/A/A.dll")));
        // The profile is usable again.
        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", new[] { w.Repo.Get("A", "1.1"), w.Repo.Get("F") });
        w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        Assert.Equal("2", InstalledState.TryRead(w.Profile)!.PackVersion);
    }

    [Fact]
    public void Recover_without_a_snapshot_does_not_block_the_profile_forever()
    {
        using var w = new TestWorld("recover-nosnap");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        var txnId = TransactionJournal.NewId();
        new TransactionJournal { TxnId = txnId, Operation = "update", State = TxnState.RollingBack, SnapshotDir = Path.Combine(w.Profile.SnapshotsDir, txnId) }.Write(w.Profile);

        var rb = w.Worker.Run(() => w.Ops().Recover(w.Profile));
        Assert.NotNull(rb);
        Assert.Single(rb!.Errors);
        Assert.Null(TransactionJournal.FindIncomplete(w.Profile));
        Assert.Null(w.Worker.Run(() => w.Ops().Recover(w.Profile)));
    }

    [Fact]
    public void Recover_retries_a_failed_rollback_once_then_unblocks_the_profile()
    {
        using var w = new TestWorld("recover-retry");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("F") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        // A snapshot that has lost one of its files: every restore reports that error.
        var txnId = TransactionJournal.NewId();
        SnapshotEngine.Create(w.Profile, FileRules.Default, txnId, null);
        File.Delete(Path.Combine(w.Profile.SnapshotsDir, txnId, "game", "GameData", "F", "F.cfg"));
        new TransactionJournal { TxnId = txnId, Operation = "update", State = TxnState.RollbackFailed, SnapshotDir = Path.Combine(w.Profile.SnapshotsDir, txnId) }.Write(w.Profile);

        var rb = w.Worker.Run(() => w.Ops().Recover(w.Profile));
        Assert.Contains(rb!.Errors, e => e.Contains("F.cfg", StringComparison.Ordinal));
        Assert.Null(TransactionJournal.FindIncomplete(w.Profile));
        Assert.True(Directory.Exists(Path.Combine(w.Profile.SnapshotsDir, txnId)), "the snapshot is kept for manual recovery");
        Assert.True(File.Exists(w.GamePath("GameData/F/F.cfg")));
    }

    [Fact]
    public void Update_dropping_replace_and_disable_overrides_restores_the_owners_files()
    {
        using var w = new TestWorld("ovr-drop");
        var mods = new[] { w.Repo.Get("A"), w.Repo.Get("B"), w.Repo.Get("D"), w.Repo.Get("E") };
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", mods, new[] { ReplaceBcfg(w, "B = replaced\n"), DisableEdll(w, "E") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        Assert.False(File.Exists(w.GamePath("GameData/E/E.dll")));

        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", mods);
        w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        Assert.StartsWith("B = 1", File.ReadAllText(w.GamePath("GameData/B/B.cfg")), StringComparison.Ordinal);
        Assert.True(File.Exists(w.GamePath("GameData/E/E.dll")));
        Assert.False(File.Exists(w.GamePath("GameData/E/E.dll.disabled")));
        Assert.Equal("2", InstalledState.TryRead(w.Profile)!.PackVersion);
    }

    [Fact]
    public void Update_changing_a_replace_override_applies_the_new_content()
    {
        using var w = new TestWorld("ovr-change");
        var mods = new[] { w.Repo.Get("A"), w.Repo.Get("B") };
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", mods, new[] { ReplaceBcfg(w, "B = replaced\n") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", mods, new[] { ReplaceBcfg(w, "B = replaced again\n") });
        w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        Assert.Equal("B = replaced again\n", File.ReadAllText(w.GamePath("GameData/B/B.cfg")));
    }

    [Fact]
    public void Disable_override_naming_the_wrong_owner_is_rejected_and_rolled_back()
    {
        using var w = new TestWorld("ovr-owner");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("D"), w.Repo.Get("E") }, new[] { DisableEdll(w, "D") });
        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Install(w.Request(lockPath))));
        Assert.Equal(ErrorCode.InvalidLock, ex.Code);
        Assert.Equal(Stock, w.GameDataFiles());
        Assert.Equal(TxnState.RolledBack, TransactionJournal.ReadAll(w.Profile).Single().State);
    }

    [Fact]
    public void Failed_update_quarantines_root_files_it_installed_and_retry_succeeds()
    {
        using var w = new TestWorld("root-new");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A", "1.0") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        var mods = new[] { w.Repo.Get("A", "1.1"), w.Repo.Get("Root") };
        // Fail after CKAN committed its install (inside CKAN's own transaction it removes new files itself):
        // an add override whose inline content does not match its hash.
        var bad = new LockOverride { Id = "ovr-bad", Op = "add", Path = "GameData/Bad/bad.cfg", Sha256 = new string('0', 64), Size = 3, Content = new OverrideContent { InlineBase64 = Convert.ToBase64String("bad"u8.ToArray()) } };
        var v2Bad = w.Repo.WriteLock(w.LockDir("v2bad"), "2", mods, new[] { bad }, rootInstallOwners: new[] { "Root" });
        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Update(w.Request(v2Bad))));
        Assert.Equal(ErrorCode.Tampered, ex.Code);
        Assert.False(File.Exists(w.GamePath("rootfile.txt")));
        Assert.Single(Directory.EnumerateFiles(w.Profile.QuarantineDir, "rootfile.txt", SearchOption.AllDirectories));
        Assert.All(StockRootFiles, f => Assert.True(File.Exists(w.GamePath(f)), f));
        Assert.Equal(TxnState.RolledBack, TransactionJournal.ReadAll(w.Profile).Last().State);
        Assert.StartsWith("A-DLL-1.0", File.ReadAllText(w.GamePath("GameData/A/A.dll")), StringComparison.Ordinal);

        // Without the orphaned root file CKAN can install Root again.
        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", mods, rootInstallOwners: new[] { "Root" });
        w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        Assert.True(File.Exists(w.GamePath("rootfile.txt")));
    }

    [Fact]
    public void Failed_update_restores_root_files_of_a_removed_module()
    {
        using var w = new TestWorld("root-removed");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A", "1.0"), w.Repo.Get("Root") }, rootInstallOwners: new[] { "Root" });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        Assert.True(File.Exists(w.GamePath("rootfile.txt")));
        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", new[] { w.Repo.Get("A", "1.1") });
        var ops = w.Ops(_ => throw new IOException("simulated disk full"));
        Assert.Throws<IOException>(() => w.Worker.Run(() => ops.Update(w.Request(v2))));
        Assert.True(File.Exists(w.GamePath("rootfile.txt")));
        Assert.All(StockRootFiles, f => Assert.True(File.Exists(w.GamePath(f)), f));
        // The restored profile still verifies and repairs as nothing to do.
        Assert.Null(w.Worker.Run(() => w.Ops().Repair(w.Profile, w.CacheDir, quarantineUnknownDlls: false)).TxnId);
    }

    [Fact]
    public void Failed_update_keeps_empty_directories_ckan_registered()
    {
        using var w = new TestWorld("emptydir");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A", "1.0"), w.Repo.Get("Dirs") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        Assert.True(Directory.Exists(w.GamePath("GameData/Dirs/Empty")));
        Assert.Contains("GameData/Dirs/Empty", File.ReadAllText(Path.Combine(w.Profile.CkanDir, "registry.json")), StringComparison.Ordinal);

        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", new[] { w.Repo.Get("A", "1.1"), w.Repo.Get("Dirs") });
        var ops = w.Ops(_ => throw new IOException("simulated disk full"));
        Assert.Throws<IOException>(() => w.Worker.Run(() => ops.Update(w.Request(v2))));
        Assert.True(Directory.Exists(w.GamePath("GameData/Dirs/Empty")));

        // The registry restored from the snapshot still lists the directory; the retry must pass its registry check.
        w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        Assert.Equal("2", InstalledState.TryRead(w.Profile)!.PackVersion);
    }

    [Fact]
    public void Update_reinstalls_pinned_auto_installed_modules_ckan_removed()
    {
        using var w = new TestWorld("autoremove");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B") }, autoInstalled: new[] { "A" });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        // B goes; A stays pinned (auto-installed) although nothing depends on it any more.
        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", new[] { w.Repo.Get("A"), w.Repo.Get("F") }, autoInstalled: new[] { "A" });
        w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        Assert.Contains("pinned modules CKAN removed: A", w.Log.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(w.GamePath("GameData/A/A.dll")));
        Assert.True(File.Exists(w.GamePath("GameData/F/F.cfg")));
        Assert.False(Directory.Exists(w.GamePath("GameData/B")));
        Assert.Equal("2", InstalledState.TryRead(w.Profile)!.PackVersion);
    }

    [Fact]
    public void Repair_restores_replaced_disabled_and_added_override_files()
    {
        using var w = new TestWorld("repair-ovr");
        var addBytes = Encoding.UTF8.GetBytes("SigmaReplacements {}\n");
        var overrides = new[]
        {
            new LockOverride { Id = "ovr-add", Op = "add", Path = "GameData/Sigma2/Settings.cfg", Sha256 = Sha256Util.HashBytes(addBytes), Size = addBytes.Length, Content = new OverrideContent { InlineBase64 = Convert.ToBase64String(addBytes) } },
            ReplaceBcfg(w, "B = replaced\n"),
            DisableEdll(w, "E"),
        };
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B"), w.Repo.Get("D"), w.Repo.Get("E") }, overrides);
        w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        File.WriteAllText(w.GamePath("GameData/B/B.cfg"), "tampered");
        File.Delete(w.GamePath("GameData/E/E.dll.disabled"));
        File.Delete(w.GamePath("GameData/Sigma2/Settings.cfg"));

        var report = w.Worker.Run(() => w.Ops().Repair(w.Profile, w.CacheDir, quarantineUnknownDlls: false));
        Assert.NotNull(report.TxnId);
        Assert.Equal(3, report.OverridesReapplied);
        Assert.Equal("B = replaced\n", File.ReadAllText(w.GamePath("GameData/B/B.cfg")));
        Assert.StartsWith("E-DLL", File.ReadAllText(w.GamePath("GameData/E/E.dll.disabled")), StringComparison.Ordinal);
        Assert.False(File.Exists(w.GamePath("GameData/E/E.dll")));
        Assert.Equal("SigmaReplacements {}\n", File.ReadAllText(w.GamePath("GameData/Sigma2/Settings.cfg")));
        Assert.Equal(TxnState.Committed, TransactionJournal.ReadAll(w.Profile).Last().State);
        Assert.Null(w.Worker.Run(() => w.Ops().Repair(w.Profile, w.CacheDir, quarantineUnknownDlls: false)).TxnId);
    }

    [Fact]
    public void Repair_uses_provided_private_archives_and_asks_for_missing_ones()
    {
        using var w = new TestWorld("repair-private");
        var f = w.Repo.Get("F");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), f }, privateIds: new[] { "F" });
        var sideload = new Dictionary<string, string> { ["F"] = f.ZipPath };
        w.Worker.Run(() => w.Ops().Install(w.Request(lockPath, sideload: sideload)));
        File.Delete(w.GamePath("GameData/F/F.cfg"));
        foreach (var cached in Directory.EnumerateFiles(w.CacheDir, "*F-1.0.zip", SearchOption.AllDirectories).ToList())
        {
            File.Delete(cached);
        }
        w.Server.ClearRequests();

        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Repair(w.Profile, w.CacheDir, quarantineUnknownDlls: false)));
        Assert.Equal(ErrorCode.PrivateArtifactNeeded, ex.Code);
        Assert.Null(TransactionJournal.FindIncomplete(w.Profile));
        Assert.Empty(w.Server.RequestsFor("F-1.0.zip"));

        var report = w.Worker.Run(() => w.Ops().Repair(w.Profile, w.CacheDir, quarantineUnknownDlls: false, sideloadFiles: sideload));
        Assert.Equal(1, report.FilesRestored);
        Assert.True(File.Exists(w.GamePath("GameData/F/F.cfg")));
    }

    private static LockOverride ReplaceBcfg(TestWorld w, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new LockOverride
        {
            Id = "ovr-replace", Op = "replace", Path = "GameData/B/B.cfg", Owner = "B", BaseSha256 = ArchiveEntrySha(w.Repo.Get("B").ZipPath, "GameData/B/B.cfg"),
            Sha256 = Sha256Util.HashBytes(bytes), Size = bytes.Length, Content = new OverrideContent { InlineBase64 = Convert.ToBase64String(bytes) },
        };
    }

    private static LockOverride DisableEdll(TestWorld w, string owner)
        => new() { Id = "ovr-disable", Op = "disable", Path = "GameData/E/E.dll", Owner = owner, BaseSha256 = ArchiveEntrySha(w.Repo.Get("E").ZipPath, "GameData/E/E.dll"), RenameTo = "E.dll.disabled" };

    private static string ArchiveEntrySha(string zip, string entry)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        using var s = z.GetEntry(entry)!.Open();
        return Sha256Util.HashStream(s);
    }
}
