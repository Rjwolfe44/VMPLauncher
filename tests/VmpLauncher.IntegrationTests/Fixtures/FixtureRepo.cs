using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Encoding = System.Text.Encoding;
using CKAN;
using CKAN.Games.KerbalSpaceProgram;
using VmpLauncher.Ckan.Locking;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.IntegrationTests.Fixtures;

/// <summary>
/// Tiny CKAN universe with real zip archives served by <see cref="LocalArtifactServer"/>:
///  A 1.0 / A 1.1 (GameData/A/A.dll, GameData/A/PluginData/s.cfg)
///  B 1.0 depends on A            C 1.0 conflicts with B
///  D 1.0 provides Lib            E 1.0 depends on Lib
///  F 1.0 (standalone)            Clash 1.0 ships GameData/A/A.dll too
///  Epoch 1:2.0                   Root 1.0 installs GameRoot/rootfile.txt
/// </summary>
public sealed class FixtureRepo
{
    public sealed record Module(string Id, string Version, CkanModule Ckan, string ZipPath, string Sha256);

    private readonly Dictionary<string, Module> _modules = new(StringComparer.Ordinal);

    public FixtureRepo(string archiveDir, string baseUrl)
    {
        Directory.CreateDirectory(archiveDir);
        ArchiveDir = archiveDir;
        BaseUrl = baseUrl;

        Add("A", "1.0", new() { ["GameData/A/A.dll"] = "A-DLL-1.0", ["GameData/A/A.cfg"] = "A = 1.0", ["GameData/A/PluginData/s.cfg"] = "state=1" });
        Add("A", "1.1", new() { ["GameData/A/A.dll"] = "A-DLL-1.1-newer", ["GameData/A/A.cfg"] = "A = 1.1", ["GameData/A/PluginData/s.cfg"] = "state=1" });
        Add("B", "1.0", new() { ["GameData/B/B.dll"] = "B-DLL", ["GameData/B/B.cfg"] = "B = 1" }, depends: new[] { "A" });
        Add("C", "1.0", new() { ["GameData/C/C.cfg"] = "C" }, conflicts: new[] { "B" });
        Add("D", "1.0", new() { ["GameData/D/Lib.dll"] = "LIB" }, provides: new[] { "Lib" });
        Add("E", "1.0", new() { ["GameData/E/E.dll"] = "E-DLL", ["GameData/E/E.cfg"] = "E" }, depends: new[] { "Lib" });
        Add("F", "1.0", new() { ["GameData/F/F.cfg"] = "F" });
        Add("Clash", "1.0", new() { ["GameData/A/A.dll"] = "CLASH" });
        Add("Epoch", "1:2.0", new() { ["GameData/Epoch/e.cfg"] = "epoch" });
        Add("Root", "1.0", new() { ["Root/rootfile.txt"] = "root" }, install: """[ { "file": "Root/rootfile.txt", "install_to": "GameRoot" } ]""");
        Add("Dirs", "1.0", new() { ["GameData/Dirs/d.cfg"] = "dirs" }, emptyDirs: new[] { "GameData/Dirs/Empty/" });
    }

    public string ArchiveDir { get; }
    public string BaseUrl { get; }

    public Module this[string id, string version] => _modules[$"{id}={version}"];
    public Module Get(string id, string version = "1.0") => this[id, version];
    public IEnumerable<Module> All => _modules.Values;

    private void Add(string id, string version, Dictionary<string, string> files, string[]? depends = null, string[]? conflicts = null, string[]? provides = null, string? install = null, string[]? emptyDirs = null)
    {
        var fileName = $"{id}-{version.Replace(':', '_')}.zip";
        var zipPath = Path.Combine(ArchiveDir, fileName);
        using (var fs = new FileStream(zipPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var dir in emptyDirs ?? Array.Empty<string>())
            {
                zip.CreateEntry(dir).LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            }
            foreach (var (path, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var e = zip.CreateEntry(path, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes(content + "\n" + new string('x', 2048));
                s.Write(bytes, 0, bytes.Length);
            }
        }
        var bytesAll = File.ReadAllBytes(zipPath);
        var sha256 = Sha256Util.HashBytes(bytesAll);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytesAll));
        var installSize = files.Values.Sum(c => c.Length + 1 + 2048);
        var installJson = install ?? $$"""[ { "find": "{{id}}", "install_to": "GameData" } ]""";
        var json = $$"""
            {
              "spec_version": "v1.34",
              "identifier": "{{id}}",
              "name": "Fixture {{id}}",
              "abstract": "Fixture module {{id}} {{version}}",
              "author": "fixture",
              "license": "MIT",
              "version": "{{version}}",
              "ksp_version": "1.12",
              "download": "{{BaseUrl}}{{fileName}}",
              "download_size": {{bytesAll.Length}},
              "download_hash": { "sha1": "{{sha1}}", "sha256": "{{sha256.ToUpperInvariant()}}" },
              "download_content_type": "application/zip",
              "install_size": {{installSize}},
              "release_date": "2026-01-01T00:00:00Z",
              "install": {{installJson}}
              {{Rel("depends", depends)}}{{Rel("conflicts", conflicts)}}{{(provides is null ? "" : $", \"provides\": [{string.Join(", ", provides.Select(p => $"\"{p}\""))}]")}}
            }
            """;
        if (id == "Clash")
        {
            json = json.Replace("\"find\": \"Clash\"", "\"find\": \"A\"", StringComparison.Ordinal);
        }
        var module = CkanModule.FromJson(json);
        _modules[$"{id}={version}"] = new Module(id, version, module, zipPath, sha256);
    }

