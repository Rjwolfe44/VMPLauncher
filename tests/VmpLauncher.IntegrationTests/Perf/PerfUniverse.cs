using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using CKAN;
using VmpLauncher.Core.Hashing;
using VmpLauncher.IntegrationTests.Fixtures;

namespace VmpLauncher.IntegrationTests.Perf;

/// <summary>
/// A pack shaped like a real modded KSP install: a few hundred modules, tens of thousands of small files in nested
/// GameData folders (cfg, dll, png) plus some 50-300 MB textures. Archives are stored (not compressed) and written
/// once into <c>root\archives</c>; later runs reuse them. Version 1.1 of a few modules changes a handful of small
/// files, like a typical pack update.
/// </summary>
public sealed class PerfUniverse
{
    /// <param name="PeDir">Folder of real DLLs: when set, the pack's DLLs are those (each made unique by an appended
    /// overlay), so an antivirus scanner treats them as the executables they are.</param>
    public sealed record Spec(int Modules, int Files, long BigBytes, int BigFiles, int ChangedModules, string? PeDir = null);

    private readonly Spec _spec;

    public PerfUniverse(string root, Spec spec)
    {
        _spec = spec;
        ArchiveDir = Path.Combine(root, "archives");
        Directory.CreateDirectory(ArchiveDir);
        Changed = Enumerable.Range(0, spec.ChangedModules).Select(i => ModuleId(i * spec.Modules / spec.ChangedModules + 3)).ToHashSet(StringComparer.Ordinal);
        var marker = Path.Combine(ArchiveDir, "complete.txt");
        var specText = spec + " shape 2";
        if (File.Exists(marker) && File.ReadAllText(marker) == specText)
        {
            return;
        }
        foreach (var f in Directory.EnumerateFiles(ArchiveDir))
        {
            File.Delete(f);
        }
        Generate();
        File.WriteAllText(marker, specText);
    }

    public string ArchiveDir { get; }
    public IReadOnlySet<string> Changed { get; }

    public static string ModuleId(int i) => $"PerfMod{i:000}";

    public IEnumerable<string> ArchiveFiles => Directory.EnumerateFiles(ArchiveDir, "*.zip");

    /// <summary>CKAN modules for one pack version (download URLs point at <paramref name="baseUrl"/>).</summary>
    public IReadOnlyList<FixtureRepo.Module> Modules(string baseUrl, bool v2)
    {
        var list = new List<FixtureRepo.Module>();
        for (var i = 0; i < _spec.Modules; i++)
        {
            var id = ModuleId(i);
            var version = v2 && Changed.Contains(id) ? "1.1" : "1.0";
            var zip = Path.Combine(ArchiveDir, $"{id}-{version}.zip");
            var bytes = new FileInfo(zip).Length;
            var sha256 = File.ReadAllText(zip + ".sha256").Trim();
            var sha1 = File.ReadAllText(zip + ".sha1").Trim();
            var installSize = long.Parse(File.ReadAllText(zip + ".size").Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var json = $$"""
                {
                  "spec_version": "v1.34",
                  "identifier": "{{id}}",
                  "name": "Perf {{id}}",
                  "abstract": "Synthetic module {{id}} {{version}}",
                  "author": "perf",
                  "license": "MIT",
                  "version": "{{version}}",
                  "ksp_version": "1.12",
                  "download": "{{baseUrl}}{{Path.GetFileName(zip)}}",
                  "download_size": {{bytes}},
                  "download_hash": { "sha1": "{{sha1}}", "sha256": "{{sha256.ToUpperInvariant()}}" },
                  "download_content_type": "application/zip",
                  "install_size": {{installSize}},
                  "release_date": "2026-01-01T00:00:00Z",
                  "install": [ { "find": "{{id}}", "install_to": "GameData" } ]
                }
                """;
            list.Add(new FixtureRepo.Module(id, version, CkanModule.FromJson(json), zip, sha256));
        }
        return list;
    }

    private static ulong StableHash(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h = (h ^ c) * 1099511628211UL;
        }
        return h;
    }

