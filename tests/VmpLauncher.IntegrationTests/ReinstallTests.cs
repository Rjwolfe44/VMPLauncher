using System.IO;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Transactions;
using VmpLauncher.IntegrationTests.Fixtures;
using Xunit;

namespace VmpLauncher.IntegrationTests;

/// <summary>The last-resort reinstall against real CKAN installs: what LauncherService.ReinstallAsync runs, minus the UI.</summary>
[Collection("ckan")]
public class ReinstallTests
{
    /// <summary>What the launcher's build step does: seed a fresh copy from the player's KSP, then install the lock.</summary>
    private static Task<ProfileOperations.Report> Rebuild(TestWorld w, string lockPath, Action<CKAN.CkanModule>? fault = null) => Task.FromResult(w.Worker.Run(() =>
    {
        w.Profile.EnsureCreated();
        new ProfileSeeder().Seed(new ProfileSeeder.Plan(w.SourceKsp, w.Profile.GameDir, HardLink: false, Array.Empty<string>()));
        w.Profile.WriteManifest(new ProfileManifest { Id = "test", Name = "test", Seed = new SeedInfo { FromPath = w.SourceKsp, BuildId64 = "03190", Mode = "copy" } });
        return w.Ops(fault).Install(w.Request(lockPath));
    }));

    private static void Write(TestWorld w, string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(w.GamePath(rel))!);
        File.WriteAllText(w.GamePath(rel), content);
    }

    [Fact]
    public async Task Reinstall_rebuilds_a_broken_copy_and_keeps_the_players_files()
    {
        using var w = new TestWorld("reinstall");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B"), w.Repo.Get("D"), w.Repo.Get("E") });
        w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        // A copy that Repair could not save: a broken pack file, a stray DLL, a leftover CKAN lock, plus the player's own files.
        Write(w, "GameData/A/A.dll", "BROKEN");
        Write(w, "GameData/Stray/Stray.dll", "not in the pack");
        Write(w, "CKAN/registry.locked", "4242");
        Write(w, "saves/career/persistent.sfs", "my career");
        Write(w, "Ships/VAB/My Rocket.craft", "my craft");
        Write(w, "settings.cfg", "MY KEYS");
        Write(w, "GameData/A/PluginData/settings.cfg", "my mod settings");

        var outcome = await ProfileReinstaller.RunAsync(w.Profile.Root, FileRules.Default, () => Rebuild(w, lockPath), w.Log);

        Assert.Equal(4, outcome.Built.ModulesInstalled);
        Assert.StartsWith("A-DLL-1.0", File.ReadAllText(w.GamePath("GameData/A/A.dll")), StringComparison.Ordinal);
        Assert.False(File.Exists(w.GamePath("GameData/Stray/Stray.dll")));
        Assert.True(w.SentinelIntact());
        Assert.Equal("my career", File.ReadAllText(w.GamePath("saves/career/persistent.sfs")));
        Assert.Equal("my craft", File.ReadAllText(w.GamePath("Ships/VAB/My Rocket.craft")));
        Assert.Equal("MY KEYS", File.ReadAllText(w.GamePath("settings.cfg")));
        Assert.Equal("my mod settings", File.ReadAllText(w.GamePath("GameData/A/PluginData/settings.cfg")));
        Assert.NotNull(InstalledState.TryRead(w.Profile));
        Assert.Null(outcome.LeftOver);
        Assert.Empty(Directory.EnumerateDirectories(w.Root, "profile" + ProfileReinstaller.OldCopyMarker + "*"));
        // The fresh copy updates and verifies like any other.
        var v2 = w.Repo.WriteLock(w.LockDir("v2"), "2", new[] { w.Repo.Get("A"), w.Repo.Get("B"), w.Repo.Get("D") });
        w.Worker.Run(() => w.Ops().Update(w.Request(v2)));
    }

    [Fact]
    public async Task Failed_reinstall_puts_the_old_copy_back_untouched()
    {
        using var w = new TestWorld("reinstall-fail");
        var lockPath = w.Repo.WriteLock(w.LockDir("v1"), "1", new[] { w.Repo.Get("A"), w.Repo.Get("B") });
        w.Worker.Run(() => w.Ops().Install(w.Request(lockPath)));
        Write(w, "GameData/A/A.dll", "BROKEN");
        Write(w, "saves/career/persistent.sfs", "my career");
        var installedBefore = InstalledState.TryRead(w.Profile)!.LockSha256;

        await Assert.ThrowsAnyAsync<Exception>(() => ProfileReinstaller.RunAsync(w.Profile.Root, FileRules.Default,
            () => Rebuild(w, lockPath, fault: m => { if (m.identifier == "B") { throw new IOException("simulated disk failure"); } }), w.Log));

        Assert.Equal("BROKEN", File.ReadAllText(w.GamePath("GameData/A/A.dll")));
        Assert.Equal("my career", File.ReadAllText(w.GamePath("saves/career/persistent.sfs")));
        Assert.True(w.SentinelIntact());
        Assert.Equal(installedBefore, InstalledState.TryRead(w.Profile)!.LockSha256);
        Assert.Empty(Directory.EnumerateDirectories(w.Root, "profile" + ProfileReinstaller.OldCopyMarker + "*"));
    }
}
