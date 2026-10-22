using System.IO;
using VmpLauncher.Core.Security;
using Xunit;

namespace VmpLauncher.Tests.Core;

public class SafePathTests
{
    [Theory]
    [InlineData("GameData/Foo/bar.cfg", "GameData/Foo/bar.cfg")]
    [InlineData(@"GameData\Foo\bar.dll", "GameData/Foo/bar.dll")]
    [InlineData("Ships/VAB/Kerbal X.craft", "Ships/VAB/Kerbal X.craft")]
    public void Normalizes_valid_paths(string input, string expected)
        => Assert.Equal(expected, SafePath.Normalize(input));

    [Theory]
    [InlineData("../x")]
    [InlineData("GameData/../../x")]
    [InlineData("/GameData/x")]
    [InlineData(@"C:\x")]
    [InlineData("GameData/a:b")]
    [InlineData("GameData/CON.cfg")]
    [InlineData("GameData/nul")]
    [InlineData("GameData//x")]
    [InlineData("GameData/x. ")]
    [InlineData("GameData/x\u0001y")]
    [InlineData("GameData/<x>")]
    [InlineData("")]
    public void Rejects_unsafe_paths(string input)
        => Assert.Throws<UnsafePathException>(() => SafePath.Normalize(input));

    [Fact]
    public void Resolve_stays_under_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmpl-safepath");
        var full = SafePath.Resolve(root, "GameData/Foo/x.cfg");
        Assert.StartsWith(Path.GetFullPath(root), full, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<UnsafePathException>(() => SafePath.Resolve(root, "../escape.txt"));
    }

    [Fact]
    public void Rejects_overlong_paths()
        => Assert.Throws<UnsafePathException>(() => SafePath.Normalize("GameData/" + new string('a', 300)));
}
