using System.Diagnostics;
using CKAN;
using CKAN.Games.KerbalSpaceProgram;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Ckan.Locking;

/// <summary>
/// Builds a lock + inventory from a live KSP install's CKAN registry and a CKAN download cache.
/// Read-only on the host install: the registry is loaded without taking CKAN's lock file.
/// Custom packages and overrides are added by the Publishing layer (slice 8); this produces the CKAN part.
/// </summary>
public sealed class LockGenerator
{
    public sealed record Request(
        string HostKspDir,
        string CacheDir,
        PackDefinition Pack,
        string OutputDir,
        LauncherPaths Paths,
        bool VerifyHostHashes = false,
        string? PackFileSha256 = null,
        Extras? Extras = null);

    /// <summary>Non-CKAN content supplied by the publishing layer: custom packages (as CKAN-shaped modules), overrides, client version.</summary>
    public sealed record Extras(
        IReadOnlyList<LockedModule> CustomModules,
        IReadOnlyList<InventoryEntry> CustomInventory,
        IReadOnlyList<LockOverride> Overrides,
        string? RequiredClientVersion,
        string? PolicySha256);

    public sealed record Report(
        string LockPath,
        string InventoryPath,
        int Modules,
        int Files,
        long TotalBytes,
        TimeSpan Elapsed,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> ReproductionDiffs);

    private readonly TextWriter _log;

    public LockGenerator(TextWriter log)
    {
        _log = log;
    }

