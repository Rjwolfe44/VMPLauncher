using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using Xunit;

namespace VmpLauncher.Tests.Core;

public class GlobAndOwnershipTests
{
    [Theory]
    [InlineData("GameData/**/*.log", "GameData/Foo/Bar/x.log", true)]
    [InlineData("GameData/**/*.log", "GameData/x.log", true)]
    [InlineData("GameData/**/*.log", "GameData/Foo/x.txt", false)]
    [InlineData("GameData/**/PluginData/**", "GameData/Kopernicus/PluginData/a/b.cfg", true)]
    [InlineData("GameData/**/PluginData/**", "GameData/Kopernicus/Plugins/x.dll", false)]
    [InlineData("GameData/Squad/**", "GameData/Squad/Parts/x.cfg", true)]
    [InlineData("GameData/Squad/**", "GameData/SquadExpansion/x.cfg", false)]
    [InlineData("GameData/ModuleManager.ConfigCache", "GameData/ModuleManager.ConfigCache", true)]
    [InlineData("GameData/ModuleManager.ConfigCache", "gamedata/modulemanager.configcache", true)]
    public void Glob_matches(string glob, string path, bool expected)
        => Assert.Equal(expected, new GlobMatcher(new[] { glob }).IsMatch(path));

    [Fact]
    public void Default_rules_classify_known_files()
    {
        var rules = new FileRuleSet(FileRules.Default);
        Assert.True(rules.IsIgnored("GameData/Squad/Parts/x.cfg"));
        Assert.True(rules.IsIgnored("GameData/ModuleManager.ConfigSHA"));
        Assert.True(rules.IsIgnored("GameData/VladMultiplayer/Data/settings.xml"));
        Assert.True(rules.MutableOwned.IsMatch("GameData/[x]_Science!/PluginData/settings.cfg"));
        Assert.False(rules.IsIgnored("GameData/ModuleManager.4.2.3.dll"));
    }

    private static InventoryEntry Entry(string path, string owner) => new() { Path = path, Size = 1, Sha256 = new string('a', 64), Owner = owner };

    [Fact]
    public void Duplicate_owner_is_rejected_case_insensitively()
    {
        var problems = OwnershipValidator.Validate(new[] { Entry("GameData/A/a.dll", "A"), Entry("GameData/a/A.DLL", "Clash") }, Array.Empty<LockOverride>());
        Assert.Contains(problems, p => p.Code == "duplicate-owner");
    }

    [Fact]
    public void Root_files_need_explicit_owner_listing()
    {
        var files = new[] { Entry("SDL2.dll", "AdvancedFlyByWire") };
        Assert.Contains(OwnershipValidator.Validate(files, Array.Empty<LockOverride>()), p => p.Code == "root-not-allowed");
        Assert.Empty(OwnershipValidator.Validate(files, Array.Empty<LockOverride>(), new[] { "AdvancedFlyByWire" }));
        Assert.Contains(OwnershipValidator.Validate(new[] { Entry("Other/x.txt", "Foo") }, Array.Empty<LockOverride>(), new[] { "Foo" }), p => p.Code == "root-not-allowed");
    }

    [Fact]
    public void Overrides_must_name_owner_and_base_hash()
    {
        var files = new[] { Entry("GameData/JSI/MechJebRPM.dll", "RasterPropMonitor-Core") };
        var bad = new LockOverride { Id = "o1", Op = "disable", Path = "GameData/JSI/MechJebRPM.dll" };
        Assert.Contains(OwnershipValidator.Validate(files, new[] { bad }), p => p.Code == "override-needs-owner");
        var good = new LockOverride { Id = "o1", Op = "disable", Path = "GameData/JSI/MechJebRPM.dll", Owner = "RasterPropMonitor-Core", BaseSha256 = new string('b', 64), RenameTo = "MechJebRPM.dll.disabled" };
        Assert.Empty(OwnershipValidator.Validate(files, new[] { good }));
        var addOverOwned = new LockOverride { Id = "o2", Op = "add", Path = "GameData/JSI/MechJebRPM.dll", Sha256 = new string('c', 64) };
        Assert.Contains(OwnershipValidator.Validate(files, new[] { addOverOwned }), p => p.Code == "add-over-owned");
    }

    [Fact]
    public void Unsafe_inventory_paths_are_reported()
        => Assert.Contains(OwnershipValidator.Validate(new[] { Entry("GameData/../x", "A") }, Array.Empty<LockOverride>()), p => p.Code == "unsafe-path");
}
