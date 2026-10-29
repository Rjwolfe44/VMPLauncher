using System.IO;
using System.IO.Compression;
using System.Text;
using VmpLauncher.Core.Catalog;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class SetupShareTests
{
    private static SharedSetup Sample(int mods = 3) => new()
    {
        Name = "Career with visuals",
        By = "Vlad",
        GameVersion = "1.12.5",
        Mods = Enumerable.Range(0, mods).Select(i => new SharedMod(i == 0 ? "Scatterer" : $"Mod{i}", i == 0 ? "3:v0.0878" : $"1.{i}.0", i % 3 == 2)).ToList(),
    };

    [Fact]
    public void Code_round_trips()
    {
        var code = SetupShare.ToCode(Sample());
        Assert.StartsWith(SetupShare.CodePrefix, code, StringComparison.Ordinal);
        Assert.True(SetupShare.TryParse(code, out var back, out var error), error);
        Assert.Equal("Career with visuals", back!.Name);
        Assert.Equal("Vlad", back.By);
        Assert.Equal("1.12.5", back.GameVersion);
        Assert.Equal(Sample().Mods, back.Mods);
    }

    [Fact]
    public void File_round_trips()
    {
        var json = SetupShare.ToJson(Sample());
        Assert.Contains("\"schema\": \"vmpshare/1\"", json, StringComparison.Ordinal);
        Assert.True(SetupShare.TryParse(json, out var back, out _));
        Assert.Equal(Sample().Mods, back!.Mods);
    }

    [Fact]
    public void A_code_is_found_inside_a_chat_message_or_link()
    {
        var code = SetupShare.ToCode(Sample());
        Assert.True(SetupShare.TryParse($"yo try this: {code} (needs 1.12.5)", out _, out _));
        Assert.Equal(code, SetupShare.FindCode("vmpack://share/" + code));
    }

    [Fact]
    public void Small_setups_fit_a_chat_message_and_big_ones_still_round_trip()
    {
        Assert.True(SetupShare.ToCode(Sample(40)).Length <= SetupShare.ChatLimit);
        var big = Sample(400);
        Assert.True(SetupShare.TryParse(SetupShare.ToCode(big), out var back, out _));
        Assert.Equal(400, back!.Mods.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("VMPSHARE1-!!!")]
    [InlineData("{\"schema\":\"vmpack/1\",\"name\":\"x\",\"mods\":[]}")]
    [InlineData("{\"schema\":\"vmpshare/1\",\"name\":\"x\",\"mods\":[]}")]
    [InlineData("{\"schema\":\"vmpshare/1\",\"name\":\"x\",\"mods\":[{\"id\":\"../evil\",\"version\":\"1\"}]}")]
    [InlineData("{\"schema\":\"vmpshare/1\",\"name\":\"x\",\"mods\":[{\"id\":\"A\",\"version\":\"1\"},{\"id\":\"A\",\"version\":\"2\"}]}")]
    public void Rejects_anything_that_is_not_a_valid_share(string text)
    {
        Assert.False(SetupShare.TryParse(text, out var setup, out var error));
        Assert.Null(setup);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_code_that_expands_to_megabytes_is_refused()
    {
        var huge = "vmpshare1\nx\n\n\n" + new string('A', 2_000_000);
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            d.Write(Encoding.UTF8.GetBytes(huge));
        }
        var code = SetupShare.CodePrefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(SetupShare.TryParse(code, out _, out var error));
        Assert.Contains("too", error, StringComparison.OrdinalIgnoreCase);
    }
}
