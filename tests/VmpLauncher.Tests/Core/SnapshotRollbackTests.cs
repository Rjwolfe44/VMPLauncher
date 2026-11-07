using System.Diagnostics;
using System.IO;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Transactions;
using Xunit;

namespace VmpLauncher.Tests.Core;

/// <summary>SnapshotEngine + RollbackEngine on a plain folder (no CKAN).</summary>
public sealed class SnapshotRollbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-snaptests", Guid.NewGuid().ToString("N")[..8]);
    private readonly ProfilePaths _profile;

    public SnapshotRollbackTests()
    {
        _profile = new ProfilePaths(Path.Combine(_root, "profile"));
        _profile.EnsureCreated();
        Write("KSP_x64.exe", "stock exe");
        Write("GameData/M/m.cfg", "m");
    }

    private string? _junction;

    public void Dispose()
    {
        try
        {
            if (_junction is not null && Directory.Exists(_junction))
            {
                Directory.Delete(_junction); // the link only, never its target's contents
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Game(string rel) => Path.Combine(_profile.GameDir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Game(rel))!);
        File.WriteAllText(Game(rel), content);
    }

    private RollbackEngine.Result Restore(string txnId, FileRules? fallback = null)
        => RollbackEngine.Restore(_profile, new TransactionJournal { TxnId = txnId, Operation = "update", State = TxnState.RollingBack }, fallback);

    [Fact]
    public void Restore_uses_the_rules_the_snapshot_was_taken_with()
    {
        var rules = FileRules.Default with { Generated = FileRules.Default.Generated.Append("GameData/M/generated.cfg").ToList() };
        SnapshotEngine.Create(_profile, rules, "t1", null);
        Write("GameData/M/generated.cfg", "written by the game during the transaction");

        var rb = Restore("t1", FileRules.Default);
        Assert.Empty(rb.Errors);
        Assert.True(File.Exists(Game("GameData/M/generated.cfg")), "ignored by the snapshot's rules, so not quarantined");
    }

    [Fact]
    public void Restore_quarantines_only_pack_owned_root_files_the_transaction_created()
    {
        Write("oldroot.txt", "owned by the installed pack");
        SnapshotEngine.Create(_profile, FileRules.Default, "t2", new[] { "oldroot.txt", "newroot.txt" });
        File.Delete(Game("oldroot.txt"));         // uninstalled by the transaction
        Write("newroot.txt", "GameRoot install");  // installed by the transaction
        Write("user.txt", "not the pack's");       // not a candidate: left alone

        var rb = Restore("t2");
        Assert.Empty(rb.Errors);
        Assert.Equal("owned by the installed pack", File.ReadAllText(Game("oldroot.txt")));
        Assert.False(File.Exists(Game("newroot.txt")));
        Assert.True(File.Exists(Path.Combine(_profile.QuarantineDir, "t2", "newroot.txt")));
        Assert.True(File.Exists(Game("user.txt")));
        Assert.Equal("stock exe", File.ReadAllText(Game("KSP_x64.exe")));
    }

    [Fact]
    public void Restore_keeps_and_recreates_directories_that_existed_before()
    {
        Directory.CreateDirectory(Game("GameData/M/Empty"));
        SnapshotEngine.Create(_profile, FileRules.Default, "t3", null);
        Directory.Delete(Game("GameData/M"), recursive: true); // module uninstalled
        Write("GameData/N/n.cfg", "new module");

        var rb = Restore("t3");
        Assert.Empty(rb.Errors);
        Assert.True(Directory.Exists(Game("GameData/M/Empty")));
        Assert.True(File.Exists(Game("GameData/M/m.cfg")));
        Assert.False(Directory.Exists(Game("GameData/N")), "new, now-empty folders are pruned");
    }

    [Fact]
    public void Snapshot_rollback_and_verify_do_not_follow_junctions()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.dll"), "not the profile's");
        _junction = Game("GameData/Link");
        CreateJunction(_junction, outside);

        var manifest = SnapshotEngine.Create(_profile, FileRules.Default, "t4", null);
        Assert.DoesNotContain(manifest.Files, f => f.Path.StartsWith("GameData/Link", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifest.Directories, d => d.StartsWith("GameData/Link", StringComparison.OrdinalIgnoreCase));

        var rb = Restore("t4");
        Assert.Empty(rb.Errors);
        Assert.True(File.Exists(Path.Combine(outside, "secret.dll")), "files behind a junction are never quarantined");

        var known = new VmpLauncher.Core.Inventory.InventoryEntry { Path = "GameData/M/m.cfg", Size = 1, Sha256 = new string('a', 64), Owner = "M" };
        var inventory = new VmpLauncher.Core.Inventory.InventoryDocument { PackId = "p", PackVersion = "1", Files = new[] { known } };
        var verify = new InstallVerifier().Verify(_profile, inventory, FileRules.Default, InstallVerifier.Mode.Full);
        Assert.DoesNotContain(verify.UnknownDlls, d => d.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Parallel_snapshot_of_long_paths_restores_them_and_a_discarded_snapshot_leaves_game_files_alone()
    {
        // Past MAX_PATH: the raw Win32 calls (hard link, attribute-only open) need the long-path form.
        var deep = "GameData/M/" + string.Join('/', Enumerable.Range(0, 12).Select(i => $"folder-with-a-long-name-{i:00}"));
        for (var i = 0; i < 100; i++)
        {
            Write($"{deep}/file{i:000}.cfg", $"content {i}");
        }
        Assert.True(Game($"{deep}/file000.cfg").Length > 300);
        FileParallelism.Override = 8;
        try
        {
            var manifest = SnapshotEngine.Create(_profile, FileRules.Default, "t5", null);
            Assert.Equal(101, manifest.Files.Count);
            Assert.All(manifest.Files, f => Assert.NotNull(f.FileId));
            Assert.Equal(2u, NativeFiles.GetFileIdentity(Game($"{deep}/file099.cfg")).Links); // linked, not copied
            for (var i = 0; i < 50; i++)
            {
                File.Delete(Game($"{deep}/file{i:000}.cfg"));                 // removed by the transaction
            }
            Write($"{deep}/created.cfg", "created by the transaction");

            var rb = Restore("t5");
            Assert.Empty(rb.Errors);
            for (var i = 0; i < 50; i++)
            {
                Assert.Equal($"content {i}", File.ReadAllText(Game($"{deep}/file{i:000}.cfg")));
            }
            Assert.False(File.Exists(Game($"{deep}/created.cfg")));

            SnapshotEngine.Discard(_profile, "t5");
            Assert.False(Directory.Exists(Path.Combine(_profile.SnapshotsDir, "t5")));
            Assert.True(SnapshotEngine.HasTrash(_profile));
            Assert.Equal(1, SnapshotEngine.EmptyTrash(_profile));
            Assert.False(SnapshotEngine.HasTrash(_profile));
            for (var i = 51; i < 100; i++)
            {
                Assert.Equal($"content {i}", File.ReadAllText(Game($"{deep}/file{i:000}.cfg")));
            }
        }
        finally
        {
            FileParallelism.Override = null;
        }
    }

    [Fact]
    public void Emptying_the_trash_removes_a_junction_without_touching_what_it_points_to()
    {
        var outside = Path.Combine(_root, "outside2");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not the launcher's");
        var trashed = Path.Combine(SnapshotEngine.TrashDir(_profile), "old-snapshot", "game", "GameData");
        Directory.CreateDirectory(trashed);
        File.WriteAllText(Path.Combine(trashed, "a.cfg"), "a");
        _junction = Path.Combine(trashed, "Link");
        CreateJunction(_junction, outside);

        Assert.Equal(1, SnapshotEngine.EmptyTrash(_profile));
        Assert.False(Directory.Exists(Path.Combine(SnapshotEngine.TrashDir(_profile), "old-snapshot")));
        Assert.Equal("not the launcher's", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void A_trash_folder_that_is_a_junction_is_never_used()
    {
        var outside = Path.Combine(_root, "outside3");
        Directory.CreateDirectory(Path.Combine(outside, "looks-like-a-snapshot"));
        File.WriteAllText(Path.Combine(outside, "looks-like-a-snapshot", "keep.txt"), "not the launcher's");
        _junction = SnapshotEngine.TrashDir(_profile);
        CreateJunction(_junction, outside);
        var logged = new List<string>();

        Assert.False(SnapshotEngine.HasTrash(_profile, logged.Add));
        Assert.Equal(0, SnapshotEngine.EmptyTrash(_profile, logged.Add));
        SnapshotEngine.Create(_profile, FileRules.Default, "t6", null);
        SnapshotEngine.Discard(_profile, "t6"); // deleted in place instead of moved through the link

        Assert.False(Directory.Exists(Path.Combine(_profile.SnapshotsDir, "t6")));
        Assert.Equal(new[] { "looks-like-a-snapshot" }, Directory.EnumerateFileSystemEntries(outside).Select(Path.GetFileName));
        Assert.Equal("not the launcher's", File.ReadAllText(Path.Combine(outside, "looks-like-a-snapshot", "keep.txt")));
        Assert.True(logged.Count <= 1, "logged at most once");
        Assert.Equal("m", File.ReadAllText(Game("GameData/M/m.cfg")));
    }

    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && Directory.Exists(link), "could not create a junction: " + p.StandardError.ReadToEnd());
    }
}
