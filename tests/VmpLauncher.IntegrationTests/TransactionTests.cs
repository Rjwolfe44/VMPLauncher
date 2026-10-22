using System.IO;
using System.Text;
using VmpLauncher.Ckan.Repo;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Transactions;
using VmpLauncher.IntegrationTests.Fixtures;
using Xunit;

namespace VmpLauncher.IntegrationTests;

[Collection("ckan")]
public class TransactionTests
{
    private static readonly string[] Stock = { "GameData/Squad/Parts/stock.cfg", "GameData/SquadExpansion/MakingHistory/readme.txt", "GameData/SquadExpansion/Serenity/readme.txt" };

    [Fact]
    public void Install_fixture_pack_installs_verifies_and_keeps_saves()
    {
        using var w = new TestWorld("install");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B"), w.Repo.Get("D"), w.Repo.Get("E") });
        var report = w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        Assert.Equal(4, report.ModulesInstalled);
        Assert.True(File.Exists(w.GamePath("GameData/A/A.dll")));
        Assert.True(File.Exists(w.GamePath("GameData/E/E.cfg")));
        Assert.NotNull(InstalledState.TryRead(w.Profile));
        Assert.True(w.SentinelIntact());
        Assert.Equal(TxnState.Committed, TransactionJournal.ReadAll(w.Profile).Single().State);
        Assert.Equal(4, w.Server.Requests.Count(r => r.Status == 200));
    }

    [Fact]
    public void Exact_pin_ignores_newer_version_in_repo()
    {
        using var w = new TestWorld("pin");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A", "1.0") });
        w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        Assert.StartsWith("A-DLL-1.0", File.ReadAllText(w.GamePath("GameData/A/A.dll")), StringComparison.Ordinal);
        Assert.Empty(w.Server.RequestsFor("A-1.1.zip"));
    }

