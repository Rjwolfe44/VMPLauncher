using System.IO;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Transactions;
using VmpLauncher.IntegrationTests.Fixtures;
using VmpLauncher.IntegrationTests.Perf;
using Xunit;

namespace VmpLauncher.IntegrationTests;

/// <summary>
/// The guarantees the faster update path must keep: multi-threaded snapshots still restore exactly, a cancel or failure
/// at any phase leaves either the old or the new pack (never a mix) with no open journal, discarded snapshots are
/// cleaned up after a crash, and the incremental final check still catches a swapped file.
/// </summary>
[Collection("ckan")]
public class UpdateSpeedSafetyTests
{
    /// <summary>Hundreds of files across modules, so the per-file work really runs on several threads.</summary>
    private static readonly PerfUniverse.Spec Small = new(Modules: 8, Files: 480, BigBytes: 0, BigFiles: 0, ChangedModules: 4);

    private sealed class Pack
    {
        public required string V1 { get; init; }
        public required string V2 { get; init; }
    }

    private static Pack MakePack(TestWorld w)
    {
        var universe = new PerfUniverse(Path.Combine(w.Root, "universe"), Small);
        foreach (var zip in universe.ArchiveFiles)
        {
            File.Copy(zip, Path.Combine(w.Root, "archives", Path.GetFileName(zip)));
        }
        return new Pack
        {
            V1 = w.Repo.WriteLock(w.LockDir("v1"), "1", universe.Modules(w.Server.BaseUrl, v2: false)),
            V2 = w.Repo.WriteLock(w.LockDir("v2"), "2", universe.Modules(w.Server.BaseUrl, v2: true)),
        };
    }

