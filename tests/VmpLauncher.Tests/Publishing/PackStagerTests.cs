using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Publishing.Publish;
using Xunit;

namespace VmpLauncher.Tests.Publishing;

public sealed class PackStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-stager-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _exportRoot;
    private readonly PublisherKey _key = PublisherKey.Generate("test-key");
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);

    public PackStagerTests()
    {
        _repo = Path.Combine(_root, "repo");
        _exportRoot = Path.Combine(_root, "export");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void First_version_creates_layout_catalog_share_and_gitattributes()
    {
        var lk = Lock("1.0.0", ("A", "1.0", "aaa"), ("VMPCustom-Client", "0.1", "ccc"));
        var export = WriteExport(lk);
        var r = new PackStager().Stage(new PackStager.Request(_repo, export.VersionDir, export.ChannelFile, lk, "First release", "unchanged", _key, "https://example.test/catalog.json", Now));

        Assert.Null(r.PreviousLock);
        Assert.True(File.Exists(Path.Combine(_repo, "packs", "p", "1.0.0", "p.vmpack.lock.json")));
        Assert.True(File.Exists(Path.Combine(_repo, "packs", "p", "1.0.0", "p.vmpack.lock.json.sig")));
        Assert.True(File.Exists(Path.Combine(_repo, "packs", "p", "1.0.0", "inventory.json.gz")));
        Assert.True(File.Exists(Path.Combine(_repo, "packs", "p", "channel.json.sig")));
        Assert.True(File.Exists(Path.Combine(_repo, "packs", "p", "KSPModControl.xml")));
        Assert.True(File.Exists(Path.Combine(_repo, "packs", "p", "share", "p.vmpack")));
        Assert.Contains("* -text", File.ReadAllText(Path.Combine(_repo, ".gitattributes")), StringComparison.Ordinal);
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(_repo, "catalog.json"))) as JsonArray;
        Assert.Equal("p", catalog![0]!["id"]!.GetValue<string>());
        var changelog = File.ReadAllText(Path.Combine(_repo, "packs", "p", "CHANGELOG.md"));
        Assert.StartsWith("# Pack P\n\n## 1.0.0 (2026-10-23)\n\nFirst release\n", changelog, StringComparison.Ordinal);
        Assert.Contains("First published version", changelog, StringComparison.Ordinal);
        Assert.True(r.PolicyChanged);
    }

    [Fact]
    public void Second_version_diffs_changelog_bumps_servers_json_and_refuses_same_version()
    {
        var v1 = Lock("1.0.0", ("A", "1.0", "aaa"), ("B", "2.0", "bbb"), ("VMPCustom-Client", "0.1", "ccc"));
        var e1 = WriteExport(v1);
        new PackStager().Stage(new PackStager.Request(_repo, e1.VersionDir, e1.ChannelFile, v1, null, "unchanged", _key, null, Now));
        var servers = Path.Combine(_repo, "packs", "p", "servers.json");
        File.WriteAllText(servers, """{"schemaVersion":1,"packId":"p","sequence":4,"issuedAt":"2026-01-01T00:00:00Z","server":{"requiredBuild":"0.1"},"routes":[]}""");
        _key.SignFile(servers);

        var v2 = Lock("1.0.1", ("A", "1.1", "aa2"), ("C", "3.0", "ccc3"), ("VMPCustom-Client", "0.2", "ccc2"), ("VMPCustom-Client", "0.2", "ccc2")) with { };
        v2 = Lock("1.0.1", ("A", "1.1", "aa2"), ("C", "3.0", "ccc3"), ("VMPCustom-Client", "0.2", "ccc2"));
        var e2 = WriteExport(v2, policyBytes: "<ModControlStructure>v2</ModControlStructure>");
        var r = new PackStager().Stage(new PackStager.Request(_repo, e2.VersionDir, e2.ChannelFile, v2, "Client 0.2", "1 hash(es) updated", _key, null, Now.AddDays(1)));

        Assert.NotNull(r.Diff);
        Assert.Equal("1.0.0", r.PreviousLock!.Pack.Version);
        var changelog = File.ReadAllText(Path.Combine(_repo, "packs", "p", "CHANGELOG.md"));
        Assert.Contains("## 1.0.1 (2026-10-24)", changelog, StringComparison.Ordinal);
        Assert.Contains("- Added: C 3.0", changelog, StringComparison.Ordinal);
        Assert.Contains("- Updated: A 1.0 -> 1.1", changelog, StringComparison.Ordinal);
        Assert.Contains("- Removed: B 2.0", changelog, StringComparison.Ordinal);
        Assert.Contains("- Server policy: 1 hash(es) updated.", changelog, StringComparison.Ordinal);
        Assert.True(changelog.IndexOf("## 1.0.1", StringComparison.Ordinal) < changelog.IndexOf("## 1.0.0", StringComparison.Ordinal), "newest entry first");
        Assert.True(r.PolicyChanged);

        Assert.True(r.ServersJsonUpdated);
        var node = JsonNode.Parse(File.ReadAllText(servers))!;
        Assert.Equal("0.2", node["server"]!["requiredBuild"]!.GetValue<string>());
        Assert.Equal(5, node["sequence"]!.GetValue<long>());
        var verifier = new SignatureVerifier(new[] { _key.PublicInfo() });
        Assert.Equal(TrustLevel.VerifiedPublisher, verifier.VerifyFile(servers, out _, out _));

        var channel = JsonNode.Parse(File.ReadAllText(Path.Combine(_repo, "packs", "p", "channel.json")))!;
        Assert.Equal("1.0.1", channel["channels"]!["stable"]!["version"]!.GetValue<string>());
        Assert.True(Directory.Exists(Path.Combine(_repo, "packs", "p", "1.0.0")), "old version folder stays");

        var again = WriteExport(v2, suffix: "again");
        Assert.Throws<LauncherException>(() => new PackStager().Stage(new PackStager.Request(_repo, again.VersionDir, again.ChannelFile, v2, null, null, _key, null, Now)));
    }

    [Fact]
    public void Rebuilt_custom_package_with_the_same_file_name_is_refused()
    {
        var v1 = Lock("1.0.0", ("A", "1.0", "aaa"), ("VMPCustom-Client", "0.1", "ccc"));
        var e1 = WriteExport(v1);
        new PackStager().Stage(new PackStager.Request(_repo, e1.VersionDir, e1.ChannelFile, v1, null, "unchanged", _key, null, Now));

        // Same client version (so the same download name) but different bytes: the old asset would stay online.
        var v2 = Lock("1.0.1", ("A", "1.0", "aaa"), ("VMPCustom-Client", "0.1", "different"));
        var e2 = WriteExport(v2);
        var ex = Assert.Throws<LauncherException>(() => new PackStager().Stage(new PackStager.Request(_repo, e2.VersionDir, e2.ChannelFile, v2, null, "unchanged", _key, null, Now)));
        Assert.Contains("without a new version", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_repo, "packs", "p", "1.0.1")), "nothing staged");
    }

    [Fact]
    public void A_published_version_folder_is_never_overwritten()
    {
        var v1 = Lock("1.0.0", ("A", "1.0", "aaa"), ("VMPCustom-Client", "0.1", "ccc"));
        var e1 = WriteExport(v1);
        new PackStager().Stage(new PackStager.Request(_repo, e1.VersionDir, e1.ChannelFile, v1, null, "unchanged", _key, null, Now));
        var v2 = Lock("1.0.1", ("A", "1.1", "aa2"), ("VMPCustom-Client", "0.1", "ccc"));
        var e2 = WriteExport(v2);
        new PackStager().Stage(new PackStager.Request(_repo, e2.VersionDir, e2.ChannelFile, v2, null, "unchanged", _key, null, Now));

        // Re-publishing the older 1.0.0 (e.g. --version 1.0.0) must not rewrite what friends may have installed.
        var e3 = WriteExport(v1, suffix: "old");
        Assert.Throws<LauncherException>(() => new PackStager().Stage(new PackStager.Request(_repo, e3.VersionDir, e3.ChannelFile, v1, null, "unchanged", _key, null, Now)));
    }

    [Fact]
    public void Rewritten_servers_json_uses_lf_line_endings()
    {
        var v1 = Lock("1.0.0", ("A", "1.0", "aaa"), ("VMPCustom-Client", "0.1", "ccc"));
        var e1 = WriteExport(v1);
        new PackStager().Stage(new PackStager.Request(_repo, e1.VersionDir, e1.ChannelFile, v1, null, "unchanged", _key, null, Now));
        var servers = Path.Combine(_repo, "packs", "p", "servers.json");
        File.WriteAllText(servers, "{\"sequence\":1,\"server\":{\"requiredBuild\":\"0.0\"}}\n");
        var v2 = Lock("1.0.1", ("A", "1.0", "aaa"), ("VMPCustom-Client", "0.2", "ccd"));
        var e2 = WriteExport(v2);
        new PackStager().Stage(new PackStager.Request(_repo, e2.VersionDir, e2.ChannelFile, v2, null, "unchanged", _key, null, Now));
        var text = File.ReadAllText(servers);
        Assert.DoesNotContain("\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\"requiredBuild\": \"0.2\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NextVersion_counts_per_day()
    {
        var packDir = Path.Combine(_root, "pd");
        Directory.CreateDirectory(Path.Combine(packDir, "2026.10.22.1"));
        Directory.CreateDirectory(Path.Combine(packDir, "2026.10.22.3"));
        Directory.CreateDirectory(Path.Combine(packDir, "share"));
        Assert.Equal("2026.10.22.4", Publisher.NextVersion(packDir, new DateTimeOffset(2026, 10, 22, 23, 0, 0, TimeSpan.FromHours(-4))));
        Assert.Equal("2026.10.23.1", Publisher.NextVersion(packDir, new DateTimeOffset(2026, 10, 23, 0, 5, 0, TimeSpan.FromHours(-4))));
        Assert.Equal("2026.01.01.1", Publisher.NextVersion(Path.Combine(_root, "missing"), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Server_script_checks_hash_and_backs_up_before_stopping()
    {
        var script = Publisher.ServerUpdateScript(@"C:\Users\Some One\Desktop\VMP Server", "ABCDEF");
        Assert.Contains("-ne 'ABCDEF'", script, StringComparison.Ordinal);
        Assert.Contains(@"$root='C:\Users\Some One\Desktop\VMP Server'", script, StringComparison.Ordinal);
        var backup = script.IndexOf("-Action Backup", StringComparison.Ordinal);
        var stop = script.IndexOf("-Action Stop", StringComparison.Ordinal);
        var start = script.IndexOf("-Action Start", StringComparison.Ordinal);
        Assert.True(backup >= 0 && backup < stop && stop < start, "backup, then stop, then start");
        Assert.Contains("finally", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("try {", StringComparison.Ordinal) < backup, "backup and stop run inside the try whose finally restarts the server");
        Assert.Contains("-Action Stop -ServerRoot $root", script, StringComparison.Ordinal);
        Assert.Contains("Server.exe is not running after start", script, StringComparison.Ordinal);
    }

    private static PackLock Lock(string version, params (string Id, string Version, string Sha)[] modules)
    {
        var meta = JsonDocument.Parse("{}").RootElement;
        return new PackLock
        {
            Pack = new LockPackInfo { Id = "p", Name = "Pack P", Version = version },
            Generated = new LockGenerationInfo { Utc = Now, Tool = "test", Ckan = "v1.36.4" },
            Game = new LockGameInfo { KspVersion = "1.12.5", BuildId64 = "03190" },
            Ckan = new LockCkanSection
            {
                Modules = modules.Select(m => new LockedModule
                {
                    Identifier = m.Id,
                    Version = m.Version,
                    Origin = m.Id.StartsWith("VMPCustom", StringComparison.Ordinal) ? ModuleOrigin.Custom : ModuleOrigin.CkanMeta,
                    MetadataSha256 = Sha256Util.HashUtf8(m.Id + m.Version),
                    Metadata = meta,
                    Artifact = new ArtifactRef { Sha256 = Sha256Util.HashUtf8(m.Sha), Size = 3, FileName = $"{m.Id}-{m.Version}.zip", Urls = new[] { $"https://example.test/{m.Id}-{m.Version}.zip" } },
                }).ToList(),
            },
            Inventory = new InventoryRef { Sha256 = Sha256Util.HashUtf8("inv" + version), FileCount = 1, TotalBytes = 3, MaxRelPathLength = 10 },
            Multiplayer = new LockMultiplayer { RequiredClientVersion = modules.First(m => m.Id == "VMPCustom-Client").Version },
        };
    }

    private (string VersionDir, string ChannelFile) WriteExport(PackLock lk, string? policyBytes = null, string suffix = "")
    {
        var packDir = Path.Combine(_exportRoot + suffix, lk.Pack.Id);
        var versionDir = Path.Combine(packDir, lk.Pack.Version);
        Directory.CreateDirectory(versionDir);
        var lockPath = Path.Combine(versionDir, "p.vmpack.lock.json");
        LauncherJson.WriteFile(lockPath, lk);
        _key.SignFile(lockPath);
        File.WriteAllText(Path.Combine(versionDir, "inventory.json.gz"), "inv" + lk.Pack.Version);
        File.WriteAllText(Path.Combine(versionDir, "KSPModControl.xml"), policyBytes ?? "<ModControlStructure>v1</ModControlStructure>");
        File.WriteAllText(Path.Combine(versionDir, "client-settings-template.xml"), "<SettingsStructure />");
        File.WriteAllText(Path.Combine(versionDir, "capture-report.md"), "# report");
        File.WriteAllText(Path.Combine(versionDir, "p.vmpack.json"), $$"""{"id":"p","name":"Pack P","version":"{{lk.Pack.Version}}"}""");
        var channel = Path.Combine(packDir, "channel.json");
        LauncherJson.WriteFile(channel, new { schema = "vmpack-channel/1", packId = "p", channels = new { stable = new { version = lk.Pack.Version, @lock = $"{lk.Pack.Version}/p.vmpack.lock.json", sha256 = Sha256Util.HashFile(lockPath) } } });
        _key.SignFile(channel);
        return (versionDir, channel);
    }
}
