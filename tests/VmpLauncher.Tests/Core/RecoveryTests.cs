using System.IO;
using VmpLauncher.App.Services;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class RecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-recovery-" + Guid.NewGuid().ToString("N")[..8]);

    public RecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));

    private bool Exists(string rel) => File.Exists(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Stale_ckan_lock_is_removed_but_a_held_one_is_kept()
    {
        var game = Path.Combine(_root, "game");
        var lockFile = Write("game/CKAN/registry.locked", "1234");
        using (new FileStream(lockFile, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.False(CkanLockFile.ClearStale(game)); // CKAN (or another launcher) has it open
            Assert.True(File.Exists(lockFile));
        }
        Assert.True(CkanLockFile.ClearStale(game)); // left behind by a crash
        Assert.False(File.Exists(lockFile));
        Assert.False(CkanLockFile.ClearStale(game)); // nothing there
    }

    [Fact]
    public void Player_files_move_to_the_fresh_copy_and_pack_files_stay()
    {
        Write("old/saves/career/persistent.sfs", "my career");
        Write("old/Ships/VAB/My Rocket.craft", "my craft");
        Write("old/Screenshots/shot.png", "png");
        Write("old/settings.cfg", "MY KEYS");
        Write("old/GameData/Mod/PluginData/settings.cfg", "my mod settings");
        Write("old/GameData/VladMultiplayer/Data/settings.xml", "my servers");
        Write("old/GameData/Mod/Mod.dll", "MY CHANGED DLL");
        Write("old/GameData/Mod/Parts/@thumbs/part_icon.png", "KSP's redrawn icon");
        Write("new/Ships/VAB/Stock.craft", "seeded copy");
        Write("new/settings.cfg", "DEFAULTS");
        Write("new/GameData/Mod/Mod.dll", "PACK DLL");
        Write("new/GameData/Mod/Parts/@thumbs/part_icon.png", "pack icon");

        var kept = PlayerFiles.MoveInto(Path.Combine(_root, "old"), Path.Combine(_root, "new"), FileRules.Default);

        Assert.Equal("my career", Read("new/saves/career/persistent.sfs"));
        Assert.Equal("my craft", Read("new/Ships/VAB/My Rocket.craft"));
        Assert.False(Exists("new/Ships/VAB/Stock.craft")); // the player's Ships folder replaces the seeded one
        Assert.Equal("png", Read("new/Screenshots/shot.png"));
        Assert.Equal("MY KEYS", Read("new/settings.cfg"));
        Assert.Equal("my mod settings", Read("new/GameData/Mod/PluginData/settings.cfg"));
        Assert.Equal("my servers", Read("new/GameData/VladMultiplayer/Data/settings.xml"));
        Assert.Equal("PACK DLL", Read("new/GameData/Mod/Mod.dll"));
        Assert.Equal("pack icon", Read("new/GameData/Mod/Parts/@thumbs/part_icon.png"));
        Assert.Equal(3, kept.Folders);
        Assert.Equal(3, kept.Files);
    }

    [Fact]
    public async Task Failed_or_cancelled_rebuild_puts_the_old_copy_back()
    {
        var root = Path.Combine(_root, "profile");
        Write("profile/game/saves/default/persistent.sfs", "my career");
        Write("profile/game/GameData/Mod/Mod.dll", "OLD");

        await Assert.ThrowsAsync<OperationCanceledException>(() => ProfileReinstaller.RunAsync<int>(root, FileRules.Default, () =>
        {
            Write("profile/game/GameData/Mod/Mod.dll", "HALF-BUILT"); // the new copy got part of the way
            throw new OperationCanceledException();
        }));

        Assert.Equal("my career", Read("profile/game/saves/default/persistent.sfs"));
        Assert.Equal("OLD", Read("profile/game/GameData/Mod/Mod.dll"));
        Assert.Empty(Directory.EnumerateDirectories(_root, "profile" + ProfileReinstaller.OldCopyMarker + "*"));
    }

    [Fact]
    public async Task Rebuild_keeps_player_files_and_removes_the_old_copy()
    {
        var root = Path.Combine(_root, "profile");
        Write("profile/game/saves/default/persistent.sfs", "my career");
        Write("profile/game/GameData/Mod/Mod.dll", "BROKEN");

        var outcome = await ProfileReinstaller.RunAsync(root, FileRules.Default, () =>
        {
            Write("profile/game/GameData/Mod/Mod.dll", "FRESH");
            return Task.FromResult(42);
        });

        Assert.Equal(42, outcome.Built);
        Assert.Null(outcome.LeftOver);
        Assert.Equal("FRESH", Read("profile/game/GameData/Mod/Mod.dll"));
        Assert.Equal("my career", Read("profile/game/saves/default/persistent.sfs"));
        Assert.Empty(Directory.EnumerateDirectories(_root, "profile" + ProfileReinstaller.OldCopyMarker + "*"));
    }

    [Fact]
    public void Old_copies_are_kept_while_the_current_copy_is_not_a_finished_install()
    {
        Directory.CreateDirectory(Path.Combine(_root, "profile", "game"));
        var old = Path.Combine(_root, "profile" + ProfileReinstaller.OldCopyMarker + "20261023-050000");
        Directory.CreateDirectory(old);
        Assert.Equal(0, ProfileReinstaller.CleanUpOldCopies(new ProfilePaths(Path.Combine(_root, "profile"))));
        Assert.True(Directory.Exists(old));
    }

    [Theory]
    [InlineData(RecoveryHint.Repair, "Repair files")]
    [InlineData(RecoveryHint.Restore, "Restore")]
    [InlineData(RecoveryHint.Reinstall, "Reinstall the pack copy")]
    [InlineData(RecoveryHint.Retry, null)]
    [InlineData(RecoveryHint.None, null)]
    public void Error_cards_offer_the_fix_for_their_hint(RecoveryHint hint, string? label)
    {
        Assert.Equal(label, new UserFacingError("t", "m", null, string.Empty, hint).ActionLabel);
    }

    [Fact]
    public void Update_that_still_fails_after_repair_points_to_reinstall()
    {
        var e = ErrorCatalog.From(new LauncherException(ErrorCode.VerifyFailed, "still", RecoveryHint.Reinstall));
        Assert.Equal("Files still do not match the pack", e.Title);
        Assert.Equal("Reinstall the pack copy", e.ActionLabel);
    }
}
