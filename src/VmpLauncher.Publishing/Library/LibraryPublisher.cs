using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CKAN;
using CKAN.Versioning;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Security;
using VmpLauncher.Net.Library;
using VmpLauncher.Publishing.Publish;

namespace VmpLauncher.Publishing.Library;

/// <summary>
/// Publishes Vlad's mod library: definition → build each mod (the same deterministic zip + CKAN module the packs use) →
/// merge the library fields into the module JSON → merge with what is already published → signed <c>library.json</c>.
/// Dry run by default: everything up to the signed file happens in <c>&lt;exportRoot&gt;\library</c> and every external command is
/// listed verbatim (release assets, copy into the packs repository, git push); <c>--execute</c> runs them in that order and then
/// fetches the published library back and verifies it.
/// </summary>
public sealed class LibraryPublisher
{
    public const string RemoteUrl = LibraryRemoteVerifier.DefaultUrl;

    public sealed record Options(PublishConfig Config, string DefinitionFile, bool Execute, bool AllowDirty, bool SkipRemoteCheck = false, string RemoteLibraryUrl = RemoteUrl);

    /// <param name="Added">True when this (identifier, version) was not in the library before this run.</param>
    public sealed record ModuleInfo(string Identifier, string Version, string Download, bool Added);

    /// <param name="Unchanged">Modules, showcase, name and tagline equal what is published: the sequence stays, nothing new is signed.</param>
    /// <param name="LibraryFile">The new signed library in the staging folder (the published one when unchanged).</param>
    public sealed record Report(
        long Sequence,
        bool Unchanged,
        string StagingDir,
        string LibraryFile,
        string? SignatureFile,
        IReadOnlyList<ModuleInfo> Modules,
        IReadOnlyList<Publisher.ExternalAction> Actions,
        IReadOnlyList<string> Warnings,
        bool Executed,
        string? PushedCommit,
        LibraryRemoteVerifier.Result? Remote,
        TimeSpan Elapsed);

    /// <summary>LibraryIndex refuses a library with more than 400 modules; warn well before that.</summary>
    private const int NearModuleLimit = 300;

    private const int RemoteAttempts = 12;
    private static readonly TimeSpan RemoteInterval = TimeSpan.FromSeconds(15);

    /// <summary>Fields first, in this order, so every module reads the same way; anything else keeps the builder's order after them.</summary>
    private static readonly string[] ModuleFieldOrder =
    {
        "spec_version", "identifier", "name", "abstract", "description", "author", "license", "tags", "resources", "version",
        "ksp_version", "ksp_version_min", "ksp_version_max", "depends", "recommends", "conflicts", "provides",
    };

    /// <summary>LF line endings (the repository is byte-exact and the signature covers exact bytes) and readable escaping.</summary>
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TextWriter _log;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly SignatureVerifier _pinned;
    private readonly Publisher _assets;

    /// <param name="pinned">The keys the launcher trusts (default: the ones compiled into it). A library signed by any other key would be refused by every launcher.</param>
    public LibraryPublisher(TextWriter log, TimeProvider? clock = null, HttpClient? http = null, SignatureVerifier? pinned = null)
    {
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _pinned = pinned ?? SignatureVerifier.Default();
        _assets = new Publisher(log, _clock, _http);
    }

