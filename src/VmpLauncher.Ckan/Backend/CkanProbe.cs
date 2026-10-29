using System.Diagnostics;
using System.Security.Cryptography;
using CKAN;
using CKAN.Games.KerbalSpaceProgram;
using CKAN.IO;
using CKAN.Versioning;
using VmpLauncher.Ckan.Repo;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core;

namespace VmpLauncher.Ckan.Backend;

/// <summary>
/// Slice-1 contract probe: exercises every CKAN behaviour the launcher design depends on, against the host's
/// real registry and cache, and reports pass/fail per assumption. Read-only on the host install.
/// </summary>
public sealed class CkanProbe
{
    public sealed record Options(string HostKspDir, string? LockedCkanFile, string? HostCacheDir, LauncherPaths Paths, int SampleModules = 6);

    private readonly TextWriter _out;

    public CkanProbe(TextWriter output)
    {
        _out = output;
    }

    public int Run(Options o)
    {
        var failures = 0;
        var ckanAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CKAN");
        var appDataBefore = Snapshot(ckanAppData);

        Section("1. CKAN core");
        _out.WriteLine($"   CKAN core version      : {Meta.GetVersion(VersionFormat.Full)}");
        _out.WriteLine($"   Spec version supported : {Meta.SpecVersion}");
        _out.WriteLine($"   CKAN_CONFIG_FILE       : {Environment.GetEnvironmentVariable("CKAN_CONFIG_FILE")}");

        var user = new VmpCkanUser(strict: false);
        var game = new KerbalSpaceProgram();
        var repoData = new RepositoryDataManager(o.Paths.CkanReposDir);

        Section("2. Host registry (read-only)");
        var hostInst = new GameInstance(game, o.HostKspDir, "host", user);
        _out.WriteLine($"   Game dir   : {hostInst.GameDir}");
        _out.WriteLine($"   Valid      : {hostInst.Valid}   Version: {hostInst.Version()}");
        _out.WriteLine($"   Compatible : {string.Join(", ", hostInst.CompatibleVersions)}");
        var sw = Stopwatch.StartNew();
        var registry = RegistryManager.ReadOnlyRegistry(hostInst, repoData);
        if (registry is null)
        {
            Fail("ReadOnlyRegistry returned null (registry.json unreadable)");
            return 1;
        }
        _out.WriteLine($"   Loaded registry.json in {sw.ElapsedMilliseconds} ms");
        var installed = registry.InstalledModules.ToList();
        var packages = installed.Where(m => !m.Module.IsDLC && !m.Module.IsMetapackage).ToList();
        var metas = installed.Where(m => m.Module.IsMetapackage).ToList();
        var dlcs = installed.Where(m => m.Module.IsDLC).ToList();
        _out.WriteLine($"   Installed modules : {installed.Count} ({packages.Count} packages, {metas.Count} metapackages, {dlcs.Count} DLC)");
        _out.WriteLine($"   Auto-installed    : {installed.Count(m => m.AutoInstalled)}");
        var noSha = packages.Where(p => p.Module.download_hash?.sha256 is null).Select(p => p.identifier).ToList();
        Check(noSha.Count == 0, $"every package has a sha256 ({packages.Count - noSha.Count}/{packages.Count})", noSha.Count == 0 ? null : string.Join(", ", noSha), ref failures);
        var fileRows = registry.InstalledFileInfo().ToList();
        var fileOnly = fileRows.Where(r => !Directory.Exists(hostInst.ToAbsoluteGameDir(r.relPath))).ToList();
        _out.WriteLine($"   installed_files    : {fileRows.Count} entries, {fileOnly.Count} files (rest are directories)");
        _out.WriteLine($"   installed DLLs     : {registry.InstalledDlls.Count} -> {string.Join(", ", registry.InstalledDlls)}");
        _out.WriteLine($"   installed DLC      : {string.Join(", ", registry.InstalledDlc.Select(kv => $"{kv.Key} {kv.Value}"))}");

        Section("3. Metadata round-trip (ToJson -> FromJson -> ToJson)");
        var roundTripFailures = new List<string>();
        foreach (var im in installed)
        {
            try
            {
                var json1 = im.Module.ToJson();
                var again = CkanModule.FromJson(json1);
                var json2 = again.ToJson();
                if (json1 != json2 || again.version.ToString() != im.Module.version.ToString())
                {
                    roundTripFailures.Add($"{im.identifier}: json differs after round trip");
                }
            }
            catch (Exception ex)
            {
                roundTripFailures.Add($"{im.identifier}: {ex.GetType().Name} {ex.Message}");
            }
        }
        Check(roundTripFailures.Count == 0, $"all {installed.Count} source_module records round-trip byte-identically", roundTripFailures.Count == 0 ? null : string.Join("\n      ", roundTripFailures.Take(10)), ref failures);
        var epochs = installed.Where(m => m.Module.version.ToString().Contains(':')).ToList();
        _out.WriteLine($"   Versions with epochs: {epochs.Count} e.g. {string.Join(", ", epochs.Take(4).Select(m => $"{m.identifier}={m.Module.version}"))}");

        if (o.LockedCkanFile is not null && File.Exists(o.LockedCkanFile))
        {
            Section("4. Hand-made metapackage (" + Path.GetFileName(o.LockedCkanFile) + ")");
            var raw = File.ReadAllText(o.LockedCkanFile);
            string? rawError = null;
            try { CkanModule.FromJson(raw); } catch (Exception ex) { rawError = $"{ex.GetType().Name}: {ex.Message}"; }
            _out.WriteLine($"   Raw parse: {(rawError is null ? "ACCEPTED (unexpected)" : "rejected -> " + rawError)}");
            try
            {
                var imported = CkanFileImporter.Import(raw);
                foreach (var w in imported.Warnings)
                {
                    _out.WriteLine($"   normalizer: {w}");
                }
                var pins = CkanFileImporter.ExactPins(imported.Module);
                Check(imported.Module.IsMetapackage && pins.Count > 0, $"normalized file parses as metapackage with {pins.Count} exact pins", null, ref failures);
                var pinSet = pins.ToDictionary(p => p.Identifier, p => p.Version);
                var mismatch = packages.Where(p => pinSet.TryGetValue(p.identifier, out var v) && v != p.Module.version.ToString()).ToList();
                var missing = packages.Where(p => !pinSet.ContainsKey(p.identifier) && p.identifier != "VMP-Train-Career").ToList();
                _out.WriteLine($"   pins vs registry: {mismatch.Count} version mismatches, {missing.Count} installed packages not pinned{(missing.Count > 0 ? " (" + string.Join(", ", missing.Take(5).Select(m => m.identifier)) + ")" : "")}");
            }
            catch (Exception ex)
            {
                Fail($"normalized metapackage failed: {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        Section("5. Pack repository as the only repository (fake instance)");
        var fakeRoot = Path.Combine(o.Paths.DataRoot, "probe", "fake-ksp");
        if (Directory.Exists(fakeRoot))
        {
            Directory.Delete(fakeRoot, recursive: true);
        }
        Directory.CreateDirectory(Path.Combine(fakeRoot, "GameData"));
        File.WriteAllText(Path.Combine(fakeRoot, "KSP_x64.exe"), string.Empty);
        File.WriteAllText(Path.Combine(fakeRoot, "buildID64.txt"), "build id = 03190\n2022.12.12 at 14:02:28 PST\nBranch: master\nlanguage: en-us\ndistribution name: Steam\n");
        File.WriteAllText(Path.Combine(fakeRoot, "readme.txt"), "Kerbal Space Program\nVersion 1.12.5\n");
        var fakeInst = new GameInstance(game, fakeRoot, "probe", user);
        _out.WriteLine($"   Fake instance valid: {fakeInst.Valid}  version {fakeInst.Version()}");
        fakeInst.SetCompatibleVersions(hostInst.CompatibleVersions.ToList());

        // Sample: a few real packages plus their dependency closure from the host registry.
        var sample = SampleClosure(packages, o.SampleModules);
        _out.WriteLine($"   Sample modules ({sample.Count}): {string.Join(", ", sample.Select(m => $"{m.identifier}={m.version}"))}");
        var repoZip = PackRepoBuilder.Build(sample, Path.Combine(o.Paths.DataRoot, "probe", "packrepo.zip"));
        var repo = new Repository("vmpack-probe", new Uri(repoZip));
        _out.WriteLine($"   Repo zip: {repoZip} ({new FileInfo(repoZip).Length} bytes) uri {repo.uri}");

        var fakeRepoData = new RepositoryDataManager(Path.Combine(o.Paths.DataRoot, "probe", "repos"));
        var regMgr = RegistryManager.Instance(fakeInst, fakeRepoData, new[] { repo });
        try
        {
            regMgr.registry.RepositoriesSet(new SortedDictionary<string, Repository> { [repo.name] = repo });
            var downloader = new NetAsyncDownloader(user, () => SHA256.Create(), "VmpLauncher-probe");
            RepositoryDataManager.UpdateResult? result = null;
            string? updateError = null;
            try
            {
                result = fakeRepoData.Update(new[] { repo }, game, skipETags: true, downloader, user, "VmpLauncher-probe");
            }
            catch (Exception ex)
            {
                updateError = $"{ex.GetType().Name}: {ex.Message}";
            }
            _out.WriteLine($"   RepositoryDataManager.Update(file://zip) -> {(updateError ?? result?.ToString())}");
            var available = fakeRepoData.GetAllAvailableModules(new[] { repo }).ToList();
            Check(updateError is null && available.Count == sample.Count, $"pack repo loads via file:// ({available.Count}/{sample.Count} modules available)", updateError, ref failures);

            if (available.Count == 0)
            {
                _out.WriteLine("   Trying plan B: RepositoryData.SaveTo + Prepopulate");
                var data = new RepositoryData(sample, null, null, null, false);
                var path = Path.Combine(o.Paths.DataRoot, "probe", "repos", $"{NetFileCache.CreateURLHash(repo.uri)}-{repo.name}.json");
                data.SaveTo(path);
                fakeRepoData.Prepopulate(new[] { repo }, null);
                available = fakeRepoData.GetAllAvailableModules(new[] { repo }).ToList();
                Check(available.Count == sample.Count, $"plan B loads ({available.Count}/{sample.Count})", null, ref failures);
            }

            // Exact resolution: every module in the lock set as requested; resolver must return the same set.
            var opts = RelationshipResolverOptions.DependsOnlyOpts(fakeInst.StabilityToleranceConfig);
            opts.with_recommends = false;
            opts.with_suggests = false;
            opts.allow_incompatible = true;
            opts.proceed_with_inconsistencies = false;
            try
            {
                var resolver = new RelationshipResolver(sample, null, opts, regMgr.registry, game, fakeInst.VersionCriteria());
                var list = resolver.ModList().ToList();
                var same = list.Count == sample.Count && list.All(m => sample.Any(s => s.identifier == m.identifier && s.version.ToString() == m.version.ToString()));
                Check(resolver.IsConsistent && same, $"resolver returns exactly the pinned set ({list.Count} modules, consistent={resolver.IsConsistent})", same ? null : string.Join(", ", list.Select(m => $"{m.identifier}={m.version}")), ref failures);
                // Prove the resolver cannot pick another version: the registry only knows the pinned ones.
                var first = sample[0];
                var latest = regMgr.registry.LatestAvailable(first.identifier, fakeInst.StabilityToleranceConfig, fakeInst.VersionCriteria());
                Check(latest?.version.ToString() == first.version.ToString(), $"LatestAvailable({first.identifier}) == pinned {first.version}", latest?.version.ToString(), ref failures);
            }
            catch (Exception ex)
            {
                Fail($"resolution threw {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }
        finally
        {
            RegistryManager.DisposeInstance(fakeInst);
        }

        Section("6. Cache + FindInstallableFiles vs registry installed_files");
        var cacheDir = o.HostCacheDir ?? o.Paths.CacheDir;
        using (var cache = new NetModuleCache(cacheDir))
        {
            _out.WriteLine($"   Cache dir: {cacheDir}");
            var cached = packages.Where(p => cache.IsMaybeCachedZip(p.Module)).ToList();
            _out.WriteLine($"   Packages with a cached archive: {cached.Count}/{packages.Count}");
            var hostFilesByOwner = fileOnly.GroupBy(r => r.owner.identifier).ToDictionary(g => g.Key, g => g.Select(r => r.relPath).ToHashSet(Platform.PathComparer));
            var compared = 0; var identical = 0; var diffs = new List<string>();
            sw.Restart();
            foreach (var p in cached)
            {
                var zip = cache.GetCachedFilename(p.Module);
                if (zip is null)
                {
                    continue;
                }
                try
                {
                    var expected = ModuleInstaller.FindInstallableFiles(p.Module, zip, game)
                        .Where(f => !f.source.IsDirectory && !f.destination.EndsWith(".ckan", StringComparison.OrdinalIgnoreCase))
                        .Select(f => CKANPathUtils.NormalizePath(f.destination))
                        .ToHashSet(Platform.PathComparer);
                    compared++;
                    var actual = hostFilesByOwner.TryGetValue(p.identifier, out var set) ? set : new HashSet<string>(Platform.PathComparer);
                    if (expected.SetEquals(actual))
                    {
                        identical++;
                    }
                    else
                    {
                        var onlyExpected = expected.Except(actual).Take(3);
                        var onlyActual = actual.Except(expected).Take(3);
                        diffs.Add($"{p.identifier}: expected {expected.Count} vs registry {actual.Count}; +[{string.Join(", ", onlyExpected)}] -[{string.Join(", ", onlyActual)}]");
                    }
                }
                catch (Exception ex)
                {
                    diffs.Add($"{p.identifier}: {ex.GetType().Name} {ex.Message}");
                }
            }
            _out.WriteLine($"   Compared {compared} modules in {sw.ElapsedMilliseconds} ms: {identical} identical file sets");
            foreach (var d in diffs.Take(12))
            {
                _out.WriteLine($"      diff {d}");
            }
            Check(compared > 0 && diffs.Count == 0, "FindInstallableFiles reproduces registry installed_files for sampled modules", diffs.Count == 0 ? null : $"{diffs.Count} differ", ref failures);
        }

        Section("7. Side effects under %LOCALAPPDATA%\\CKAN");
        var appDataAfter = Snapshot(ckanAppData);
        var changed = appDataAfter.Where(kv => !appDataBefore.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToList();
        _out.WriteLine(changed.Count == 0 ? "   (no files changed)" : "   changed: " + string.Join(", ", changed));
        Check(changed.All(c => c.EndsWith("builds-ksp.json", StringComparison.OrdinalIgnoreCase)), "only builds-ksp.json may change in the user's CKAN folder", changed.Count == 0 ? null : string.Join(", ", changed), ref failures);

        _out.WriteLine();
        _out.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 2;
    }

    private static List<CkanModule> SampleClosure(List<InstalledModule> packages, int count)
    {
        var byId = packages.ToDictionary(p => p.identifier, p => p.Module);
        var picked = new Dictionary<string, CkanModule>();
        // Prefer modules with dependencies so the closure is meaningful; ModuleManager first because everyone depends on it.
        var seeds = packages.OrderByDescending(p => p.Module.depends?.Count ?? 0).Take(count).Select(p => p.Module);
        var queue = new Queue<CkanModule>(seeds);
        while (queue.Count > 0 && picked.Count < 60)
        {
            var m = queue.Dequeue();
            if (!picked.TryAdd(m.identifier, m))
            {
                continue;
            }
            foreach (var d in m.depends?.OfType<ModuleRelationshipDescriptor>() ?? Enumerable.Empty<ModuleRelationshipDescriptor>())
            {
                if (byId.TryGetValue(d.name, out var dep))
                {
                    queue.Enqueue(dep);
                }
                else
                {
                    // dependency satisfied via "provides" by some installed module
                    var provider = packages.FirstOrDefault(p => p.Module.provides?.Contains(d.name) == true);
                    if (provider is not null)
                    {
                        queue.Enqueue(provider.Module);
                    }
                }
            }
        }
        return picked.Values.ToList();
    }

    private static Dictionary<string, long> Snapshot(string dir)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
        {
            return result;
        }
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            var fi = new FileInfo(f);
            result[f] = fi.Length ^ fi.LastWriteTimeUtc.Ticks;
        }
        return result;
    }

    private void Section(string title)
    {
        _out.WriteLine();
        _out.WriteLine($"== {title}");
    }

    private void Check(bool ok, string what, string? detail, ref int failures)
    {
        _out.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok)
        {
            failures++;
            if (detail is not null)
            {
                _out.WriteLine($"      {detail}");
            }
        }
    }

    private void Fail(string what) => _out.WriteLine($"   [FAIL] {what}");
}
