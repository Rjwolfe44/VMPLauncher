using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Net.Library;
using Xunit;

namespace VmpLauncher.Tests.Net;

/// <summary>Vlad's mod library: what the launcher accepts from a signed index, and how it is cached and protected from rollback.</summary>
public sealed class LibraryIndexTests : IDisposable
{
    private const string Url = "https://example.test/library/library.json";
    private const string Good = "https://github.com/Rjwolfe44/VMP/releases/download/client-0.2.0-train.1/VladMultiplayer-0.2.0-train.1.zip";
    private static readonly string Sha = new('a', 64);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-library-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeHttp _http = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Module(string id = "VladMultiplayer", string version = "0.2.0-train.1", string download = Good, string install = """[ { "find": "VladMultiplayer", "install_to": "GameData" } ]""",
        string hash = "", string extra = "", string kind = "package")
        => $$"""
            {
              "spec_version": "v1.34", "identifier": "{{id}}", "name": "Vlad Multiplayer", "abstract": "Play KSP with friends.", "author": "Rjwolfe44",
              "license": "MIT", "version": "{{version}}", "ksp_version": "1.12", "kind": "{{kind}}",
              "download": "{{download}}", "download_size": 941125, "download_hash": {{(hash.Length > 0 ? hash : $$"""{ "sha256": "{{Sha}}" }""")}},
              "install": {{install}}{{extra}}
            }
            """;

    private static string Doc(string modules, long sequence = 1, string showcase = "", string root = "", int schema = 1, string id = "vlad")
        => $$"""
            {
              "schemaVersion": {{schema}}, "libraryId": "{{id}}", "name": "Vlad's mods", "tagline": "Everything Vlad builds.", "sequence": {{sequence}}, "issuedAt": "2026-10-24T12:00:00Z"{{root}},
              "showcase": {{(showcase.Length > 0 ? showcase : "{}")}},
              "modules": [ {{modules}} ]
            }
            """;

    private static LibraryIndex Parse(string json) => LibraryIndex.Parse(Encoding.UTF8.GetBytes(json));

    private static LauncherException Refused(string json) => Assert.Throws<LauncherException>(() => Parse(json));

    [Fact]
    public void A_good_library_parses_with_its_showcase_in_order()
    {
        var index = Parse(Doc(Module() + "," + Module("VladTools", "1.0.0", "https://github.com/Rjwolfe44/Tools/releases/download/v1.0.0/Tools-1.0.0.zip"), 7,
            """{ "VladTools": { "tagline": "Second", "order": 2 }, "VladMultiplayer": { "tagline": "Fly together.", "highlights": ["One", "Two"], "accent": "#7C5CFF", "order": 1 } }"""));

        Assert.Equal(7, index.Sequence);
        Assert.Equal("Vlad's mods", index.Name);
        Assert.Equal(new[] { "VladMultiplayer", "VladTools" }, index.Identifiers);
        Assert.Equal("Fly together.", index.Showcase["VladMultiplayer"].Tagline);
        Assert.Equal(new[] { "One", "Two" }, index.Showcase["VladMultiplayer"].Highlights);
        Assert.Equal("#7C5CFF", index.Showcase["VladMultiplayer"].Accent);
        Assert.Equal(2, index.Modules.Count);
    }