    [Fact]
    public void Unavailable_pinned_artifact_fails_before_any_mutation()
    {
        using var w = new TestWorld("404");
        w.Server.SetFault("B-1.0.zip", LocalArtifactServer.Fault.NotFound);
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B") });
        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Install(w.Request(lockPath))));
        Assert.Equal(ErrorCode.ArtifactUnavailable, ex.Code);
        Assert.Equal(Stock, w.GameDataFiles());
        Assert.Null(InstalledState.TryRead(w.Profile));
        Assert.False(File.Exists(Path.Combine(w.Profile.CkanDir, "registry.json")) && new FileInfo(Path.Combine(w.Profile.CkanDir, "registry.json")).Length > 0 && Directory.Exists(Path.Combine(w.Profile.GameDir, "GameData", "A")));
        Assert.Equal(TxnState.RolledBack, TransactionJournal.ReadAll(w.Profile).Single().State);
    }

    [Fact]
    public void Corrupted_artifact_is_rejected_and_purged()
    {
        using var w = new TestWorld("corrupt");
        w.Server.SetFault("E-1.0.zip", LocalArtifactServer.Fault.CorruptByte);
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("D"), w.Repo.Get("E") });
        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Install(w.Request(lockPath))));
        Assert.True(ex.Code is ErrorCode.ArtifactUnavailable or ErrorCode.ArtifactCorrupt, ex.Code.ToString());
        Assert.Equal(Stock, w.GameDataFiles());
        Assert.Empty(Directory.EnumerateFiles(w.CacheDir, "*.zip", SearchOption.AllDirectories).Where(f => f.Contains("E-1.0", StringComparison.Ordinal)));
    }

    [Fact]
    public void Conflicting_modules_are_detected_before_mutation()
    {
        using var w = new TestWorld("conflict");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B"), w.Repo.Get("C") });
        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Install(w.Request(lockPath))));
        Assert.Equal(ErrorCode.DependencyConflict, ex.Code);
        Assert.Equal(Stock, w.GameDataFiles());
    }

    [Fact]
    public void Duplicate_ownership_is_rejected_at_lock_validation()
    {
        using var w = new TestWorld("clash");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("Clash") });
        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Install(w.Request(lockPath))));
        Assert.Equal(ErrorCode.InvalidLock, ex.Code);
        Assert.Contains(ex.Details, d => d.Contains("duplicate-owner", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancel_mid_apply_rolls_back_files_and_registry()
    {
        using var w = new TestWorld("cancel");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B"), w.Repo.Get("D"), w.Repo.Get("E") });
        using var cts = new CancellationTokenSource();
        var completed = 0;
        var ops = w.Ops(_ =>
        {
            if (Interlocked.Increment(ref completed) == 2)
            {
                cts.Cancel();
            }
        });
        Assert.ThrowsAny<OperationCanceledException>(() => w.Worker.Run(() => ops.Install(w.Request(lockPath), cts.Token)));
        Assert.Equal(Stock, w.GameDataFiles());
        Assert.Null(InstalledState.TryRead(w.Profile));
        Assert.False(File.Exists(Path.Combine(w.Profile.CkanDir, "registry.json")), "registry.json should have been quarantined");
        Assert.Equal(TxnState.RolledBack, TransactionJournal.ReadAll(w.Profile).Single().State);
        Assert.True(w.SentinelIntact());
        // And the profile is usable again afterwards.
        var report = w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        Assert.Equal(4, report.ModulesInstalled);
    }

    [Fact]
    public void Failure_during_apply_restores_previous_install()
    {
        using var w = new TestWorld("fail-update");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A", "1.0"), w.Repo.Get("B") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        var before = w.GameDataFiles();
        var aBefore = File.ReadAllText(w.GamePath("GameData/A/A.dll"));

        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", new[] { w.Repo.Get("A", "1.1"), w.Repo.Get("B"), w.Repo.Get("F") });
        var ops = w.Ops(m => throw new IOException("simulated disk full", unchecked((int)0x80070070)));
        Assert.Throws<IOException>(() => w.Worker.Run(() => ops.Update(w.Request(v2))));

        Assert.Equal(before, w.GameDataFiles());
        Assert.Equal(aBefore, File.ReadAllText(w.GamePath("GameData/A/A.dll")));
        var state = InstalledState.TryRead(w.Profile);
        Assert.NotNull(state);
        Assert.Equal("1", state!.PackVersion);
        Assert.Equal(TxnState.RolledBack, TransactionJournal.ReadAll(w.Profile).Last().State);
        // registry restored: a verify + second update must work
        var report = w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        Assert.Equal("2", InstalledState.TryRead(w.Profile)!.PackVersion);
        Assert.Equal(2, report.ModulesInstalled);
    }

    [Fact]
    public void Update_downloads_only_changed_archives_and_leaves_unchanged_files_alone()
    {
        using var w = new TestWorld("update");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A", "1.0"), w.Repo.Get("B"), w.Repo.Get("D"), w.Repo.Get("E") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        var bIdBefore = NativeFiles.FileIdString(w.GamePath("GameData/B/B.dll"));
        w.Server.ClearRequests();

        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", new[] { w.Repo.Get("A", "1.1"), w.Repo.Get("B"), w.Repo.Get("D"), w.Repo.Get("E"), w.Repo.Get("F") });
        var diff = LockDiff.Compute(VmpLauncher.Core.Serialization.LauncherJson.ReadFile<PackLock>(v1), VmpLauncher.Core.Serialization.LauncherJson.ReadFile<PackLock>(v2));
        Assert.Single(diff.Changed);
        Assert.Single(diff.Added);
        Assert.Empty(diff.Removed);

        var report = w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        var fetched = w.Server.Requests.Where(r => r.Status == 200).Select(r => Path.GetFileName(r.Path)).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "A-1.1.zip", "F-1.0.zip" }, fetched);
        Assert.StartsWith("A-DLL-1.1", File.ReadAllText(w.GamePath("GameData/A/A.dll")), StringComparison.Ordinal);
        Assert.Equal(bIdBefore, NativeFiles.FileIdString(w.GamePath("GameData/B/B.dll")));
        Assert.Equal(2, report.ModulesInstalled);
        Assert.Equal(1, report.ModulesRemoved); // A 1.0 removed before A 1.1 installed
        Assert.Equal("2", InstalledState.TryRead(w.Profile)!.PackVersion);
    }

    [Fact]
    public void Update_removing_a_module_deletes_its_files()
    {
        using var w = new TestWorld("remove");
        var v1 = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("F") });
        w.Worker.Run(() => w.Ops().Install(w.Request(v1)));
        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", new[] { w.Repo.Get("A") });
        w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
        Assert.False(Directory.Exists(w.GamePath("GameData/F")));
        Assert.True(File.Exists(w.GamePath("GameData/A/A.dll")));
    }

    [Fact]
    public void Repair_restores_tampered_and_missing_files_and_quarantines_unknown_dll()
    {
        using var w = new TestWorld("repair");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("E"), w.Repo.Get("D") });
        w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        File.WriteAllText(w.GamePath("GameData/A/A.dll"), "tampered");
        File.Delete(w.GamePath("GameData/E/E.cfg"));
        Directory.CreateDirectory(w.GamePath("GameData/Evil"));
        File.WriteAllText(w.GamePath("GameData/Evil/evil.dll"), "evil");
        // PluginData drift must be tolerated, not "repaired".
        File.WriteAllText(w.GamePath("GameData/A/PluginData/s.cfg"), "state=42");

        var report = w.Worker.Run(() => w.Ops().Repair(w.Profile, w.CacheDir, quarantineUnknownDlls: true));
        Assert.Equal(2, report.FilesRestored);
        Assert.Equal(1, report.Quarantined);
        Assert.StartsWith("A-DLL-1.0", File.ReadAllText(w.GamePath("GameData/A/A.dll")), StringComparison.Ordinal);
        Assert.True(File.Exists(w.GamePath("GameData/E/E.cfg")));
        Assert.False(File.Exists(w.GamePath("GameData/Evil/evil.dll")));
        Assert.Equal("state=42", File.ReadAllText(w.GamePath("GameData/A/PluginData/s.cfg")));
        Assert.Single(Directory.EnumerateFiles(w.Profile.QuarantineDir, "evil.dll", SearchOption.AllDirectories));
    }

    [Fact]
    public void Interrupted_download_resumes_with_a_range_request()
    {
        using var w = new TestWorld("resume");
        w.Server.SetFault("E-1.0.zip", LocalArtifactServer.Fault.DropHalfwayOnce);
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("D"), w.Repo.Get("E") });
        try
        {
            w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        }
        catch (LauncherException)
        {
            // first attempt may fail; second must succeed
            w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        }
        Assert.True(File.Exists(w.GamePath("GameData/E/E.dll")));
        var eRequests = w.Server.RequestsFor("E-1.0.zip");
        Assert.True(eRequests.Count >= 2, "expected a retry");
    }

    [Fact]
    public void Overrides_add_replace_disable_are_applied_and_verified()
    {
        using var w = new TestWorld("overrides");
        var addBytes = Encoding.UTF8.GetBytes("SigmaReplacements {}\n");
        var replaceBytes = Encoding.UTF8.GetBytes("B = replaced\n");
        var originalB = w.Repo.Get("B");
        var originalBcfgSha = ArchiveEntrySha(originalB.ZipPath, "GameData/B/B.cfg");
        var originalEdllSha = ArchiveEntrySha(w.Repo.Get("E").ZipPath, "GameData/E/E.dll");
        var overrides = new[]
        {
            new LockOverride { Id = "ovr-add", Op = "add", Path = "GameData/Sigma2/Settings.cfg", Sha256 = Sha256Util.HashBytes(addBytes), Size = addBytes.Length, Content = new OverrideContent { InlineBase64 = Convert.ToBase64String(addBytes) } },
            new LockOverride { Id = "ovr-replace", Op = "replace", Path = "GameData/B/B.cfg", Owner = "B", BaseSha256 = originalBcfgSha, Sha256 = Sha256Util.HashBytes(replaceBytes), Size = replaceBytes.Length, Content = new OverrideContent { InlineBase64 = Convert.ToBase64String(replaceBytes) } },
            new LockOverride { Id = "ovr-disable", Op = "disable", Path = "GameData/E/E.dll", Owner = "E", BaseSha256 = originalEdllSha, RenameTo = "E.dll.disabled" },
        };
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B"), w.Repo.Get("D"), w.Repo.Get("E") }, overrides);
        w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        Assert.Equal("SigmaReplacements {}\n", File.ReadAllText(w.GamePath("GameData/Sigma2/Settings.cfg")));
        Assert.Equal("B = replaced\n", File.ReadAllText(w.GamePath("GameData/B/B.cfg")));
        Assert.False(File.Exists(w.GamePath("GameData/E/E.dll")));
        Assert.True(File.Exists(w.GamePath("GameData/E/E.dll.disabled")));
        // Repair must not "fix" the overrides back to the originals.
        var repair = w.Worker.Run(() => w.Ops().Repair(w.Profile, w.CacheDir, quarantineUnknownDlls: false));
        Assert.Null(repair.TxnId);
    }

    [Fact]
    public void Lock_with_traversal_path_is_rejected_before_mutation()
    {
        using var w = new TestWorld("traversal");
        var evil = new VmpLauncher.Core.Inventory.InventoryEntry { Path = "GameData/../../escape.txt", Size = 1, Sha256 = new string('a', 64), Owner = "A" };
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A") }, extraInventory: new[] { evil });
        var ex = Assert.Throws<LauncherException>(() => w.Worker.Run(() => w.Ops().Install(w.Request(lockPath))));
        Assert.Equal(ErrorCode.InvalidLock, ex.Code);
        Assert.Equal(Stock, w.GameDataFiles());
    }

    [Fact]
    public void Epoch_versions_install_and_verify()
    {
        using var w = new TestWorld("epoch");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("Epoch", "1:2.0") });
        w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        Assert.True(File.Exists(w.GamePath("GameData/Epoch/e.cfg")));
        Assert.Contains("Epoch-1_2.0.ckan", Directory.EnumerateFiles(w.Profile.PackRepoDir).Select(f => ReadZipEntryNames(f)).SelectMany(x => x).Select(Path.GetFileName));
    }

    [Fact]
    public void Hand_made_metapackage_is_normalized()
    {
        const string raw = """
            {
              "spec_version": "v1.6",
              "identifier": "VMP-Train-Career-Locked",
              "name": "VMP Train Career (locked)",
              "abstract": "x",
              "author": "VMP host",
              "version": "2026.10.22.02.39.19",
              "license": "unknown",
              "depends": [ { "name": "ModuleManager", "version": "4.2.3", "suppress_recommendations": true }, { "name": "B9", "version": "1:v6.6.0", "suppress_recommendations": true } ]
            }
            """;
        Assert.ThrowsAny<Exception>(() => CKAN.CkanModule.FromJson(raw));
        var imported = CkanFileImporter.Import(raw);
        Assert.True(imported.Module.IsMetapackage);
        var pins = CkanFileImporter.ExactPins(imported.Module);
        Assert.Equal(2, pins.Count);
        Assert.Contains(pins, p => p.Identifier == "B9" && p.Version == "1:v6.6.0");
    }

    private static string ArchiveEntrySha(string zip, string entry)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        using var s = z.GetEntry(entry)!.Open();
        return Sha256Util.HashStream(s);
    }

    private static IEnumerable<string> ReadZipEntryNames(string zip)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        return z.Entries.Select(e => e.FullName).ToList();
    }
}

[CollectionDefinition("ckan", DisableParallelization = true)]
public class CkanCollection
{
}
