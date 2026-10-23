using System.IO;
using VmpLauncher.Core.Profiles;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class SetupProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-setup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ProfileManifest Manifest(string? kind = null, string? packId = null, string? gameDir = null) => new()
    {
        Id = "x",
        Name = "X",
        Kind = kind,
        PackId = packId,
        GameDir = gameDir,
        Seed = new SeedInfo { FromPath = "src", BuildId64 = "03190", Mode = "copy" },
    };

    [Fact]
    public void Own_game_folder_by_default()
    {
        var p = new ProfilePaths(Path.Combine(_root, "custom"));
        p.EnsureCreated();
        p.WriteManifest(Manifest(SetupKinds.Custom));
        Assert.Equal(Path.Combine(p.Root, "game"), new ProfilePaths(p.Root).GameDir);
    }

    [Fact]
    public void Existing_setups_point_at_the_players_folder()
    {
        var ksp = Path.Combine(_root, "Steam KSP");
        var p = new ProfilePaths(Path.Combine(_root, "existing"));
        p.EnsureCreated();
        p.WriteManifest(Manifest(SetupKinds.Existing, gameDir: ksp));
        Assert.Equal(ksp, p.GameDir);
        Assert.Equal(ksp, new ProfilePaths(p.Root).GameDir);
        Assert.Equal(Path.Combine(ksp, "GameData"), new ProfilePaths(p.Root).GameDataDir);
    }

    [Theory]
    [InlineData(null, "vmp-train-career", SetupKinds.Pack)]
    [InlineData(null, null, SetupKinds.Custom)]
    [InlineData(SetupKinds.Existing, null, SetupKinds.Existing)]
    public void Older_profiles_get_a_kind(string? kind, string? packId, string expected)
        => Assert.Equal(expected, Manifest(kind, packId).EffectiveKind);
}
