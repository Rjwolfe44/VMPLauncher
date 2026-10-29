using System.IO;
using System.Text;
using System.Text.Json;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class CacheReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-cache-" + Guid.NewGuid().ToString("N"));
    private readonly string _ckan;
    private readonly string _ours;

    public CacheReuseTests()
    {
        _ckan = Path.Combine(_root, "ckan-downloads");
        _ours = Path.Combine(_root, "launcher-cache");
        Directory.CreateDirectory(_ckan);
        Directory.CreateDirectory(_ours);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Prefix(string url) => CKAN.NetFileCache.CreateURLHash(new Uri(url));

    private static LockedModule Module(string id, string url, byte[] bytes, string distribution = "download") => new()
    {
        Identifier = id,
        Version = "1.0",
        MetadataSha256 = Sha256Util.HashUtf8(id),
        Metadata = JsonDocument.Parse($$"""{"identifier":"{{id}}","download":"{{url}}"}""").RootElement,
        Artifact = new ArtifactRef { Sha256 = Sha256Util.HashBytes(bytes), Size = bytes.Length, Urls = distribution == "download" ? new[] { url } : Array.Empty<string>(), Distribution = distribution, FileName = id + ".zip" },
    };

    private static PackLock Lock(params LockedModule[] modules) => new()
    {
        Pack = new LockPackInfo { Id = "p", Name = "P", Version = "1" },
        Generated = new LockGenerationInfo { Utc = DateTimeOffset.UtcNow, Tool = "t", Ckan = "v1.36.4" },
        Game = new LockGameInfo { KspVersion = "1.12.5", BuildId64 = "03190" },
        Ckan = new LockCkanSection { Modules = modules },
        Inventory = new InventoryRef { Sha256 = "00", FileCount = 0, TotalBytes = 0, MaxRelPathLength = 1 },
    };

    [Fact]
    public void Reuses_matching_archives_from_a_ckan_cache_and_rejects_wrong_bytes()
    {
        var good = Encoding.UTF8.GetBytes("archive A bytes");
        var bad = Encoding.UTF8.GetBytes("archive B bytes");
        const string urlA = "https://example.test/a/ModA-1.0.zip";
        const string urlB = "https://example.test/b/ModB-1.0.zip";
        File.WriteAllBytes(Path.Combine(_ckan, $"{Prefix(urlA)}-ModA-1.0.zip"), good);
        File.WriteAllBytes(Path.Combine(_ckan, $"{Prefix(urlB)}-ModB-1.0.zip"), Encoding.UTF8.GetBytes("tampered B bytes")[..bad.Length]);
        var lk = Lock(Module("ModA", urlA, good), Module("ModB", urlB, bad));

        var r = CkanCacheImporter.Import(lk, _ours, new[] { _ckan }, null, CancellationToken.None);

        Assert.Equal(1, r.Imported);
        Assert.Equal(1, r.Rejected);
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(_ours, $"{Prefix(urlA)}-ModA-1.0.zip")));
        Assert.False(File.Exists(Path.Combine(_ours, $"{Prefix(urlB)}-ModB-1.0.zip")));
        Assert.Empty(Directory.EnumerateFiles(_ours, "*.import"));
        Assert.True(File.Exists(Path.Combine(_ckan, $"{Prefix(urlA)}-ModA-1.0.zip")), "the other cache keeps its copy");

        // A second run finds everything already in place.
        Assert.Equal(0, CkanCacheImporter.Import(lk, _ours, new[] { _ckan }, null, CancellationToken.None).Imported);
    }

    [Fact]
    public void Clearing_a_ckan_cache_after_transfer_keeps_the_launchers_copies_and_counts_only_real_savings()
    {
        var needed = Encoding.UTF8.GetBytes("needed archive");
        var other = Encoding.UTF8.GetBytes("only CKAN used this one");
        const string url = "https://example.test/Needed.zip";
        File.WriteAllBytes(Path.Combine(_ckan, $"{Prefix(url)}-Needed.zip"), needed);
        File.WriteAllBytes(Path.Combine(_ckan, "ABCDEF01-Other.zip"), other);
        File.WriteAllText(Path.Combine(_ckan, "not-a-cache-entry.txt"), "keep me");
        CkanCacheImporter.Import(Lock(Module("Needed", url, needed)), _ours, new[] { _ckan }, null, CancellationToken.None);

        var info = CkanCacheImporter.Describe(_ckan);
        Assert.Equal(2, info.Files);
        Assert.Equal(1, info.SharedFiles);
        Assert.Equal(other.Length, info.FreeableBytes);

        var (files, freed) = CkanCacheImporter.Clear(_ckan, CancellationToken.None);
        Assert.Equal(2, files);
        Assert.Equal(other.Length, freed);
        Assert.Equal(needed, File.ReadAllBytes(Path.Combine(_ours, $"{Prefix(url)}-Needed.zip")));
        Assert.True(File.Exists(Path.Combine(_ckan, "not-a-cache-entry.txt")), "only CKAN cache entries are deleted");
    }

    [Fact]
    public void Cleanup_keeps_what_current_and_installed_locks_need_including_private_placeholders()
    {
        var a = Encoding.UTF8.GetBytes("A");
        var old = Encoding.UTF8.GetBytes("old");
        var apex = Encoding.UTF8.GetBytes("apex");
        const string urlA = "https://example.test/ModA.zip";
        const string urlOld = "https://example.test/ModOld.zip";
        const string urlApex = "https://sideload.invalid/VMPCustom-ApexGuidance/Apex.zip";
        File.WriteAllBytes(Path.Combine(_ours, $"{Prefix(urlA)}-ModA.zip"), a);
        File.WriteAllBytes(Path.Combine(_ours, $"{Prefix(urlOld)}-ModOld.zip"), old);
        File.WriteAllBytes(Path.Combine(_ours, $"{Prefix(urlApex)}-Apex.zip"), apex);
        File.WriteAllText(Path.Combine(_ours, "vmpl-verified.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_ours, "private"));
        var apexModule = Module("VMPCustom-ApexGuidance", urlApex, apex, "private-sideload");
        File.WriteAllBytes(Path.Combine(_ours, "private", apexModule.Artifact!.Sha256 + ".zip"), apex);
        File.WriteAllBytes(Path.Combine(_ours, "private", new string('0', 64) + ".zip"), old);

        var current = Lock(Module("ModA", urlA, a), apexModule);
        var plan = DownloadCacheCleaner.Analyze(_ours, new[] { current });

        Assert.Equal(2, plan.Remove.Count);
        Assert.Contains(plan.Remove, p => p.EndsWith("ModOld.zip", StringComparison.Ordinal));
        Assert.Contains(plan.Remove, p => p.EndsWith(new string('0', 64) + ".zip", StringComparison.Ordinal));
        var (files, _) = DownloadCacheCleaner.Execute(plan);
        Assert.Equal(2, files);
        Assert.True(File.Exists(Path.Combine(_ours, $"{Prefix(urlA)}-ModA.zip")));
        Assert.True(File.Exists(Path.Combine(_ours, $"{Prefix(urlApex)}-Apex.zip")), "private placeholder cache entry kept");
        Assert.True(File.Exists(Path.Combine(_ours, "vmpl-verified.json")), "non-archive files are never touched");

        // With the old version still installed, its archive is kept.
        File.WriteAllBytes(Path.Combine(_ours, $"{Prefix(urlOld)}-ModOld.zip"), old);
        var installed = Lock(Module("ModOld", urlOld, old));
        Assert.Empty(DownloadCacheCleaner.Analyze(_ours, new[] { current, installed }).Remove);
    }
}