    public Report Run(Options o, PublisherKey key, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var c = o.Config;
        var warnings = new List<string>();
        var definitionFile = Path.GetFullPath(o.DefinitionFile);
        var definition = LibraryDefinition.Load(definitionFile);
        var packDir = Path.GetDirectoryName(definitionFile)!;
        _log.WriteLine($"== library {definition.Id} ({(o.Execute ? "EXECUTE" : "dry run")}) ==");

        if (!_pinned.Keys.Any(k => string.Equals(k.KeyId, key.KeyId, StringComparison.Ordinal) && !k.Revoked))
        {
            var message = $"The signing key '{key.KeyId}' is not one of the launcher's pinned publisher keys, so every launcher would refuse this library.";
            if (o.Execute)
            {
                throw new LauncherException(ErrorCode.InvalidPack, message + " Nothing was published.", RecoveryHint.None);
            }
            warnings.Add(message);
        }

        // 1. The state the library will be merged into: the committed copy in the packs repository.
        if (o.Execute)
        {
            Publisher.EnsureCleanRepo(c, warnings);
        }
        var repoLibraryDir = Path.Combine(c.PacksRepoDir, "library");
        var publishedFile = Path.Combine(repoLibraryDir, "library.json");
        var previous = ReadPrevious(publishedFile);
        _log.WriteLine(previous is null
            ? "No library published yet: this will be sequence 1."
            : $"Published library: sequence {previous.Index.Sequence}, {previous.Index.Modules.Count} module(s).");

        // 2. Build every mod into the staging area (never the repository).
        var stagingDir = Path.Combine(c.ExportRoot, "library");
        var assetsDir = Path.Combine(stagingDir, "assets");
        var libraryFile = Path.Combine(stagingDir, "library.json");
        var signatureFile = libraryFile + ".sig";
        Directory.CreateDirectory(stagingDir);
        foreach (var stale in new[] { libraryFile, signatureFile })
        {
            File.Delete(stale);
        }
        if (Directory.Exists(assetsDir))
        {
            Directory.Delete(assetsDir, recursive: true);
        }
        Directory.CreateDirectory(assetsDir);
        var built = new List<BuiltMod>();
        foreach (var mod in definition.Mods)
        {
            ct.ThrowIfCancellationRequested();
            var b = CustomPackageBuilder.Build(mod.Package, packDir, assetsDir, o.AllowDirty);
            warnings.AddRange(b.Warnings);
            var module = BuildModule(mod, b);
            built.Add(new BuiltMod(mod, b, module));
            _log.WriteLine($"Built {mod.Package.Id} {b.Version}: {b.FileName}, {b.Size:N0} bytes, SHA-256 {b.Sha256}");
        }

        // 3. Merge with every module already published; a published version is immutable.
        var modules = new Dictionary<(string Id, string Version), JsonObject>();
        var added = new HashSet<(string Id, string Version)>();
        if (previous is not null)
        {
            foreach (var m in previous.Index.Modules)
            {
                modules[(m.Identifier, m.Version)] = JsonNode.Parse(m.Json.GetRawText())!.AsObject();
            }
        }
        foreach (var b in built)
        {
            var moduleKey = (ModuleString(b.Module, "identifier"), ModuleString(b.Module, "version"));
            if (modules.TryGetValue(moduleKey, out var old))
            {
                RefuseIfDifferent(b, old, moduleKey);
            }
            else
            {
                modules[moduleKey] = b.Module;
                added.Add(moduleKey);
            }
        }
        var ordered = modules.OrderBy(kv => kv.Key.Id, StringComparer.Ordinal).ThenBy(kv => kv.Key.Version, VersionComparer.Instance).ToList();
        var moduleArray = new JsonArray(ordered.Select(kv => (JsonNode)kv.Value.DeepClone()).ToArray());
        var showcase = BuildShowcase(definition, previous);

        // 4. Same as published? Then the sequence stays and nothing new is signed.
        var comparable = Comparable(definition.Name, definition.Tagline, showcase, moduleArray);
        var unchanged = previous is not null && string.Equals(comparable, Comparable(previous.Root), StringComparison.Ordinal);
        var summary = ordered.Select(kv => new ModuleInfo(kv.Key.Id, kv.Key.Version, ModuleString(kv.Value, "download"), added.Contains(kv.Key))).ToList();
        long sequence;
        string finalLibraryFile;
        string? finalSignatureFile = null;
        byte[] bytes;
        if (unchanged)
        {
            sequence = previous!.Index.Sequence;
            bytes = previous.Bytes;
            finalLibraryFile = publishedFile;
            _log.WriteLine($"Library unchanged: still sequence {sequence}; nothing new to sign or publish.");
        }
        else
        {
            sequence = previous is null ? 1 : previous.Index.Sequence + 1;
            var root = new JsonObject
            {
                ["schemaVersion"] = LibraryIndex.CurrentSchemaVersion,
                ["libraryId"] = definition.Id,
                ["name"] = definition.Name,
                ["tagline"] = definition.Tagline,
                ["sequence"] = sequence,
                ["issuedAt"] = _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["showcase"] = showcase,
                ["modules"] = moduleArray,
            };
            bytes = new UTF8Encoding(false).GetBytes(root.ToJsonString(JsonOut) + "\n");
            Verify(bytes, sequence, built);
            File.WriteAllBytes(libraryFile, bytes);
            key.SignFile(libraryFile);
            var roundTrip = new SignatureVerifier(new[] { key.PublicInfo() }).VerifyFile(libraryFile, out _, out var reason);
            if (roundTrip != TrustLevel.VerifiedPublisher)
            {
                throw new LauncherException(ErrorCode.VerifyFailed, $"The signature just produced for library.json does not verify ({reason}); nothing was staged for publishing.", RecoveryHint.None);
            }
            finalLibraryFile = libraryFile;
            finalSignatureFile = signatureFile;
            _log.WriteLine($"Library: sequence {sequence}, {summary.Count} module(s), {bytes.Length:N0} bytes, signed by {key.KeyId}. Staged in {stagingDir}");
        }
        foreach (var m in summary)
        {
            _log.WriteLine($"  {(m.Added ? "new " : "kept")} {m.Identifier} {m.Version}");
        }
        if (!File.Exists(Path.Combine(c.PacksRepoDir, ".gitattributes")) || !File.ReadLines(Path.Combine(c.PacksRepoDir, ".gitattributes")).Any(l => l.Trim() == "* -text"))
        {
            warnings.Add("The packs repository has no '* -text' rule in .gitattributes: git could rewrite line endings and break the signature.");
        }
        if (bytes.Length > LibraryIndex.MaxBytes / 4 * 3 || summary.Count > NearModuleLimit)
        {
            warnings.Add($"The library holds {summary.Count} module version(s) in {bytes.Length:N0} bytes. The launcher refuses more than {LibraryIndex.MaxBytes:N0} bytes or 400 modules, and every published version stays in the list.");
        }

        // 5. External actions, in the order they run.
        var actions = new List<Publisher.ExternalAction>();
        foreach (var b in built)
        {
            var source = b.Mod.Package;
            var notes = $"Mod of {definition.Name}. SHA-256 {b.Built.Sha256}.";
            var action = _assets.PlanReleaseAsset(source.Id, source.Name ?? source.Id, b.Built.Version, source.Distribution.Repo, source.Distribution.TagPrefix,
                ModuleString(b.Module, "download"), b.Built.Sha256, b.Built.ZipPath, notes, ct);
            if (action is not null)
            {
                actions.Add(action);
            }
        }
        if (!unchanged)
        {
            actions.Add(PlanCopy(libraryFile, signatureFile, repoLibraryDir));
            actions.Add(PlanGit(c, sequence, added, ordered.Count));
        }

        // 6. Show the plan.
        _log.WriteLine();
        if (actions.Count == 0)
        {
            _log.WriteLine("No external actions: everything is already published.");
        }
        else
        {
            _log.WriteLine(o.Execute ? "External actions:" : "External actions that --execute would run:");
            foreach (var a in actions)
            {
                _log.WriteLine($"- {a.Title}");
                foreach (var cmd in a.Commands)
                {
                    _log.WriteLine($"    {cmd}");
                }
            }
        }

        // 7. Execute, then check what friends will see.
        string? pushed = null;
        LibraryRemoteVerifier.Result? remote = null;
        if (o.Execute)
        {
            var tools = new ExternalTools(_log);
            foreach (var a in actions)
            {
                _log.WriteLine();
                _log.WriteLine($">> {a.Title}");
                a.Run(tools);
            }
            if (!unchanged)
            {
                pushed = tools.Git(c.PacksRepoDir, "rev-parse", "HEAD").StdOut.Trim();
                if (!o.SkipRemoteCheck)
                {
                    _log.WriteLine();
                    _log.WriteLine("Fetching the published library back (retrying while the CDN catches up)...");
                    remote = new LibraryRemoteVerifier(_http, _pinned)
                        .VerifyWithRetryAsync(o.RemoteLibraryUrl, sequence, Sha256Util.HashBytes(bytes), RemoteAttempts, RemoteInterval, _log, ct)
                        .GetAwaiter().GetResult();
                }
            }
        }
        return new Report(sequence, unchanged, stagingDir, finalLibraryFile, finalSignatureFile, summary, actions, warnings, o.Execute, pushed, remote, sw.Elapsed);
    }