    public Report Generate(Request r, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var user = new VmpCkanUser();
        var game = new KerbalSpaceProgram();
        var repoData = new RepositoryDataManager(r.Paths.CkanReposDir);
        var inst = new GameInstance(game, r.HostKspDir, "host", user);
        var registry = RegistryManager.ReadOnlyRegistry(inst, repoData)
                       ?? throw new InvalidOperationException($"Could not read {Path.Combine(inst.CkanDir, "registry.json")}");
        var registryJsonPath = Path.Combine(inst.CkanDir, "registry.json");
        var registrySha = Sha256Util.HashFile(registryJsonPath, ct);

        var installed = registry.InstalledModules.ToList();
        var selected = SelectModules(installed, r.Pack.Ckan, warnings);
        _log.WriteLine($"Selected {selected.Count} modules from registry ({installed.Count} installed).");

        var lockedModules = new List<LockedModule>();
        var dlcs = new List<LockedDlc>();
        foreach (var im in selected.OrderBy(m => m.identifier, StringComparer.Ordinal))
        {
            if (im.Module.IsDLC)
            {
                dlcs.Add(new LockedDlc(im.identifier, im.Module.version.ToString()));
                continue;
            }
            lockedModules.Add(RegistryLockBuilder.FromInstalled(im));
        }

        // Inventory from archives
        var rules = new FileRuleSet(r.Pack.FileRules);
        var indexer = new ArchiveContentIndexer(Path.Combine(r.Paths.DataRoot, "capture-cache.json"), game);
        var files = new List<InventoryEntry>();
        using (var cache = new NetModuleCache(r.CacheDir))
        {
            var done = 0;
            foreach (var lm in lockedModules.Where(m => m.Kind == ModuleKindDto.Package))
            {
                ct.ThrowIfCancellationRequested();
                var module = selected.First(m => m.identifier == lm.Identifier).Module;
                var zip = cache.GetCachedFilename(module);
                if (zip is null)
                {
                    throw new FileNotFoundException($"Archive for {lm.Identifier} {lm.Version} is not in the cache {r.CacheDir}. Download it with CKAN first or point --cache at a cache that has it.");
                }
                var actualSha = Sha256Util.HashFile(zip, ct);
                if (!Sha256Util.HexEquals(actualSha, lm.Artifact!.Sha256))
                {
                    throw new InvalidDataException($"Cached archive for {lm.Identifier} has sha256 {actualSha}, metadata says {lm.Artifact.Sha256}.");
                }
                foreach (var ef in indexer.Index(module, zip, lm.MetadataSha256, ct))
                {
                    files.Add(new InventoryEntry
                    {
                        Path = ef.Path,
                        Size = ef.Size,
                        Sha256 = ef.Sha256,
                        Owner = lm.Identifier,
                        Class = rules.MutableOwned.IsMatch(ef.Path) ? FileClass.MutableOwned : FileClass.Owned,
                    });
                }
                done++;
                if (done % 20 == 0)
                {
                    _log.WriteLine($"  indexed {done}/{lockedModules.Count} archives ({indexer.CacheHits} cached, {indexer.CacheMisses} hashed)");
                }
            }
        }
        indexer.Save();
        _log.WriteLine($"Indexed {files.Count} files from {lockedModules.Count} archives ({indexer.CacheHits} cache hits, {indexer.CacheMisses} hashed).");

        var rootInstallOwners = lockedModules
            .Where(lm => lm.Kind == ModuleKindDto.Package)
            .Select(lm => selected.First(m => m.identifier == lm.Identifier).Module)
            .Where(m => (m.install ?? Array.Empty<ModuleInstallDescriptor>()).Any(st => string.Equals(st.install_to, "GameRoot", StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.identifier)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (rootInstallOwners.Count > 0)
        {
            warnings.Add($"Modules installing into the game root: {string.Join(", ", rootInstallOwners)}");
        }
        var extras = r.Extras;
        if (extras is not null)
        {
            lockedModules.AddRange(extras.CustomModules);
            files.AddRange(extras.CustomInventory);
            lockedModules = lockedModules.OrderBy(m => m.Identifier, StringComparer.Ordinal).ToList();
        }
        var overrides = extras?.Overrides ?? Array.Empty<LockOverride>();
        // Overrides reshape the expected inventory: replace -> new hash, add -> new entry, disable -> renamed entry.
        foreach (var o in overrides)
        {
            switch (o.Op)
            {
                case "replace":
                {
                    var idx = files.FindIndex(f => string.Equals(f.Path, o.Path, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) { throw new InvalidDataException($"Override {o.Id} replaces {o.Path} but no module produces it."); }
                    files[idx] = files[idx] with { Sha256 = o.Sha256!, Size = o.Size!.Value, Class = FileClass.Override };
                    break;
                }
                case "add":
                    files.Add(new InventoryEntry { Path = o.Path, Size = o.Size!.Value, Sha256 = o.Sha256!, Owner = o.Id, Class = FileClass.Override });
                    break;
                case "disable":
                {
                    var idx = files.FindIndex(f => string.Equals(f.Path, o.Path, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) { throw new InvalidDataException($"Override {o.Id} disables {o.Path} but no module produces it."); }
                    var orig = files[idx];
                    files.RemoveAt(idx);
                    var dir = o.Path.Contains('/') ? o.Path[..(o.Path.LastIndexOf('/') + 1)] : string.Empty;
                    files.Add(new InventoryEntry { Path = dir + o.RenameTo!, Size = orig.Size, Sha256 = orig.Sha256, Owner = o.Id, Class = FileClass.Override });
                    break;
                }
            }
        }
        var problems = OwnershipValidator.Validate(files, overrides, rootInstallOwners);
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Ownership validation failed:\n" + string.Join("\n", problems.Select(p => $"  [{p.Code}] {p.Message}")));
        }

        // Reproduction check against the host registry's installed_files (and optionally the host's disk)
        var diffs = ReproductionCheck(inst, registry, files.Where(f => f.Class != FileClass.Custom && f.Class != FileClass.Override).ToList(), r.VerifyHostHashes, ct);

        Directory.CreateDirectory(r.OutputDir);
        var inventoryPath = Path.Combine(r.OutputDir, "inventory.json.gz");
        var inv = InventoryFile.Write(inventoryPath, new InventoryDocument { PackId = r.Pack.Id, PackVersion = r.Pack.Version, Files = files });

        var requiredClient = r.Pack.Multiplayer.RequiredClientVersion == "auto" ? extras?.RequiredClientVersion : r.Pack.Multiplayer.RequiredClientVersion;
        var pack = r.Pack;
        var lockDoc = new PackLock
        {
            Pack = new LockPackInfo
            {
                Id = pack.Id, Name = pack.Name, Version = pack.Version, Author = pack.Author, Description = pack.Description,
                PackSha256 = r.PackFileSha256, Links = pack.Links,
            },
            Generated = new LockGenerationInfo
            {
                Utc = DateTimeOffset.UtcNow, Tool = $"VmpLauncher {typeof(LockGenerator).Assembly.GetName().Version?.ToString(3)}",
                Ckan = Meta.GetVersion(), FromRegistrySha256 = registrySha,
            },
            Game = new LockGameInfo
            {
                KspVersion = pack.Game.KspVersion, BuildId64 = pack.Game.BuildId64, RequiredDlc = pack.Game.RequiredDlc,
                CompatibleGameVersions = inst.CompatibleVersions.Select(v => v.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList(),
            },
            Ckan = new LockCkanSection
            {
                ResolverOptions = new ResolverOptions { WithRecommends = pack.Ckan.WithRecommends },
                Modules = lockedModules,
                Dlc = dlcs,
                RootInstallOwners = rootInstallOwners,
            },
            Overrides = overrides,
            Inventory = new InventoryRef { Sha256 = inv.Sha256, FileCount = inv.FileCount, TotalBytes = inv.TotalBytes, MaxRelPathLength = inv.MaxRelPathLength },
            FileRules = pack.FileRules,
            Multiplayer = new LockMultiplayer
            {
                RequiredClientVersion = requiredClient,
                Servers = pack.Multiplayer.Servers.Where(s => s.Exportable).ToList(),
                Policy = pack.Multiplayer.PolicyUrl is null ? null : new PolicyRef(pack.Multiplayer.PolicyUrl, extras?.PolicySha256),
                ServerDirectoryUrl = pack.Multiplayer.ServerDirectoryUrl,
            },
        };
        var lockPath = Path.Combine(r.OutputDir, $"{pack.Id}.vmpack.lock.json");
        LauncherJson.WriteFile(lockPath, lockDoc);
        return new Report(lockPath, inventoryPath, lockedModules.Count, inv.FileCount, inv.TotalBytes, sw.Elapsed, warnings, diffs);
    }

    private static List<InstalledModule> SelectModules(List<InstalledModule> installed, CkanSelection sel, List<string> warnings)
    {
        if (sel.Source == "registry")
        {
            return installed;
        }
        var byId = installed.ToDictionary(m => m.identifier, StringComparer.Ordinal);
        var result = new List<InstalledModule>();
        foreach (var pin in sel.Modules)
        {
            if (!byId.TryGetValue(pin.Id, out var im))
            {
                throw new InvalidDataException($"Pack pins {pin.Id} {pin.Version} but it is not installed in the host registry.");
            }
            if (im.Module.version.ToString() != pin.Version)
            {
                throw new InvalidDataException($"Pack pins {pin.Id} {pin.Version} but the host has {im.Module.version}.");
            }
            result.Add(im);
        }
        foreach (var meta in sel.Metapackages)
        {
            if (byId.TryGetValue(meta, out var im))
            {
                result.Add(im);
            }
            else
            {
                warnings.Add($"Metapackage {meta} is not installed; skipped.");
            }
        }
        return result;
    }

    private List<string> ReproductionCheck(GameInstance inst, Registry registry, List<InventoryEntry> files, bool verifyHashes, CancellationToken ct)
    {
        var diffs = new List<string>();
        var registryFiles = registry.InstalledFileInfo()
            .Where(r => !Directory.Exists(inst.ToAbsoluteGameDir(r.relPath)))
            .ToDictionary(r => r.relPath, r => r.owner.identifier, Platform.PathComparer);
        var invByPath = files.ToDictionary(f => f.Path, Platform.PathComparer);
        foreach (var (path, owner) in registryFiles)
        {
            if (!invByPath.TryGetValue(path, out var e))
            {
                diffs.Add($"registry has {path} ({owner}) but archives do not produce it");
            }
            else if (!string.Equals(e.Owner, owner, StringComparison.Ordinal))
            {
                diffs.Add($"{path}: registry owner {owner}, archive owner {e.Owner}");
            }
        }
        foreach (var e in files)
        {
            if (!registryFiles.ContainsKey(e.Path))
            {
                diffs.Add($"archives produce {e.Path} ({e.Owner}) but the registry does not list it");
            }
        }
        _log.WriteLine($"Reproduction vs registry installed_files: {diffs.Count} differences.");

        var missing = 0; var sizeDiff = 0; var hashDiff = 0; var checkedHashes = 0;
        var sw = Stopwatch.StartNew();
        foreach (var e in files)
        {
            ct.ThrowIfCancellationRequested();
            var abs = inst.ToAbsoluteGameDir(e.Path);
            var fi = new FileInfo(abs);
            if (!fi.Exists)
            {
                missing++;
                if (missing <= 10)
                {
                    diffs.Add($"host disk missing {e.Path} ({e.Owner})");
                }
                continue;
            }
            if (fi.Length != e.Size && e.Class == FileClass.Owned)
            {
                sizeDiff++;
                if (sizeDiff <= 10)
                {
                    diffs.Add($"host disk size differs {e.Path}: {fi.Length} vs archive {e.Size} ({e.Owner})");
                }
                continue;
            }
            if (verifyHashes && e.Class == FileClass.Owned)
            {
                checkedHashes++;
                if (!Sha256Util.HexEquals(Sha256Util.HashFile(abs, ct), e.Sha256))
                {
                    hashDiff++;
                    if (hashDiff <= 20)
                    {
                        diffs.Add($"host disk content differs {e.Path} ({e.Owner}) -> override candidate");
                    }
                }
            }
        }
        _log.WriteLine($"Host disk: {missing} missing, {sizeDiff} size mismatches, {hashDiff} hash mismatches of {checkedHashes} hashed ({sw.Elapsed.TotalSeconds:F1}s).");
        return diffs;
    }
}