    private void Generate()
    {
        _pe = _spec.PeDir is null ? Array.Empty<byte[]>() : Directory.EnumerateFiles(_spec.PeDir, "*.dll")
            .Where(f => new FileInfo(f).Length is > 30_000 and < 3_000_000).OrderBy(f => f, StringComparer.Ordinal).Take(200).Select(File.ReadAllBytes).ToArray();
        var noise = new byte[4 << 20];
        new Random(1234).NextBytes(noise);
        var filesPerModule = Math.Max(1, _spec.Files / _spec.Modules);
        var bigEach = _spec.BigFiles == 0 ? 0 : _spec.BigBytes / _spec.BigFiles;
        // Big textures go to modules spread over the range.
        var bigOwners = Enumerable.Range(0, _spec.BigFiles).Select(i => ModuleId(i * _spec.Modules / Math.Max(1, _spec.BigFiles) + 1)).ToList();
        Parallel.For(0, _spec.Modules, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var id = ModuleId(i);
            var local = new Random(i);
            var entries = new List<(string Path, long Size)>();
            for (var j = 0; j < filesPerModule; j++)
            {
                var kind = j % 10;
                var (folder, ext, size) = kind switch
                {
                    0 => ("Plugins", ".dll", local.Next(20_000, 300_000)),
                    < 5 => ("Parts", ".cfg", local.Next(400, 8_000)),
                    < 8 => ("Textures", ".png", local.Next(10_000, 150_000)),
                    _ => ("Patches", ".cfg", local.Next(300, 3_000)),
                };
                if (folder == "Plugins" && j > 0)
                {
                    (folder, ext) = ("Parts", ".cfg"); // one DLL per module, about the real ratio
                }
                entries.Add(($"GameData/{id}/{folder}/Set{j / 40}/{folder[..4]}{j:0000}{ext}", size));
            }
            foreach (var (owner, k) in bigOwners.Select((o, k) => (o, k)).Where(x => x.o == id))
            {
                entries.Add(($"GameData/{id}/Textures/Big/planet{k:00}.dds", bigEach));
            }
            WriteZip(id, "1.0", entries, noise, changeSalt: 0);
            if (Changed.Contains(id))
            {
                WriteZip(id, "1.1", entries, noise, changeSalt: 1);
            }
        });
    }

    /// <summary>
    /// Each file is a unique 64-byte header (path, version salt) followed by a slice of shared noise, so contents differ
    /// per file. Version 1.1 changes the content of the first DLL and two cfgs only; every other file is identical.
    /// </summary>
    private byte[][] _pe = Array.Empty<byte[]>();

    private void WriteZip(string id, string version, List<(string Path, long Size)> entries, byte[] noise, int changeSalt)
    {
        var zipPath = Path.Combine(ArchiveDir, $"{id}-{version}.zip");
        long installSize = 0;
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var changedSoFar = 0;
            foreach (var (path, size) in entries)
            {
                var salt = 0;
                if (changeSalt != 0 && changedSoFar < 3 && (path.EndsWith(".dll", StringComparison.Ordinal) || changedSoFar > 0) && !path.Contains("/Big/", StringComparison.Ordinal))
                {
                    salt = changeSalt;
                    changedSoFar++;
                }
                var e = zip.CreateEntry(path, CompressionLevel.NoCompression);
                e.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                var header = new byte[64];
                System.Text.Encoding.UTF8.GetBytes($"{path}|{salt}".PadRight(64).AsSpan(0, 64), header);
                var hash = StableHash(path);
                if (_pe.Length > 0 && path.EndsWith(".dll", StringComparison.Ordinal))
                {
                    var pe = _pe[(int)(hash % (ulong)_pe.Length)];
                    s.Write(pe);
                    s.Write(header); // overlay after the image: still a valid DLL, unique per file
                    installSize += pe.Length + header.Length;
                    continue;
                }
                s.Write(header);
                var remaining = size - header.Length;
                var offset = (int)(hash % (ulong)(noise.Length / 2));
                while (remaining > 0)
                {
                    var n = (int)Math.Min(remaining, noise.Length - offset);
                    s.Write(noise, offset, n);
                    remaining -= n;
                    offset = 0;
                }
                installSize += Math.Max(size, header.Length);
            }
        }
        File.WriteAllText(zipPath + ".sha256", Sha256Util.HashFile(zipPath));
        using (var read = File.OpenRead(zipPath))
        {
            File.WriteAllText(zipPath + ".sha1", Convert.ToHexString(SHA1.HashData(read)));
        }
        File.WriteAllText(zipPath + ".size", installSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