    private sealed record BuiltMod(LibraryModDefinition Mod, CustomPackageBuilder.Built Built, JsonObject Module);

    private sealed record Previous(LibraryIndex Index, byte[] Bytes, JsonObject Root);

    private static Previous? ReadPrevious(string publishedFile)
    {
        if (!File.Exists(publishedFile))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(publishedFile);
        LibraryIndex index;
        try
        {
            index = LibraryIndex.Parse(bytes);
        }
        catch (LauncherException ex)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The library already in the packs repository ({publishedFile}) is not valid, so a new one cannot be merged into it: {ex.Message}", RecoveryHint.None, ex, ex.Details);
        }
        if (!string.Equals(index.LibraryId, LibraryIndex.VladLibraryId, StringComparison.Ordinal))
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"{publishedFile} is the library '{index.LibraryId}', not '{LibraryIndex.VladLibraryId}'.", RecoveryHint.None);
        }
        return new Previous(index, bytes, JsonNode.Parse(bytes)!.AsObject());
    }

    /// <summary>The builder's module plus the library's own fields, checked as CKAN metadata.</summary>
    private static JsonObject BuildModule(LibraryModDefinition mod, CustomPackageBuilder.Built built)
    {
        var node = JsonNode.Parse(built.Module.ToJson())!.AsObject();

        // CKAN's serializer leaves out install_to when it is the default ("GameData"). The launcher's library parser wants it
        // spelled out, and spelling out the default changes nothing for CKAN.
        if (node["install"] is JsonArray install)
        {
            foreach (var step in install.OfType<JsonObject>().Where(s => !s.ContainsKey("install_to")))
            {
                step["install_to"] = "GameData";
            }
        }
        if (!string.IsNullOrWhiteSpace(mod.Description))
        {
            node["description"] = mod.Description.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        if (mod.Tags.Count > 0)
        {
            node["tags"] = new JsonArray(mod.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        }
        if (mod.Resources.Count > 0)
        {
            var resources = new JsonObject();
            foreach (var (name, url) in mod.Resources.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                resources[name] = url;
            }
            node["resources"] = resources;
        }
        if (mod.Depends.Count > 0)
        {
            node["depends"] = Relations(mod.Depends);
        }
        if (mod.Recommends.Count > 0)
        {
            node["recommends"] = Relations(mod.Recommends);
        }
        if (mod.KspVersionMin is not null || mod.KspVersionMax is not null)
        {
            node.Remove("ksp_version");
            if (mod.KspVersionMin is not null)
            {
                node["ksp_version_min"] = mod.KspVersionMin;
            }
            if (mod.KspVersionMax is not null)
            {
                node["ksp_version_max"] = mod.KspVersionMax;
            }
        }
        var ordered = Reorder(node);
        try
        {
            CkanModule.FromJson(ordered.ToJsonString());
        }
        catch (Exception ex) when (ex is Kraken or Newtonsoft.Json.JsonException)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"{mod.Package.Id}: the module metadata is not valid CKAN: {ex.Message}", RecoveryHint.None, ex);
        }
        return ordered;
    }

    private static JsonArray Relations(IEnumerable<LibraryRelation> relations)
    {
        var array = new JsonArray();
        foreach (var r in relations)
        {
            var o = new JsonObject { ["name"] = r.Name };
            if (r.Version is not null)
            {
                o["version"] = r.Version;
            }
            if (r.MinVersion is not null)
            {
                o["min_version"] = r.MinVersion;
            }
            if (r.MaxVersion is not null)
            {
                o["max_version"] = r.MaxVersion;
            }
            array.Add(o);
        }
        return array;
    }

    private static JsonObject Reorder(JsonObject node)
    {
        var result = new JsonObject();
        var props = node.ToList();
        foreach (var name in ModuleFieldOrder)
        {
            var i = props.FindIndex(p => string.Equals(p.Key, name, StringComparison.Ordinal));
            if (i < 0)
            {
                continue;
            }
            result[name] = props[i].Value?.DeepClone();
            props.RemoveAt(i);
        }
        foreach (var (name, value) in props)
        {
            result[name] = value?.DeepClone();
        }
        return result;
    }

    private static string ModuleString(JsonObject module, string name)
        => module[name]?.GetValue<string>() ?? throw new LauncherException(ErrorCode.InvalidPack, $"A module has no '{name}'.", RecoveryHint.None);

    /// <summary>A published (identifier, version) never changes: the same name with different content would leave friends with a list that does not match what they downloaded.</summary>
    private static void RefuseIfDifferent(BuiltMod built, JsonObject published, (string Id, string Version) key)
    {
        var mine = JsonCanonicalizer.Canonicalize(built.Module.ToJsonString());
        if (string.Equals(mine, JsonCanonicalizer.Canonicalize(published.ToJsonString()), StringComparison.Ordinal))
        {
            return;
        }
        var names = built.Module.Select(p => p.Key).Union(published.Select(p => p.Key), StringComparer.Ordinal)
            .Where(n => !string.Equals(JsonCanonicalizer.Canonicalize(built.Module[n]?.ToJsonString() ?? "null"), JsonCanonicalizer.Canonicalize(published[n]?.ToJsonString() ?? "null"), StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var content = names.Contains("download_hash") || names.Contains("download_size");
        throw new LauncherException(ErrorCode.InvalidPack,
            $"{key.Id} {key.Version} is already published with different {(content ? "content" : "metadata")} ({string.Join(", ", names)}). {built.Mod.Package.Name ?? key.Id} was rebuilt without a new version: bump its version and publish again.",
            RecoveryHint.None);
    }

    /// <summary>
    /// The showcase of every mod in the definition (the definition is the authority for those), plus the entries of published mods
    /// the definition no longer lists: their modules stay in the library, so does their look.
    /// </summary>
    private static JsonObject BuildShowcase(LibraryDefinition definition, Previous? previous)
    {
        var entries = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var defined = definition.Mods.Select(m => m.Package.Id).ToHashSet(StringComparer.Ordinal);
        if (previous?.Root["showcase"] is JsonObject old)
        {
            foreach (var (name, value) in old.Where(e => !defined.Contains(e.Key)))
            {
                entries[name] = value?.DeepClone();
            }
        }
        foreach (var mod in definition.Mods)
        {
            if (mod.Showcase is not { } s)
            {
                continue;
            }
            var entry = new JsonObject
            {
                ["tagline"] = s.Tagline,
                ["highlights"] = new JsonArray(s.Highlights.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
                ["accent"] = s.Accent,
                ["order"] = s.Order,
            };
            if (s.Banner is not null)
            {
                entry["banner"] = s.Banner;
            }
            entries[mod.Package.Id] = entry;
        }
        var result = new JsonObject();
        foreach (var (name, value) in entries)
        {
            result[name] = value;
        }
        return result;
    }

    /// <summary>What "unchanged" means: name, tagline, showcase and the set of modules; key order and formatting do not count.</summary>
    private static string Comparable(string name, string tagline, JsonObject showcase, JsonArray modules)
        => JsonCanonicalizer.Canonicalize(new JsonObject
        {
            ["name"] = name,
            ["tagline"] = tagline,
            ["showcase"] = showcase.DeepClone(),
            ["modules"] = modules.DeepClone(),
        }.ToJsonString());

    private static string Comparable(JsonObject previousRoot)
    {
        var modules = previousRoot["modules"] as JsonArray ?? new JsonArray();
        var sorted = modules.OfType<JsonObject>()
            .OrderBy(m => m["identifier"]?.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(m => m["version"]?.GetValue<string>() ?? string.Empty, VersionComparer.Instance)
            .Select(m => (JsonNode)m.DeepClone())
            .ToArray();
        return Comparable(previousRoot["name"]?.GetValue<string>() ?? string.Empty, previousRoot["tagline"]?.GetValue<string>() ?? string.Empty,
            previousRoot["showcase"] as JsonObject ?? new JsonObject(), new JsonArray(sorted));
    }

    /// <summary>The launcher's own strict parser must accept the document, and it must say what was meant.</summary>
    private static void Verify(byte[] bytes, long sequence, List<BuiltMod> built)
    {
        LibraryIndex parsed;
        try
        {
            parsed = LibraryIndex.Parse(bytes);
        }
        catch (LauncherException ex)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The library that was built would be refused by the launcher: {ex.Message} Nothing was staged for publishing.", RecoveryHint.None, ex, ex.Details);
        }
        var missing = built.Where(b => !parsed.Modules.Any(m => m.Identifier == b.Mod.Package.Id && m.Version == b.Built.Version)).Select(b => $"{b.Mod.Package.Id} {b.Built.Version}").ToList();
        if (parsed.Sequence != sequence || !string.Equals(parsed.LibraryId, LibraryIndex.VladLibraryId, StringComparison.Ordinal) || missing.Count > 0)
        {
            throw new LauncherException(ErrorCode.VerifyFailed, "The library that was built does not read back as intended.", RecoveryHint.None,
                details: missing.Select(m => "missing after parsing: " + m).Append($"sequence {parsed.Sequence}, expected {sequence}").ToList());
        }
    }

    private static Publisher.ExternalAction PlanCopy(string libraryFile, string signatureFile, string repoLibraryDir)
    {
        var destLibrary = Path.Combine(repoLibraryDir, "library.json");
        var destSignature = destLibrary + ".sig";
        return new Publisher.ExternalAction("Copy the library into the packs repository",
            new[] { ExternalTools.Describe("copy", new[] { libraryFile, destLibrary }), ExternalTools.Describe("copy", new[] { signatureFile, destSignature }) },
            _ =>
            {
                Directory.CreateDirectory(repoLibraryDir);
                File.Copy(libraryFile, destLibrary, overwrite: true);
                File.Copy(signatureFile, destSignature, overwrite: true);
            });
    }

    private static Publisher.ExternalAction PlanGit(PublishConfig c, long sequence, HashSet<(string Id, string Version)> added, int moduleCount)
    {
        var what = added.Count == 0
            ? "metadata update"
            : string.Join(", ", added.OrderBy(a => a.Id, StringComparer.Ordinal).ThenBy(a => a.Version, VersionComparer.Instance).Take(3).Select(a => $"{a.Id} {a.Version}"))
              + (added.Count > 3 ? $" and {added.Count - 3} more" : string.Empty);
        var commitMessage = $"Vlad's mod library {sequence}: {what}";
        var addArgs = new[] { "add", "library/library.json", "library/library.json.sig" };
        return new Publisher.ExternalAction("Publish the library (git)",
            new[]
            {
                ExternalTools.Describe("git", new[] { "-C", c.PacksRepoDir }.Concat(addArgs)),
                ExternalTools.Describe("git", new[] { "-C", c.PacksRepoDir, "commit", "-m", commitMessage }),
                ExternalTools.Describe("git", new[] { "-C", c.PacksRepoDir, "push", "origin", c.PacksRepoBranch }),
            },
            tools =>
            {
                tools.Git(c.PacksRepoDir, addArgs);
                tools.Git(c.PacksRepoDir, "-c", "core.safecrlf=false", "commit", "-q", "-m", commitMessage);
                tools.Git(c.PacksRepoDir, "push", "origin", c.PacksRepoBranch);
            });
    }

    /// <summary>Oldest first by CKAN's own version rules, ordinal on a tie, so the order never depends on the machine.</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var byVersion = new ModuleVersion(x ?? string.Empty).CompareTo(new ModuleVersion(y ?? string.Empty));
            return byVersion != 0 ? byVersion : string.CompareOrdinal(x, y);
        }
    }
}
