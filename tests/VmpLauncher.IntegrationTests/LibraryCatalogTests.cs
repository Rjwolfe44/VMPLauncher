using System.IO;
using System.Text.Json.Nodes;
using CKAN;
using VmpLauncher.Ckan.Catalog;
using VmpLauncher.Ckan.Repo;
using VmpLauncher.Core.Catalog;
using VmpLauncher.IntegrationTests.Fixtures;
using Xunit;

namespace VmpLauncher.IntegrationTests;

/// <summary>Vlad's library as a second CKAN repository, against the fixture CKAN universe and the real CKAN engine.</summary>
public sealed class LibraryCatalogTests : IDisposable
{
    private readonly TestWorld _w = new("library");
    private readonly CatalogService _svc;
    private readonly CatalogTarget _target;

    public LibraryCatalogTests()
    {
        var latest = _w.Repo.All.GroupBy(m => m.Id).SelectMany(g => g).Select(m => m.Ckan).ToList();
        var zip = PackRepoBuilder.Build(latest, Path.Combine(_w.Root, "repo", "fixture.zip"));
        var repo = new Repository("fixture", new Uri(zip));
        _svc = _w.Worker.Run(() => new CatalogService(_w.Paths, _w.CacheDir, repo, Path.Combine(_w.Root, "repos")));
        _target = new CatalogTarget(_w.Profile.GameDir, "test", ProfileRoot: _w.Profile.Root);
        _w.Worker.Run(() => _svc.Update(force: true, null, CancellationToken.None));
    }

    public void Dispose()
    {
        _w.Worker.Run(() => _svc.Dispose());
        _w.Dispose();
    }

    private T Run<T>(Func<CatalogService, T> f) => _w.Worker.Run(() => f(_svc));

    private CatalogEntry? Entry(string id) => Run(s => s.Build(_target)).Entries.SingleOrDefault(e => e.Identifier == id);

    /// <summary>A fixture mod re-issued as a library mod under a new identifier (the fixture's download server stands in for GitHub).</summary>
    private VladLibrary Library(string identifier = "VladF", string source = "F", string version = "1.0")
    {
        var module = _w.Repo.Get(source, version).Ckan;
        var json = JsonNode.Parse(module.ToJson())!.AsObject();
        json["identifier"] = identifier;
        json["name"] = "Vlad " + source;
        return new VladLibrary("Vlad's mods", "Everything Vlad builds.", 1, DateTimeOffset.UtcNow,
            new[] { new VladLibraryModule(identifier, version, json.ToJsonString()) },
            new Dictionary<string, VladShowcase> { [identifier] = new VladShowcase("Fly together.", new[] { "One", "Two" }, "#7C5CFF", 1, null) });
    }

    [Fact]
    public void Library_mods_join_the_catalog_with_their_showcase()
    {
        Assert.Null(Entry("VladF"));

        var outcome = Run(s => s.UseLibrary(Library()));

        Assert.True(outcome.Changed);
        Assert.Equal(1, outcome.Mods);
        Assert.Empty(outcome.Skipped);
        var entry = Entry("VladF");
        Assert.NotNull(entry);
        Assert.True(entry!.IsVlad);
        Assert.Equal("Fly together.", entry.Vlad!.Tagline);
        Assert.Equal(new[] { "One", "Two" }, entry.Vlad.Highlights);
        Assert.Equal("Vlad F", entry.Name);
        Assert.Single(Run(s => s.Build(_target)).Entries, e => e.IsVlad);
        Assert.False(Entry("F")!.IsVlad);
    }

    [Fact]
    public void An_unchanged_library_is_not_loaded_again()
    {
        Run(s => s.UseLibrary(Library()));

        var again = Run(s => s.UseLibrary(Library()));

        Assert.False(again.Changed);
        Assert.Equal(1, again.Mods);
    }

    [Fact]
    public void A_library_cannot_shadow_a_mod_from_the_standard_catalog()
    {
        var outcome = Run(s => s.UseLibrary(Library(identifier: "A", source: "F")));

        Assert.Equal(0, outcome.Mods);
        Assert.Single(outcome.Skipped);
        Assert.Contains("already has", outcome.Skipped[0], StringComparison.Ordinal);
        Assert.False(Entry("A")!.IsVlad);
        Assert.Equal("1.1", Entry("A")!.LatestVersion);
    }

    [Fact]
    public void A_library_mod_installs_like_any_other_and_the_registry_never_mentions_the_library()
    {
        Run(s => s.UseLibrary(Library()));

        var report = Run(s => s.Apply(_target, new ChangeRequest { Install = new[] { "VladF" } }, null, CancellationToken.None));

        Assert.Equal(1, report.Installed);
        Assert.True(File.Exists(_w.GamePath("GameData/F/F.cfg")));
        Assert.True(Entry("VladF")!.IsInstalled);
        var registry = File.ReadAllText(_w.GamePath("CKAN/registry.json"));
        Assert.DoesNotContain(CatalogService.LibraryRepoName, registry, StringComparison.Ordinal);
        Assert.True(_w.SentinelIntact());
    }

    [Fact]
    public void Taking_the_library_away_removes_its_mods_from_the_catalog()
    {
        Run(s => s.UseLibrary(Library()));
        Assert.NotNull(Entry("VladF"));

        var outcome = Run(s => s.UseLibrary(null));

        Assert.True(outcome.Changed);
        Assert.Null(Entry("VladF"));
        Assert.False(Run(s => s.HasLibrary));
    }

    [Fact]
    public void A_new_release_in_the_library_shows_as_the_newest_version()
    {
        var first = Library();
        Run(s => s.UseLibrary(first));
        var second = _w.Repo.Get("F", "1.0").Ckan;
        var newer = JsonNode.Parse(second.ToJson())!.AsObject();
        newer["identifier"] = "VladF";
        newer["version"] = "2.0";
        var library = first with
        {
            Sequence = 2,
            Modules = first.Modules.Append(new VladLibraryModule("VladF", "2.0", newer.ToJsonString())).ToList(),
        };

        var outcome = Run(s => s.UseLibrary(library));

        Assert.True(outcome.Changed);
        Assert.Equal("2.0", Entry("VladF")!.LatestVersion);
    }
}
