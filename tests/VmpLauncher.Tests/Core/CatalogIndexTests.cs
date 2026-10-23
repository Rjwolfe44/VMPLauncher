using VmpLauncher.Core.Catalog;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class CatalogIndexTests
{
    private static CatalogEntry E(string id, string name, int downloads = 0, bool compatible = true, bool installed = false, bool update = false,
        string summary = "", string authors = "", string[]? tags = null, DateTime? released = null, long size = 0, bool isNew = false)
        => new()
        {
            Identifier = id,
            Name = name,
            Downloads = downloads,
            IsCompatible = compatible,
            IsInstalled = installed,
            InstalledVersion = installed ? "1.0" : null,
            HasUpdate = update,
            Abstract = summary,
            Authors = authors,
            Tags = tags ?? Array.Empty<string>(),
            ReleaseDate = released,
            DownloadSize = size,
            IsNew = isNew,
        };

    private static readonly CatalogEntry[] Sample =
    {
        E("MechJeb2", "MechJeb 2", 22_072, summary: "Autopilot and info", authors: "Sarbian"),
        E("MechJebForAll", "MechJeb and Engineer for all!", 300_421, summary: "Adds MechJeb to every pod"),
        E("KerbalEngineerRedux", "Kerbal Engineer Redux", 1_916_962, installed: true, update: true, tags: new[] { "information" }),
        E("Scatterer", "Scatterer", 2_669_959, installed: true, summary: "Atmospheric scattering shaders", authors: "blackrack", tags: new[] { "graphics" }),
        E("Scatterer-sunflare", "Scatterer Sunflare", 2_668_472, tags: new[] { "graphics" }),
        E("OldMod", "Old Mod", 5_000, compatible: false, released: new DateTime(2016, 1, 1), size: 50),
        E("FreshMod", "Fresh Mod", 10, released: new DateTime(2026, 10, 1), size: 5, isNew: true),
    };

    [Fact]
    public void Normalize_keeps_only_letters_and_digits()
    {
        Assert.Equal("mechjeb2", CatalogIndex.Normalize("MechJeb-2!"));
        Assert.Equal(string.Empty, CatalogIndex.Normalize(null));
    }

    [Fact]
    public void Every_word_must_match_and_spacing_does_not_matter()
    {
        var index = new CatalogIndex(Sample);
        var hits = index.Query("mech jeb", CatalogFilter.All, null, CatalogSort.Popular).Select(e => e.Identifier).ToList();
        Assert.Equal(new[] { "MechJeb2", "MechJebForAll" }, hits);
        Assert.Empty(index.Query("mechjeb graphics", CatalogFilter.All, null, CatalogSort.Popular));
    }

    [Fact]
    public void Close_name_matches_rank_above_more_downloaded_partial_ones()
    {
        var index = new CatalogIndex(Sample);
        var hits = index.Query("scatterer", CatalogFilter.All, null, CatalogSort.Popular).Select(e => e.Identifier).ToList();
        Assert.Equal("Scatterer", hits[0]);
        Assert.Equal("Scatterer-sunflare", hits[1]);
    }

    [Fact]
    public void Authors_and_summary_are_searched()
    {
        var index = new CatalogIndex(Sample);
        Assert.Equal("Scatterer", Assert.Single(index.Query("blackrack", CatalogFilter.All, null, CatalogSort.Popular)).Identifier);
        Assert.Equal("MechJebForAll", Assert.Single(index.Query("every pod", CatalogFilter.All, null, CatalogSort.Popular)).Identifier);
    }

    [Fact]
    public void Filters_and_tags()
    {
        var index = new CatalogIndex(Sample);
        Assert.DoesNotContain(index.Query(null, CatalogFilter.Compatible, null, CatalogSort.Popular), e => e.Identifier == "OldMod");
        Assert.Equal(new[] { "Scatterer", "KerbalEngineerRedux" }, index.Query(null, CatalogFilter.Installed, null, CatalogSort.Popular).Select(e => e.Identifier));
        Assert.Equal("KerbalEngineerRedux", Assert.Single(index.Query(null, CatalogFilter.Updates, null, CatalogSort.Popular)).Identifier);
        Assert.Equal("OldMod", Assert.Single(index.Query(null, CatalogFilter.Incompatible, null, CatalogSort.Popular)).Identifier);
        Assert.Equal("FreshMod", Assert.Single(index.Query(null, CatalogFilter.New, null, CatalogSort.Popular)).Identifier);
        Assert.DoesNotContain(index.Query(null, CatalogFilter.NotInstalled, null, CatalogSort.Popular), e => e.IsInstalled);
        Assert.Equal(2, index.Query(null, CatalogFilter.All, "GRAPHICS", CatalogSort.Popular).Count);
    }

    [Fact]
    public void Sorts()
    {
        var index = new CatalogIndex(Sample);
        Assert.Equal("Scatterer", index.Query(null, CatalogFilter.All, null, CatalogSort.Popular)[0].Identifier);
        Assert.Equal("FreshMod", index.Query(null, CatalogFilter.All, null, CatalogSort.RecentlyUpdated)[0].Identifier);
        Assert.Equal("FreshMod", index.Query(null, CatalogFilter.All, null, CatalogSort.Name)[0].Identifier);
        Assert.Equal("OldMod", index.Query(null, CatalogFilter.All, null, CatalogSort.Size)[0].Identifier);
    }

    [Theory]
    [InlineData("3:v0.0878", "v0.0878")]
    [InlineData("1.1.9.5", "1.1.9.5")]
    [InlineData("2:EVE-1.2.2-1", "EVE-1.2.2-1")]
    [InlineData("v1:2", "v1:2")]
    [InlineData(null, "")]
    public void Display_version_drops_the_epoch(string? raw, string shown)
        => Assert.Equal(shown, CatalogFormat.DisplayVersion(raw));
}
