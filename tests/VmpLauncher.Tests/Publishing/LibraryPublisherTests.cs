using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using VmpLauncher.Ckan;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Security;
using VmpLauncher.Net.Library;
using VmpLauncher.Publishing.Library;
using VmpLauncher.Publishing.Publish;
using VmpLauncher.Tests.Net;
using Xunit;

namespace VmpLauncher.Tests.Publishing;

public sealed class LibraryPublisherTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private const string LibraryUrl = "https://example.test/library/library.json";

    /// <summary>CKAN reads its config path from the environment once per process: point it at a throwaway folder, never at the developer's own CKAN.</summary>
    private static readonly Lazy<string> CkanHome = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "vmpl-library-tests-ckan");
        CkanBootstrap.Initialize(new LauncherPaths(dir));
        return dir;
    });

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-library-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _packsRepo;
    private readonly string _exportRoot;
    private readonly string _definition;
    private readonly PublisherKey _key = PublisherKey.Generate("test-key");
    private readonly FakeHttp _http = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly PublishConfig _config;

    public LibraryPublisherTests()
    {
        _ = CkanHome.Value;
        _source = Path.Combine(_root, "mod-build");
        _packsRepo = Path.Combine(_root, "packs-repo");
        _exportRoot = Path.Combine(_root, "export");
        _definition = Path.Combine(_root, "library", "vlad.library.json");
        Directory.CreateDirectory(Path.Combine(_source, "Plugins"));
        Directory.CreateDirectory(_packsRepo);
        Directory.CreateDirectory(Path.GetDirectoryName(_definition)!);
        _config = new PublishConfig
        {
            PackFile = Path.Combine(_root, "p.vmpack.json"),
            HostKspDir = Path.Combine(_root, "ksp"),
            HostCacheDir = Path.Combine(_root, "cache"),
            PacksRepoDir = _packsRepo,
            ExportRoot = _exportRoot,
            PolicyFile = Path.Combine(_root, "KSPModControl.xml"),
            KeyId = "test-key",
        };
        WriteSource("plugin one");
        WriteDefinition();
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- the document ----

    [Fact]
    public void Index_parses_with_the_launchers_strict_parser_and_carries_every_field()
    {
        var report = Run();

        var index = LibraryIndex.Parse(File.ReadAllBytes(report.LibraryFile));
        Assert.Equal(1, index.Sequence);
        Assert.False(report.Unchanged);
        Assert.Equal("vlad", index.LibraryId);
        Assert.Equal("Vlad's mods", index.Name);
        Assert.Equal("Everything Vlad builds for KSP.", index.Tagline);
        Assert.Equal(Now, index.IssuedAt.ToUniversalTime());
        var module = Assert.Single(index.Modules);
        Assert.Equal("VladMultiplayer", module.Identifier);
        Assert.Equal("1.0.0", module.Version);
        var json = module.Json;
        Assert.Equal("Vlad Multiplayer", json.GetProperty("name").GetString());
        Assert.Equal("A test mod.\n\nSecond paragraph.", json.GetProperty("description").GetString());
        Assert.Equal("multiplayer,vlad", string.Join(",", json.GetProperty("tags").EnumerateArray().Select(t => t.GetString())));
        Assert.Equal("https://github.com/Rjwolfe44/VMP", json.GetProperty("resources").GetProperty("homepage").GetString());
        Assert.Equal("Harmony2", Assert.Single(json.GetProperty("depends").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("LunaMultiplayer,LunaCompat", string.Join(",", json.GetProperty("conflicts").EnumerateArray().Select(c => c.GetProperty("name").GetString())));
        Assert.False(json.TryGetProperty("ksp_version_min", out _));
        Assert.Equal("1.12", json.GetProperty("ksp_version").GetString());
        Assert.Equal(report.Modules[0].Download, json.GetProperty("download").GetString());
        var showcase = index.Showcase["VladMultiplayer"];
        Assert.Equal("Fly together.", showcase.Tagline);
        Assert.Equal("Career sync|Time warp", string.Join("|", showcase.Highlights));
        Assert.Equal("#7C5CFF", showcase.Accent);
        Assert.Equal(1, showcase.Order);
    }

    [Fact]
    public void Output_is_utf8_without_bom_lf_indented_and_deterministic()
    {
        var first = File.ReadAllBytes(Run().LibraryFile);

        Assert.False(first.Length > 3 && first[0] == 0xEF && first[1] == 0xBB && first[2] == 0xBF, "no byte order mark");
        Assert.DoesNotContain((byte)'\r', first);
        Assert.Equal((byte)'\n', first[^1]);
        Assert.StartsWith("{\n  \"schemaVersion\": 1,\n  \"libraryId\": \"vlad\",", Encoding.UTF8.GetString(first), StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Vlad's mods\"", Encoding.UTF8.GetString(first), StringComparison.Ordinal);

        // The same inputs in a fresh export folder give the same bytes: the zip and the module metadata are pure functions of the content.
        var second = File.ReadAllBytes(new LibraryPublisher(TextWriter.Null, _clock, new HttpClient(_http))
            .Run(new LibraryPublisher.Options(_config with { ExportRoot = Path.Combine(_root, "export-2") }, _definition, false, false), _key).LibraryFile);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Signature_verifies_with_the_key_and_fails_after_tampering_one_byte()
    {
        var report = Run();
        var verifier = new SignatureVerifier(new[] { _key.PublicInfo() });

        Assert.NotNull(report.SignatureFile);
        Assert.Equal(TrustLevel.VerifiedPublisher, verifier.VerifyFile(report.LibraryFile, out var keyId, out _));
        Assert.Equal("test-key", keyId);

        var tamperedDir = Path.Combine(_root, "tampered");
        Directory.CreateDirectory(tamperedDir);
        var bytes = File.ReadAllBytes(report.LibraryFile);
        var at = Encoding.UTF8.GetString(bytes).IndexOf("Fly together", StringComparison.Ordinal);
        bytes[at] = (byte)'G'; // "Gly together": still valid JSON, one byte different
        File.WriteAllBytes(Path.Combine(tamperedDir, "library.json"), bytes);
        File.Copy(report.SignatureFile!, Path.Combine(tamperedDir, "library.json.sig"));
        Assert.Equal(TrustLevel.Tampered, verifier.VerifyFile(Path.Combine(tamperedDir, "library.json"), out _, out var reason));
        Assert.Contains("does not match", reason, StringComparison.Ordinal);

        using var impostor = PublisherKey.Generate("test-key");
        var other = new SignatureVerifier(new[] { impostor.PublicInfo() });
        Assert.Equal(TrustLevel.Tampered, other.VerifyFile(report.LibraryFile, out _, out _));
    }

    [Fact]
    public void Generated_modules_satisfy_the_download_allow_list()
    {
        var report = Run();
        var index = LibraryIndex.Parse(File.ReadAllBytes(report.LibraryFile));

        foreach (var module in index.Modules)
        {
            var url = module.Json.GetProperty("download").GetString()!;
            Assert.StartsWith(LibraryIndex.AllowedDownloadPrefix, url, StringComparison.Ordinal);
            Assert.True(LibraryIndex.IsAllowedDownload(url, out var why), why);
            Assert.Equal("https://github.com/Rjwolfe44/VMP/releases/download/client-1.0.0/VladMultiplayer-1.0.0.zip", url);
            Assert.Equal(Sha256Util.HashFile(Path.Combine(_exportRoot, "library", "assets", "VladMultiplayer-1.0.0.zip")), module.Json.GetProperty("download_hash").GetProperty("sha256").GetString()!.ToLowerInvariant());
        }
    }

    // ---- sequence and history ----

    [Fact]
    public void Sequence_is_1_first_then_2_only_when_content_changed_and_old_versions_stay()
    {
        var first = Run();
        Assert.Equal(1, first.Sequence);
        Publish(first);

        WriteSource("plugin two");
        WriteDefinition(version: "1.1.0");
        _clock.Advance(TimeSpan.FromHours(2));
        var second = Run();

        Assert.False(second.Unchanged);
        Assert.Equal(2, second.Sequence);
        var index = LibraryIndex.Parse(File.ReadAllBytes(second.LibraryFile));
        Assert.Equal(2, index.Sequence);
        Assert.Equal(Now.AddHours(2), index.IssuedAt.ToUniversalTime());
        Assert.Equal("1.0.0 1.1.0", string.Join(" ", index.Modules.Select(m => m.Version)));
        Assert.Equal("False True", string.Join(" ", second.Modules.Select(m => m.Added)));
    }

    [Fact]
    public void An_unchanged_run_keeps_the_sequence_and_signs_nothing_new()
    {
        var first = Run();
        Publish(first);
        var publishedBytes = File.ReadAllBytes(Path.Combine(_packsRepo, "library", "library.json"));
        _clock.Advance(TimeSpan.FromDays(1));

        var again = Run();

        Assert.True(again.Unchanged);
        Assert.Equal(1, again.Sequence);
        Assert.Null(again.SignatureFile);
        Assert.Equal(Path.Combine(_packsRepo, "library", "library.json"), again.LibraryFile);
        Assert.False(File.Exists(Path.Combine(_exportRoot, "library", "library.json")), "no new library was staged");
        Assert.Equal(publishedBytes, File.ReadAllBytes(Path.Combine(_packsRepo, "library", "library.json")));
        Assert.DoesNotContain(again.Actions, a => a.Title.Contains("packs repository", StringComparison.Ordinal) || a.Title.Contains("(git)", StringComparison.Ordinal));
        Assert.All(again.Modules, m => Assert.False(m.Added));
    }

    [Fact]
    public void Changing_only_the_showcase_or_the_name_publishes_a_new_sequence()
    {
        Publish(Run());

        WriteDefinition(showcaseTagline: "Fly together, again.");
        var showcaseChange = Run();
        Assert.Equal(2, showcaseChange.Sequence);
        Assert.Equal("Fly together, again.", LibraryIndex.Parse(File.ReadAllBytes(showcaseChange.LibraryFile)).Showcase["VladMultiplayer"].Tagline);
        Publish(showcaseChange);

        WriteDefinition(showcaseTagline: "Fly together, again.", libraryName: "Vlad's KSP mods");
        var nameChange = Run();
        Assert.Equal(3, nameChange.Sequence);
        Assert.Equal("Vlad's KSP mods", LibraryIndex.Parse(File.ReadAllBytes(nameChange.LibraryFile)).Name);
    }

    [Fact]
    public void A_mod_whose_showcase_is_dropped_from_the_definition_loses_it_in_the_library()
    {
        Publish(Run());

        WriteDefinition(withShowcase: false);
        var report = Run();

        Assert.Equal(2, report.Sequence);
        var index = LibraryIndex.Parse(File.ReadAllBytes(report.LibraryFile));
        Assert.Empty(index.Showcase);
        Assert.Single(index.Modules);
    }

    [Fact]
    public void Published_versions_are_kept_and_ordered_by_version_not_by_text()
    {
        foreach (var version in "1.0.9 1.0.10 1.0.2".Split(' '))
        {
            WriteSource("plugin " + version);
            WriteDefinition(version: version);
            Publish(Run());
        }

        var index = LibraryIndex.Parse(File.ReadAllBytes(Path.Combine(_packsRepo, "library", "library.json")));
        Assert.Equal(3, index.Sequence);
        Assert.Equal("1.0.2 1.0.9 1.0.10", string.Join(" ", index.Modules.Select(m => m.Version)));
    }

    [Fact]
    public void Same_version_rebuilt_with_different_content_is_refused()
    {
        Publish(Run());
        WriteSource("plugin one, edited after release");

        var ex = Assert.Throws<LauncherException>(() => Run());

        Assert.Contains("VladMultiplayer 1.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("without a new version", ex.Message, StringComparison.Ordinal);
        Assert.Contains("download_hash", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_version_with_changed_metadata_is_refused_too()
    {
        Publish(Run());
        WriteDefinition(description: "A different description.");

        var ex = Assert.Throws<LauncherException>(() => Run());

        Assert.Contains("without a new version", ex.Message, StringComparison.Ordinal);
        Assert.Contains("description", ex.Message, StringComparison.Ordinal);
        Assert.Contains("metadata", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_published_library_that_is_not_valid_blocks_the_merge()
    {
        var library = Path.Combine(_packsRepo, "library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "library.json"), "{ \"schemaVersion\": 1 }");

        var ex = Assert.Throws<LauncherException>(() => Run());

        Assert.Contains("already in the packs repository", ex.Message, StringComparison.Ordinal);
    }

    // ---- what a library may contain ----

    [Theory]
    [InlineData("private-github")]
    [InlineData("private-sideload")]
    [InlineData("url")]
    [InlineData("ftp")]
    public void Anything_but_a_github_release_is_refused_before_anything_is_built(string kind)
    {
        WriteDefinition(kind: kind);

        var ex = Assert.Throws<LauncherException>(() => Run());

        Assert.Contains($"distribution kind '{kind}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("github-release", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_exportRoot, "library")), "nothing was built");
        if (kind.StartsWith("private", StringComparison.Ordinal))
        {
            Assert.Contains("private mod must never be listed in a public library", ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("SomeoneElse/Mod")]
    [InlineData("Rjwolfe44")]
    [InlineData("Rjwolfe44/a/b")]
    public void A_repository_outside_Vlads_account_is_refused(string repo)
    {
        WriteDefinition(repo: repo);

        var ex = Assert.Throws<LauncherException>(() => Run());

        Assert.Contains("distribution.repo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Rjwolfe44", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_for_another_library_or_schema_is_refused()
    {
        File.WriteAllText(_definition, File.ReadAllText(_definition).Replace("\"id\": \"vlad\"", "\"id\": \"someone\"", StringComparison.Ordinal).Replace("vmplib/1", "vmplib/9", StringComparison.Ordinal));

        var ex = Assert.Throws<LauncherException>(() => LibraryDefinition.Load(_definition));

        Assert.Contains("vmplib/9", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'vlad'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_definition_loads_and_lists_the_vmp_client()
    {
        var path = LibraryDefinition.FindNextTo(Path.Combine(AppContext.BaseDirectory, "x"));
        Assert.NotNull(path);

        var definition = LibraryDefinition.Load(path!);

        Assert.Equal("vlad", definition.Id);
        Assert.Equal("Vlad's mods", definition.Name);
        var mod = Assert.Single(definition.Mods);
        Assert.Equal("VladMultiplayer", mod.Package.Id);
        Assert.Equal("github-release", mod.Package.Distribution.Kind);
        Assert.Equal("Rjwolfe44/VMP", mod.Package.Distribution.Repo);
        Assert.Equal("client-", mod.Package.Distribution.TagPrefix);
        Assert.Equal("Plugins/LmpClient.dll", mod.Package.VersionFrom.AssemblyInformationalVersion);
        Assert.Contains("Harmony2", mod.Depends.Select(d => d.Name));
        Assert.Equal(1, mod.Showcase!.Order);
    }

    [Fact]
    public void The_default_definition_is_found_by_walking_up_from_the_pack_file()
    {
        var nested = Path.Combine(_root, "repo", "packs", "p");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(_root, "repo", "library"));
        File.WriteAllText(Path.Combine(_root, "repo", "library", "vlad.library.json"), "{}");

        Assert.Equal(Path.Combine(_root, "repo", "library", "vlad.library.json"), LibraryDefinition.FindNextTo(Path.Combine(nested, "p.vmpack.json")));
        Assert.Null(LibraryDefinition.FindNextTo(Path.Combine(Path.GetPathRoot(_root)!, "vmpl-no-such-folder", "x.json")));
    }

    // ---- the dry-run plan ----

    [Fact]
    public void A_missing_release_asset_is_planned_with_gh_and_a_present_identical_one_is_not()
    {
        var missing = Run();
        var asset = Assert.Single(missing.Actions, a => a.Title.StartsWith("Release asset for VladMultiplayer 1.0.0", StringComparison.Ordinal));
        Assert.Contains("gh release create client-1.0.0", asset.Commands[0], StringComparison.Ordinal);
        Assert.Contains("--repo Rjwolfe44/VMP", asset.Commands[0], StringComparison.Ordinal);
        Assert.Contains("gh release upload client-1.0.0", asset.Commands[1], StringComparison.Ordinal);

        ServeAsset("1.0.0", File.ReadAllBytes(Path.Combine(_exportRoot, "library", "assets", "VladMultiplayer-1.0.0.zip")));
        var present = Run();
        Assert.DoesNotContain(present.Actions, a => a.Title.StartsWith("Release asset", StringComparison.Ordinal));
        Assert.Equal(2, present.Actions.Count);
    }

    [Fact]
    public void A_release_asset_that_exists_with_different_bytes_is_refused()
    {
        ServeAsset("1.0.0", Encoding.UTF8.GetBytes("some other zip"));

        var ex = Assert.Throws<LauncherException>(() => Run());

        Assert.Contains("already exists with different content", ex.Message, StringComparison.Ordinal);
        Assert.Contains("without a new version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dry_run_lists_assets_then_copy_then_git_and_leaves_the_packs_repository_alone()
    {
        var report = Run();

        Assert.False(report.Executed);
        Assert.Null(report.PushedCommit);
        Assert.Null(report.Remote);
        Assert.Collection(report.Actions,
            asset => Assert.StartsWith("Release asset for VladMultiplayer 1.0.0", asset.Title, StringComparison.Ordinal),
            copyStep => Assert.Equal("Copy the library into the packs repository", copyStep.Title),
            gitStep => Assert.Equal("Publish the library (git)", gitStep.Title));
        var copy = report.Actions[1];
        Assert.Contains(Path.Combine(_exportRoot, "library", "library.json"), copy.Commands[0], StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_packsRepo, "library", "library.json.sig"), copy.Commands[1], StringComparison.Ordinal);
        var git = report.Actions[2].Commands;
        Assert.Contains("add library/library.json library/library.json.sig", git[0], StringComparison.Ordinal);
        Assert.Contains("commit -m", git[1], StringComparison.Ordinal);
        Assert.Contains("Vlad's mod library 1: VladMultiplayer 1.0.0", git[1], StringComparison.Ordinal);
        Assert.EndsWith("push origin main", git[2], StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_packsRepo, "library")), "the packs repository was not touched");
        Assert.True(File.Exists(Path.Combine(_exportRoot, "library", "library.json.sig")));
    }

    [Fact]
    public void The_copy_action_puts_the_signed_files_into_the_packs_repository_byte_for_byte()
    {
        var report = Run();
        var copy = Assert.Single(report.Actions, a => a.Title.StartsWith("Copy", StringComparison.Ordinal));

        copy.Run(new ExternalTools(TextWriter.Null));

        Assert.Equal(File.ReadAllBytes(report.LibraryFile), File.ReadAllBytes(Path.Combine(_packsRepo, "library", "library.json")));
        Assert.Equal(File.ReadAllBytes(report.SignatureFile!), File.ReadAllBytes(Path.Combine(_packsRepo, "library", "library.json.sig")));
        Assert.Equal(TrustLevel.VerifiedPublisher, new SignatureVerifier(new[] { _key.PublicInfo() }).VerifyFile(Path.Combine(_packsRepo, "library", "library.json"), out _, out _));
    }

    [Fact]
    public void A_key_the_launcher_does_not_pin_only_warns_in_a_dry_run_and_stops_an_execute_before_anything_runs()
    {
        var dry = Run();
        Assert.Contains(dry.Warnings, w => w.Contains("pinned", StringComparison.Ordinal));

        var pinned = new SignatureVerifier(new[] { _key.PublicInfo() });
        var quiet = new LibraryPublisher(TextWriter.Null, _clock, new HttpClient(_http), pinned).Run(new LibraryPublisher.Options(_config, _definition, false, false), _key);
        Assert.DoesNotContain(quiet.Warnings, w => w.Contains("pinned", StringComparison.Ordinal));

        // Execute is refused first: no git, no gh, no network.
        var ex = Assert.Throws<LauncherException>(() => new LibraryPublisher(TextWriter.Null, _clock, new HttpClient(_http)).Run(new LibraryPublisher.Options(_config, _definition, true, false), _key));
        Assert.Contains("pinned publisher keys", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was published", ex.Message, StringComparison.Ordinal);
    }

    // ---- the check of what friends get ----

    [Fact]
    public async Task Remote_check_accepts_what_the_publisher_signed_and_lists_the_mods()
    {
        var report = Run();
        ServeLibrary(report);
        ServeDownloads(report);

        var result = await Remote().VerifyAsync(LibraryUrl, 1, Sha256Util.HashFile(report.LibraryFile));

        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Equal(1, result.Sequence);
        var module = Assert.Single(result.Modules);
        Assert.Equal("VladMultiplayer", module.Identifier);
        Assert.Equal("1.0.0", module.Version);
        Assert.StartsWith("https://github.com/Rjwolfe44/VMP/releases/download/client-1.0.0/", module.Download, StringComparison.Ordinal);
        Assert.Contains(result.Lines, l => l.Contains("Verified publisher (test-key)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remote_check_rejects_tampered_bytes_a_wrong_sequence_and_an_unreachable_download()
    {
        var report = Run();
        var bytes = File.ReadAllBytes(report.LibraryFile);
        var at = Encoding.UTF8.GetString(bytes).IndexOf("A test mod.", StringComparison.Ordinal);
        var tampered = (byte[])bytes.Clone();
        tampered[at] = (byte)'B';
        _http.Routes[LibraryUrl] = _ => Bytes(tampered);
        _http.Routes[LibraryUrl + ".sig"] = _ => Bytes(File.ReadAllBytes(report.SignatureFile!));
        ServeDownloads(report);

        var bad = await Remote().VerifyAsync(LibraryUrl);
        Assert.False(bad.Ok);
        Assert.Contains(bad.Problems, p => p.Contains("Tampered", StringComparison.Ordinal));

        ServeLibrary(report);
        var wrongSequence = await Remote().VerifyAsync(LibraryUrl, 5);
        Assert.Contains(wrongSequence.Problems, p => p.Contains("sequence 1, expected 5", StringComparison.Ordinal));

        _http.Routes.Remove(report.Modules[0].Download);
        var noDownload = await Remote().VerifyAsync(LibraryUrl, 1);
        Assert.Contains(noDownload.Problems, p => p.Contains("not reachable", StringComparison.Ordinal) && p.Contains("404", StringComparison.Ordinal));

        _http.Routes.Remove(LibraryUrl + ".sig");
        var noSignature = await Remote().VerifyAsync(LibraryUrl);
        Assert.Contains(noSignature.Problems, p => p.Contains("no .sig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remote_check_waits_for_the_cdn_to_serve_the_new_file()
    {
        var first = Run();
        var firstBytes = File.ReadAllBytes(first.LibraryFile);
        var firstSig = File.ReadAllBytes(first.SignatureFile!);
        Publish(first);
        WriteSource("plugin two");
        WriteDefinition(version: "1.1.0");
        var second = Run();
        var secondBytes = File.ReadAllBytes(second.LibraryFile);
        var secondSig = File.ReadAllBytes(second.SignatureFile!);
        ServeDownloads(second);

        var delays = 0;
        _http.Routes[LibraryUrl] = _ => Bytes(delays >= 2 ? secondBytes : firstBytes);
        _http.Routes[LibraryUrl + ".sig"] = _ => Bytes(delays >= 2 ? secondSig : firstSig);
        var verifier = new LibraryRemoteVerifier(new HttpClient(_http), new SignatureVerifier(new[] { _key.PublicInfo() }), (_, _) => { delays++; return Task.CompletedTask; });

        var result = await verifier.VerifyWithRetryAsync(LibraryUrl, 2, Sha256Util.HashBytes(secondBytes), attempts: 5, interval: TimeSpan.FromSeconds(15));

        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Equal(2, delays);
        Assert.Equal(2, result.Sequence);
        Assert.Equal(2, result.Modules.Count);

        // It gives up after the attempts it was given and says why.
        delays = -100;
        var never = await verifier.VerifyWithRetryAsync(LibraryUrl, 2, Sha256Util.HashBytes(secondBytes), attempts: 3, interval: TimeSpan.FromSeconds(1));
        Assert.False(never.Ok);
        Assert.Contains(never.Problems, p => p.Contains("sequence 1, expected 2", StringComparison.Ordinal));
        Assert.Equal(-98, delays);
    }

    // ---- helpers ----

    private LibraryPublisher.Report Run()
        => new LibraryPublisher(TextWriter.Null, _clock, new HttpClient(_http)).Run(new LibraryPublisher.Options(_config, _definition, false, false), _key);

    /// <summary>What the git push would do: the signed files end up in the packs repository.</summary>
    private void Publish(LibraryPublisher.Report report)
    {
        Directory.CreateDirectory(Path.Combine(_packsRepo, "library"));
        File.Copy(report.LibraryFile, Path.Combine(_packsRepo, "library", "library.json"), overwrite: true);
        File.Copy(report.SignatureFile!, Path.Combine(_packsRepo, "library", "library.json.sig"), overwrite: true);
    }

    private LibraryRemoteVerifier Remote()
        => new(new HttpClient(_http), new SignatureVerifier(new[] { _key.PublicInfo() }), (_, _) => Task.CompletedTask);

    private void ServeLibrary(LibraryPublisher.Report report)
    {
        _http.Routes[LibraryUrl] = _ => Bytes(File.ReadAllBytes(report.LibraryFile));
        _http.Routes[LibraryUrl + ".sig"] = _ => Bytes(File.ReadAllBytes(report.SignatureFile!));
    }

    private void ServeDownloads(LibraryPublisher.Report report)
    {
        foreach (var module in report.Modules)
        {
            _http.Routes[module.Download] = _ => Bytes(Encoding.UTF8.GetBytes("zip"));
        }
    }

    private void ServeAsset(string version, byte[] zip)
        => _http.Routes[$"https://github.com/Rjwolfe44/VMP/releases/download/client-{version}/VladMultiplayer-{version}.zip"] = _ => Bytes(zip);

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private void WriteSource(string pluginContent)
    {
        File.WriteAllText(Path.Combine(_source, "Plugins", "Mod.dll"), pluginContent);
        File.WriteAllText(Path.Combine(_source, "License.txt"), "MIT");
        Directory.CreateDirectory(Path.Combine(_source, "logs"));
        File.WriteAllText(Path.Combine(_source, "logs", "today.log"), "never shipped " + Guid.NewGuid());
    }

    private void WriteDefinition(string version = "1.0.0", string kind = "github-release", string repo = "Rjwolfe44/VMP", string description = "A test mod.\n\nSecond paragraph.",
        string showcaseTagline = "Fly together.", string libraryName = "Vlad's mods", bool withShowcase = true)
    {
        var showcase = withShowcase ? $$"""
            ,
                  "showcase": {
                    "tagline": {{JsonSerializer.Serialize(showcaseTagline)}},
                    "highlights": [ "Career sync", "Time warp" ],
                    "accent": "#7C5CFF",
                    "order": 1
                  }
            """ : string.Empty;
        var json = $$"""
            {
              "schema": "vmplib/1",
              "id": "vlad",
              "name": {{JsonSerializer.Serialize(libraryName)}},
              "tagline": "Everything Vlad builds for KSP.",
              "mods": [
                {
                  "package": {
                    "id": "VladMultiplayer",
                    "name": "Vlad Multiplayer",
                    "abstract": "Play KSP with friends.",
                    "license": "MIT",
                    "sourceDir": {{JsonSerializer.Serialize(_source)}},
                    "installAs": "GameData/VladMultiplayer",
                    "versionFrom": { "fixed": "{{version}}" },
                    "exclude": [ "logs/**" ],
                    "conflicts": [ "LunaMultiplayer", "LunaCompat" ],
                    "distribution": { "kind": "{{kind}}", "repo": "{{repo}}", "tagPrefix": "client-" }
                  },
                  "description": {{JsonSerializer.Serialize(description)}},
                  "tags": [ "multiplayer", "vlad" ],
                  "resources": {
                    "homepage": "https://github.com/Rjwolfe44/VMP",
                    "bugtracker": "https://github.com/Rjwolfe44/VMP/issues"
                  },
                  "depends": [ { "name": "Harmony2" } ]{{showcase}}
                }
              ]
            }
            """;
        File.WriteAllText(_definition, json, new UTF8Encoding(false));
    }
}