    [Theory]
    [InlineData("https://evil.example/VladMultiplayer.zip")]
    [InlineData("http://github.com/Rjwolfe44/VMP/releases/download/a/b.zip")]
    [InlineData("https://github.com/SomeoneElse/VMP/releases/download/a/b.zip")]
    [InlineData("https://github.com/Rjwolfe44/VMP/archive/main.zip")]
    [InlineData("https://github.com/Rjwolfe44/VMP/releases/download/a/b.zip?token=1")]
    [InlineData("https://github.com.evil.example/Rjwolfe44/VMP/releases/download/a/b.zip")]
    [InlineData("https://user@github.com/Rjwolfe44/VMP/releases/download/a/../../../b.zip")]
    [InlineData("https://github.com:8443/Rjwolfe44/VMP/releases/download/a/b.zip")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public void Downloads_may_only_come_from_Vlads_own_GitHub_releases(string download)
    {
        var ex = Refused(Doc(Module(download: download)));

        Assert.Equal(ErrorCode.InvalidPack, ex.Code);
        Assert.Contains("download", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[ { "find": "X", "install_to": "GameData/../../Windows" } ]""")]
    [InlineData("""[ { "find": "X", "install_to": "C:/Windows" } ]""")]
    [InlineData("""[ { "find": "X", "install_to": "/etc" } ]""")]
    [InlineData("""[ { "find": "X", "install_to": "Plugins" } ]""")]
    [InlineData("""[ { "find": "../X", "install_to": "GameData" } ]""")]
    [InlineData("""[ { "file": "/abs/X", "install_to": "GameData" } ]""")]
    [InlineData("""[ { "find": "X", "install_to": "GameData", "as": "../../evil" } ]""")]
    [InlineData("""[ { "install_to": "GameData" } ]""")]
    [InlineData("""[ { "find": "X", "file": "Y", "install_to": "GameData" } ]""")]
    [InlineData("""[ { "find": "X", "install_to": "GameData", "run": "cmd /c calc" } ]""")]
    [InlineData("[]")]
    public void Installs_stay_inside_the_game_folder(string install)
    {
        var ex = Refused(Doc(Module(install: install)));

        Assert.Equal(ErrorCode.InvalidPack, ex.Code);
    }

    [Theory]
    [InlineData("""[ { "find": "VladMultiplayer", "install_to": "GameData" } ]""")]
    [InlineData("""[ { "find": "VladMultiplayer", "install_to": "GameData/VladMultiplayer/Plugins" } ]""")]
    [InlineData("""[ { "file": "Ships/VAB/My Rocket.craft", "install_to": "Ships/VAB" } ]""")]
    [InlineData("""[ { "find": "Scenario", "install_to": "Scenarios" } ]""")]
    public void The_usual_installs_are_fine(string install) => Assert.Single(Parse(Doc(Module(install: install))).Modules);

    [Fact]
    public void A_download_needs_a_real_sha256_and_a_size()
    {
        Assert.Contains("sha256", Refused(Doc(Module(hash: """{ "sha1": "ABC" }"""))).Message, StringComparison.Ordinal);
        Assert.Contains("sha256", Refused(Doc(Module(hash: """{ "sha256": "xyz" }"""))).Message, StringComparison.Ordinal);
        Assert.Contains("download_size", Refused(Doc(Module().Replace("\"download_size\": 941125", "\"download_size\": 0", StringComparison.Ordinal))).Message, StringComparison.Ordinal);
        Assert.Contains("download_size", Refused(Doc(Module().Replace("\"download_size\": 941125", "\"download_size\": 99999999999", StringComparison.Ordinal))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("-x")]
    [InlineData("")]
    public void Identifiers_must_be_plain(string id) => Assert.Equal(ErrorCode.InvalidPack, Refused(Doc(Module(id: id))).Code);

    [Fact]
    public void Only_packages_are_allowed_and_each_release_is_listed_once()
    {
        Assert.Contains("packages", Refused(Doc(Module(kind: "metapackage"))).Message, StringComparison.Ordinal);
        Assert.Contains("twice", Refused(Doc(Module() + "," + Module())).Message, StringComparison.Ordinal);
        Assert.Equal(2, Parse(Doc(Module() + "," + Module(version: "0.2.0-train.2"))).Modules.Count);
    }

    [Theory]
    [InlineData(""", "password": "hunter2" """)]
    [InlineData(""", "adminPassphrase": "x" """)]
    public void A_library_never_carries_secrets(string root) => Assert.Equal(ErrorCode.Tampered, Refused(Doc(Module(), root: root)).Code);

    [Fact]
    public void The_schema_library_id_and_size_are_checked()
    {
        Assert.Contains("schemaVersion", Refused(Doc(Module(), schema: 2)).Message, StringComparison.Ordinal);
        Assert.Throws<LauncherException>(() => Parse("not json"));
        Assert.Throws<LauncherException>(() => Parse("[]"));
        var huge = Doc(Module(extra: ", \"description\": \"" + new string('x', LibraryIndex.MaxBytes) + "\""));
        Assert.Contains("limit", Refused(huge).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Showcase_text_pictures_and_colours_are_checked()
    {
        Assert.Contains("not in the library", Refused(Doc(Module(), showcase: """{ "Other": { "tagline": "x" } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("accent", Refused(Doc(Module(), showcase: """{ "VladMultiplayer": { "accent": "red" } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("banner", Refused(Doc(Module(), showcase: """{ "VladMultiplayer": { "banner": "https://evil.example/x.png" } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("highlights", Refused(Doc(Module(), showcase: """{ "VladMultiplayer": { "highlights": ["1","2","3","4","5","6","7"] } }""")).Message, StringComparison.Ordinal);
        var ok = Parse(Doc(Module(), showcase: """{ "VladMultiplayer": { "banner": "https://raw.githubusercontent.com/Rjwolfe44/VMP-Packs/main/library/banner.png" } }"""));
        Assert.NotNull(ok.Showcase["VladMultiplayer"].Banner);
    }

    [Fact]
    public void Relations_and_links_are_checked_too()
    {
        Assert.Single(Parse(Doc(Module(extra: ", \"depends\": [ { \"name\": \"Harmony2\" }, { \"any_of\": [ { \"name\": \"A\" }, { \"name\": \"B\" } ] } ], \"resources\": { \"homepage\": \"https://github.com/Rjwolfe44/VMP\" }, \"tags\": [\"multiplayer\"]"))).Modules);
        Assert.Contains("depends", Refused(Doc(Module(extra: ", \"depends\": [ { \"name\": \"../evil\" } ]"))).Message, StringComparison.Ordinal);
        Assert.Contains("resources", Refused(Doc(Module(extra: ", \"resources\": { \"homepage\": \"javascript:alert(1)\" }"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_install_step_without_install_to_means_GameData()
        => Assert.Single(Parse(Doc(Module(install: """[ { "find": "VladMultiplayer" } ]"""))).Modules);

    [Fact]
    public void A_mod_whose_name_contains_pass_is_fine_but_secret_keys_are_not()
    {
        var ok = Parse(Doc(Module("Compass"), showcase: """{ "Compass": { "tagline": "Find your way." } }"""));
        Assert.Equal("Find your way.", ok.Showcase["Compass"].Tagline);

        // The exception is only for the showcase's own keys, one level down.
        Assert.Equal(ErrorCode.Tampered, Refused(Doc(Module("Compass"), showcase: """{ "Compass": { "password": "x" } }""")).Code);
        Assert.Equal(ErrorCode.Tampered, Refused(Doc(Module("Compass", extra: ", \"adminPassword\": \"x\""))).Code);
        Assert.Equal(ErrorCode.Tampered, Refused(Doc(Module("Compass"), root: ", \"passphrase\": \"x\"")).Code);
    }

    // ---- the client: verification, cache, rollback ----

    private LibraryClient Client() => new(new LauncherPaths(_root), new HttpClient(_http), new HashVerifier(), _time);

    private void Serve(string json, string? signature = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        _http.Routes[Url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        _http.Routes[Url + ".sig"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature ?? HashVerifier.Sign(bytes)) };
    }

    private void GoOffline()
    {
        _http.Routes.Clear();
    }

    [Fact]
    public async Task A_verified_library_is_cached_and_used_when_offline()
    {
        Serve(Doc(Module(), 3));
        var first = await Client().FetchAsync(new Uri(Url));
        Assert.False(first.IsFromCache);
        Assert.Equal("test-key", first.KeyId);
        Assert.Equal(3, first.Index.Sequence);

        GoOffline();
        var offline = await Client().FetchAsync(new Uri(Url));

        Assert.True(offline.IsFromCache);
        Assert.Equal(3, offline.Index.Sequence);
        Assert.Contains("saved list", offline.Warning, StringComparison.Ordinal);
        Assert.NotNull(Client().LoadCached());
    }

    [Fact]
    public async Task A_wrong_signature_is_refused_and_nothing_is_cached()
    {
        Serve(Doc(Module(), 1), signature: """{"sha256":"0000"}""");

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Client().FetchAsync(new Uri(Url)));

        Assert.Equal(ErrorCode.Tampered, ex.Code);
        Assert.Null(Client().LoadCached());
    }

    [Fact]
    public async Task A_signed_but_malformed_library_is_refused()
    {
        Serve(Doc(Module(download: "https://evil.example/x.zip"), 1));

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Client().FetchAsync(new Uri(Url)));

        Assert.Equal(ErrorCode.InvalidPack, ex.Code);
        Assert.Null(Client().LoadCached());
    }

    [Fact]
    public async Task An_older_library_never_replaces_a_newer_one()
    {
        Serve(Doc(Module(), 5));
        await Client().FetchAsync(new Uri(Url));

        Serve(Doc(Module(version: "0.1.0-old"), 4));
        var result = await Client().FetchAsync(new Uri(Url));

        Assert.True(result.IsFromCache);
        Assert.Equal(5, result.Index.Sequence);
        Assert.Contains("not newer", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newer_library_replaces_the_cache()
    {
        Serve(Doc(Module(), 1));
        await Client().FetchAsync(new Uri(Url));

        Serve(Doc(Module() + "," + Module(version: "0.2.0-train.2"), 2));
        var result = await Client().FetchAsync(new Uri(Url));

        Assert.False(result.IsFromCache);
        Assert.Equal(2, result.Index.Sequence);
        Assert.Equal(2, Client().LoadCached()!.Index.Modules.Count);
    }

    [Fact]
    public async Task A_library_for_another_id_is_refused()
    {
        Serve(Doc(Module(), 1, id: "someone-else"));

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Client().FetchAsync(new Uri(Url)));

        Assert.Equal(ErrorCode.Tampered, ex.Code);
    }

    [Fact]
    public void A_folder_the_host_published_loads_with_the_same_checks()
    {
        var folder = Path.Combine(_root, "published");
        System.IO.Directory.CreateDirectory(folder);
        var bytes = Encoding.UTF8.GetBytes(Doc(Module(), 9));
        File.WriteAllBytes(Path.Combine(folder, "library.json"), bytes);
        File.WriteAllText(Path.Combine(folder, "library.json.sig"), HashVerifier.Sign(bytes));

        var result = Client().LoadFromFolder(folder);

        Assert.Equal(9, result.Index.Sequence);
        File.WriteAllText(Path.Combine(folder, "library.json.sig"), """{"sha256":"bad"}""");
        Assert.Throws<LauncherException>(() => { Client().LoadFromFolder(folder); });
    }
}