    private static string Rel(string kind, string[]? names)
        => names is null ? string.Empty : $", \"{kind}\": [{string.Join(", ", names.Select(n => $"{{ \"name\": \"{n}\" }}"))}]";

    /// <summary>Writes a lock + inventory for the given modules into <paramref name="outDir"/> and returns the lock path.</summary>
    /// <param name="autoInstalled">Identifiers locked as auto-installed (CKAN may remove them when nothing depends on them).</param>
    /// <param name="privateIds">Identifiers whose archive is a private sideload (no download URL in the lock).</param>
    public string WriteLock(string outDir, string packVersion, IEnumerable<Module> modules, IEnumerable<LockOverride>? overrides = null, FileRules? rules = null, IEnumerable<InventoryEntry>? extraInventory = null, IEnumerable<string>? rootInstallOwners = null,
        IEnumerable<string>? autoInstalled = null, IEnumerable<string>? privateIds = null)
    {
        Directory.CreateDirectory(outDir);
        rules ??= FileRules.Default;
        var ruleSet = new FileRuleSet(rules);
        var game = new KerbalSpaceProgram();
        var indexer = new ArchiveContentIndexer(Path.Combine(outDir, "capture-cache.json"), game);
        var auto = new HashSet<string>(autoInstalled ?? Array.Empty<string>(), StringComparer.Ordinal);
        var privates = new HashSet<string>(privateIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var locked = new List<LockedModule>();
        var files = new List<InventoryEntry>();
        foreach (var m in modules.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var lm = RegistryLockBuilder.FromModule(m.Ckan, autoInstalled: auto.Contains(m.Id), ModuleOrigin.CkanMeta);
            if (privates.Contains(m.Id))
            {
                lm = lm with { Artifact = lm.Artifact! with { Urls = Array.Empty<string>(), Distribution = "private-sideload", FileName = Path.GetFileName(m.ZipPath) } };
            }
            locked.Add(lm);
            foreach (var ef in indexer.Index(m.Ckan, m.ZipPath, lm.MetadataSha256))
            {
                files.Add(new InventoryEntry { Path = ef.Path, Size = ef.Size, Sha256 = ef.Sha256, Owner = m.Id, Class = ruleSet.MutableOwned.IsMatch(ef.Path) ? FileClass.MutableOwned : FileClass.Owned });
            }
        }
        if (extraInventory is not null)
        {
            files.AddRange(extraInventory);
        }
        var ov = (overrides ?? Array.Empty<LockOverride>()).ToList();
        // Replace overrides change the expected hash of the owned file; disable overrides make the original path absent.
        foreach (var o in ov)
        {
            if (o.Op == "replace")
            {
                var idx = files.FindIndex(f => string.Equals(f.Path, o.Path, StringComparison.OrdinalIgnoreCase));
                files[idx] = files[idx] with { Sha256 = o.Sha256!, Size = o.Size!.Value, Class = FileClass.Override };
            }
            else if (o.Op == "add")
            {
                files.Add(new InventoryEntry { Path = o.Path, Size = o.Size!.Value, Sha256 = o.Sha256!, Owner = o.Id, Class = FileClass.Override });
            }
            else if (o.Op == "disable")
            {
                var idx = files.FindIndex(f => string.Equals(f.Path, o.Path, StringComparison.OrdinalIgnoreCase));
                var orig = files[idx];
                files.RemoveAt(idx);
                var dir = o.Path.Contains('/') ? o.Path[..(o.Path.LastIndexOf('/') + 1)] : string.Empty;
                files.Add(new InventoryEntry { Path = dir + o.RenameTo!, Size = orig.Size, Sha256 = orig.Sha256, Owner = o.Id, Class = FileClass.Override });
            }
        }
        var invPath = Path.Combine(outDir, "inventory.json.gz");
        var inv = InventoryFile.Write(invPath, new InventoryDocument { PackId = "fixture", PackVersion = packVersion, Files = files });
        var lockDoc = new PackLock
        {
            Pack = new LockPackInfo { Id = "fixture", Name = "Fixture Pack", Version = packVersion, Author = "tests" },
            Generated = new LockGenerationInfo { Utc = DateTimeOffset.UtcNow, Tool = "tests", Ckan = Meta.GetVersion() },
            Game = new LockGameInfo { KspVersion = "1.12.5", BuildId64 = "03190", RequiredDlc = new GameRequirements().RequiredDlc, CompatibleGameVersions = new[] { "1.12" } },
            Ckan = new LockCkanSection { Modules = locked, RootInstallOwners = (rootInstallOwners ?? Array.Empty<string>()).ToList() },
            Overrides = ov,
            Inventory = new InventoryRef { Sha256 = inv.Sha256, FileCount = inv.FileCount, TotalBytes = inv.TotalBytes, MaxRelPathLength = inv.MaxRelPathLength },
            FileRules = rules,
        };
        var lockPath = Path.Combine(outDir, "fixture.vmpack.lock.json");
        LauncherJson.WriteFile(lockPath, lockDoc);
        return lockPath;
    }
}