    /// <summary>Every file under GameData plus the CKAN registry and launcher state, path → content hash.</summary>
    private static SortedDictionary<string, string> Image(TestWorld w)
    {
        var image = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(w.Profile.GameDir, "GameData"), "*", SearchOption.AllDirectories))
        {
            image[Path.GetRelativePath(w.Profile.GameDir, f).Replace('\\', '/')] = Sha256Util.HashFile(f);
        }
        foreach (var f in new[] { Path.Combine(w.Profile.CkanDir, "registry.json"), w.Profile.InstalledStateFile, w.Profile.LockCopyFile, w.Profile.InventoryCopyFile })
        {
            image["state:" + Path.GetFileName(f)] = Sha256Util.HashFile(f);
        }
        return image;
    }

    private static void AssertClosedAndClean(TestWorld w)
    {
        Assert.Null(TransactionJournal.FindIncomplete(w.Profile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(w.Profile.SnapshotsDir));
        Assert.False(SnapshotEngine.HasTrash(w.Profile));
    }

    private static T WithThreads<T>(int threads, Func<T> body)
    {
        FileParallelism.Override = threads;
        try
        {
            return body();
        }
        finally
        {
            FileParallelism.Override = null;
        }
    }

    [Fact]
    public void Failure_midway_through_an_update_with_parallel_snapshot_restores_every_file_and_removes_every_created_one()
    {
        using var w = new TestWorld("par-fail");
        var pack = MakePack(w);
        w.Worker.Run(() => w.Ops().Install(w.Request(pack.V1)));
        var before = Image(w);

        var completed = 0;
        // After two of the four changed modules: their old versions are gone, two new ones are in, two are missing.
        var ops = w.Ops(_ =>
        {
            if (Interlocked.Increment(ref completed) == 2)
            {
                throw new IOException("simulated failure in the middle of the update");
            }
        });
        var ex = Assert.Throws<IOException>(() => WithThreads(8, () => w.Worker.Run(() => ops.Update(w.Request(pack.V2)))));
        Assert.Contains("simulated", ex.Message, StringComparison.Ordinal);

        Assert.Equal(before, Image(w));
        Assert.Equal(TxnState.RolledBack, TransactionJournal.ReadAll(w.Profile)[^1].State);
        AssertClosedAndClean(w);
        Assert.True(w.SentinelIntact());

        // And the profile still updates afterwards.
        WithThreads(8, () => w.Worker.Run(() => w.Ops().Update(w.Request(pack.V2))));
        Assert.Equal("2", InstalledState.TryRead(w.Profile)!.PackVersion);
    }

    /// <summary>Synchronous progress sink: cancels the moment a status line that starts a given phase is reported.</summary>
    private sealed class CancelOn(string prefix, CancellationTokenSource cts) : IProgress<CkanProgress>
    {
        public bool Seen { get; private set; }

        public void Report(CkanProgress value)
        {
            if (!Seen && value.Message.StartsWith(prefix, StringComparison.Ordinal))
            {
                Seen = true;
                cts.Cancel();
            }
        }
    }

    [Theory]
    [InlineData("Loading the CKAN registry")]
    [InlineData("Taking a snapshot")]
    [InlineData("Installing ")]
    [InlineData("Applying the pack's file overrides")]
    [InlineData("Updating the CKAN registry")]
    [InlineData("Checking the CKAN registry")]
    [InlineData("Verifying files")]
    [InlineData("Saving the new install state")]
    [InlineData("Cleaning up")]
    public void Cancel_at_any_phase_leaves_exactly_the_old_or_the_new_pack(string phase)
    {
        using var w = new TestWorld("par-cancel");
        var pack = MakePack(w);
        w.Worker.Run(() => w.Ops().Install(w.Request(pack.V1)));
        var v1Image = Image(w);

        using var cts = new CancellationTokenSource();
        var progress = new CancelOn(phase, cts);
        var ops = new ProfileOperations(w.Paths, w.Log, progress);
        Exception? error = null;
        try
        {
            WithThreads(8, () => w.Worker.Run(() => ops.Update(w.Request(pack.V2), cts.Token)));
        }
        catch (OperationCanceledException e)
        {
            error = e;
        }
        Assert.True(progress.Seen, $"phase '{phase}' was never reported");
        AssertClosedAndClean(w);

        var image = Image(w);
        if (error is not null)
        {
            Assert.Equal(v1Image, image);
            Assert.Equal("1", InstalledState.TryRead(w.Profile)!.PackVersion);
        }
        else
        {
            // Past the commit a cancel no longer undoes anything: the new pack is complete.
            Assert.Equal("2", InstalledState.TryRead(w.Profile)!.PackVersion);
            var clean = new InstallVerifier().Verify(w.Profile, Core.Inventory.InventoryFile.Read(w.Profile.InventoryCopyFile,
                Core.Serialization.LauncherJson.ReadFile<Core.Locking.PackLock>(w.Profile.LockCopyFile).Inventory.Sha256),
                Core.Serialization.LauncherJson.ReadFile<Core.Locking.PackLock>(w.Profile.LockCopyFile).FileRules, InstallVerifier.Mode.Full);
            Assert.True(clean.IsClean);
        }
    }

    [Fact]
    public void Snapshot_left_in_the_trash_by_a_crash_after_commit_is_deleted_at_next_start_without_touching_game_files()
    {
        using var w = new TestWorld("trash");
        var pack = MakePack(w);
        w.Worker.Run(() => w.Ops().Install(w.Request(pack.V1)));
        // A deferred deletion that never ran (the app closed or crashed right after the update finished).
        var report = w.Worker.Run(() => w.Ops().Update(w.Request(pack.V2) with { DeferSnapshotDeletion = true }));
        Assert.DoesNotContain(report.Warnings, x => x.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TxnState.Committed, TransactionJournal.ReadAll(w.Profile)[^1].State);
        Assert.True(SnapshotEngine.HasTrash(w.Profile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(w.Profile.SnapshotsDir));
        var v2Image = Image(w);
        // A crash in the middle of an earlier deletion leaves a half-deleted folder too.
        var half = Path.Combine(SnapshotEngine.TrashDir(w.Profile), "20000101-000000-abcdef-12345678", "game", "GameData", "X");
        Directory.CreateDirectory(half);
        File.WriteAllText(Path.Combine(half, "left.cfg"), "x");

        // Next start: recovery (nothing to restore) empties the trash.
        Assert.Null(w.Worker.Run(() => w.Ops().Recover(w.Profile)));
        Assert.False(SnapshotEngine.HasTrash(w.Profile));
        Assert.Equal(v2Image, Image(w));
        var state = InstalledState.TryRead(w.Profile)!;
        Assert.Equal("2", state.PackVersion);

        // The next transaction cleans leftovers too.
        w.Worker.Run(() => w.Ops().Update(w.Request(pack.V1) with { DeferSnapshotDeletion = true }));
        Assert.True(SnapshotEngine.HasTrash(w.Profile));
        w.Worker.Run(() => w.Ops().Update(w.Request(pack.V2)));
        Assert.False(SnapshotEngine.HasTrash(w.Profile));
        Assert.Equal(v2Image.Where(kv => !kv.Key.StartsWith("state:", StringComparison.Ordinal)), Image(w).Where(kv => !kv.Key.StartsWith("state:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Incremental_final_check_catches_a_pack_file_swapped_for_another_with_the_same_size_and_time()
    {
        using var w = new TestWorld("swap");
        var pack = MakePack(w);
        w.Worker.Run(() => w.Ops().Install(w.Request(pack.V1)));
        // A file of a module the update does not touch, replaced by different bytes with the same size and timestamp
        // (a copy that preserves times). Only its NTFS file id tells it apart from the file hashed at install.
        var victim = Directory.EnumerateFiles(w.GamePath("GameData/PerfMod000"), "*.cfg", SearchOption.AllDirectories).First();
        var original = File.ReadAllBytes(victim);
        var time = File.GetLastWriteTimeUtc(victim);
        var forged = (byte[])original.Clone();
        forged[^1] ^= 0xFF;
        var tmp = victim + ".tmp";
        File.WriteAllBytes(tmp, forged);
        File.SetLastWriteTimeUtc(tmp, time);
        File.Move(tmp, victim, overwrite: true);
        var swappedImage = Image(w);

        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Update(w.Request(pack.V2))));
        Assert.Equal(ErrorCode.VerifyFailed, ex.Code);
        var rel = Path.GetRelativePath(w.Profile.GameDir, victim).Replace('\\', '/');
        Assert.Contains(ex.Details, d => d.Contains(rel, StringComparison.Ordinal));
        // Rolled back to the state before the update, swapped file included.
        Assert.Equal(swappedImage, Image(w));
        AssertClosedAndClean(w);
    }

    /// <summary>Changes one byte of a file in place and puts its time back: same size, same time, same NTFS file id.</summary>
    private static string EditInPlaceKeepingSizeAndTime(TestWorld w, string module)
    {
        var victim = Directory.EnumerateFiles(w.GamePath($"GameData/{module}"), "*.cfg", SearchOption.AllDirectories).First();
        var time = File.GetLastWriteTimeUtc(victim);
        var id = NativeFiles.FileIdString(victim);
        using (var fs = new FileStream(victim, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(-1, SeekOrigin.End);
            var b = fs.ReadByte();
            fs.Seek(-1, SeekOrigin.End);
            fs.WriteByte((byte)(b ^ 0xFF));
        }
        File.SetLastWriteTimeUtc(victim, time);
        Assert.Equal(id, NativeFiles.FileIdString(victim));
        return Path.GetRelativePath(w.Profile.GameDir, victim).Replace('\\', '/');
    }

    private static void AssertUpdateFailsOn(TestWorld w, Pack pack, string rel)
    {
        var before = Image(w);
        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Update(w.Request(pack.V2))));
        Assert.Equal(ErrorCode.VerifyFailed, ex.Code);
        Assert.Contains(ex.Details, d => d.Contains(rel, StringComparison.Ordinal));
        Assert.Equal(before, Image(w));
        AssertClosedAndClean(w);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("older than 30 days")]
    [InlineData("10 updates ago")]
    public void Update_hashes_every_file_again_when_the_last_full_check_is_missing_old_or_many_updates_ago(string stamp)
    {
        using var w = new TestWorld("full-due");
        var pack = MakePack(w);
        w.Worker.Run(() => w.Ops().Install(w.Request(pack.V1)));
        var installed = InstalledState.TryRead(w.Profile)!;
        Assert.NotNull(installed.LastFullCheckUtc);
        Assert.Equal(0, installed.UpdatesSinceFullCheck);
        // Bit rot or an in-place rewrite: invisible to size, time and file id, so only a full hash finds it.
        var rel = EditInPlaceKeepingSizeAndTime(w, "PerfMod000");
        (stamp switch
        {
            "missing" => installed with { LastFullCheckUtc = null },
            "older than 30 days" => installed with { LastFullCheckUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(31) },
            _ => installed with { UpdatesSinceFullCheck = InstalledState.FullCheckEveryUpdates },
        }).Write(w.Profile);

        AssertUpdateFailsOn(w, pack, rel);
    }

    [Fact]
    public void Incremental_updates_are_counted_and_a_full_check_resets_the_count()
    {
        using var w = new TestWorld("full-count");
        var pack = MakePack(w);
        w.Worker.Run(() => w.Ops().Install(w.Request(pack.V1)));
        var installedAt = InstalledState.TryRead(w.Profile)!.LastFullCheckUtc;
        w.Worker.Run(() => w.Ops().Update(w.Request(pack.V2)));
        var state = InstalledState.TryRead(w.Profile)!;
        Assert.Equal(1, state.UpdatesSinceFullCheck);
        Assert.Equal(installedAt, state.LastFullCheckUtc);
        Assert.Contains("hashing every file that changed", w.Log.ToString(), StringComparison.Ordinal);

        (state with { UpdatesSinceFullCheck = InstalledState.FullCheckEveryUpdates }).Write(w.Profile);
        w.Log.GetStringBuilder().Clear();
        w.Worker.Run(() => w.Ops().Update(w.Request(pack.V1)));
        state = InstalledState.TryRead(w.Profile)!;
        Assert.Equal(0, state.UpdatesSinceFullCheck);
        Assert.True(state.LastFullCheckUtc > installedAt);
        Assert.Contains("full hash", w.Log.ToString(), StringComparison.Ordinal);

        // A Repair that finds nothing to fix still hashed everything: it counts as the full check.
        (state with { LastFullCheckUtc = null, UpdatesSinceFullCheck = 5 }).Write(w.Profile);
        w.Worker.Run(() => w.Ops().Repair(w.Profile, w.CacheDir, quarantineUnknownDlls: false));
        state = InstalledState.TryRead(w.Profile)!;
        Assert.NotNull(state.LastFullCheckUtc);
        Assert.Equal(0, state.UpdatesSinceFullCheck);
    }

    [Fact]
    public void Update_check_hashes_a_file_whose_record_has_no_file_id()
    {
        using var w = new TestWorld("no-id");
        var pack = MakePack(w);
        w.Worker.Run(() => w.Ops().Install(w.Request(pack.V1)));
        var installed = InstalledState.TryRead(w.Profile)!;
        // Full check fresh, so the update runs incrementally; without a recorded file id nothing proves "same file".
        (installed with { Files = installed.Files.Select(f => f with { FileId = null }).ToList() }).Write(w.Profile);
        var rel = EditInPlaceKeepingSizeAndTime(w, "PerfMod000");

        AssertUpdateFailsOn(w, pack, rel);
    }

    [Fact]
    public void Recover_of_a_transaction_stuck_while_applying_never_trashes_its_snapshot()
    {
        using var w = new TestWorld("recover-trash");
        var pack = MakePack(w);
        w.Worker.Run(() => w.Ops().Install(w.Request(pack.V1)));
        var before = Image(w);
        var lockDoc = Core.Serialization.LauncherJson.ReadFile<Core.Locking.PackLock>(w.Profile.LockCopyFile);
        // A crash in the middle of applying: snapshot taken, a pack file deleted, a new one created, journal at Applying.
        var txnId = TransactionJournal.NewId();
        SnapshotEngine.Create(w.Profile, lockDoc.FileRules, txnId, null);
        new TransactionJournal { TxnId = txnId, Operation = "update", State = TxnState.Applying, SnapshotDir = Path.Combine(w.Profile.SnapshotsDir, txnId) }.Write(w.Profile);
        var gone = Directory.EnumerateFiles(w.GamePath("GameData/PerfMod001"), "*.cfg", SearchOption.AllDirectories).First();
        File.Delete(gone);
        var created = w.GamePath("GameData/PerfMod001/created-by-the-update.cfg");
        File.WriteAllText(created, "new");
        // Trash from an earlier, committed update.
        var oldTrash = Path.Combine(SnapshotEngine.TrashDir(w.Profile), "20000101-000000-aaaaaa-11111111");
        Directory.CreateDirectory(oldTrash);
        File.WriteAllText(Path.Combine(oldTrash, "x.cfg"), "x");

        // First attempt cannot finish (the created file is held open): the snapshot must stay where it is.
        using (new FileStream(created, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var rb = w.Worker.Run(() => w.Ops().Recover(w.Profile));
            Assert.NotEmpty(rb!.Errors);
        }
        Assert.Equal(TxnState.RollbackFailed, TransactionJournal.ReadAll(w.Profile)[^1].State);
        Assert.NotNull(SnapshotEngine.Read(w.Profile, txnId));
        Assert.False(Directory.Exists(oldTrash), "old trash is emptied");
        Assert.False(Directory.Exists(SnapshotEngine.TrashDir(w.Profile)) && Directory.EnumerateDirectories(SnapshotEngine.TrashDir(w.Profile), txnId + "*").Any());

        // Second attempt restores exactly the state before the crash from that snapshot.
        var again = w.Worker.Run(() => w.Ops().Recover(w.Profile));
        Assert.Empty(again!.Errors);
        Assert.Equal(before, Image(w));
        Assert.Equal(TxnState.RolledBack, TransactionJournal.ReadAll(w.Profile)[^1].State);
        AssertClosedAndClean(w);
    }
}
