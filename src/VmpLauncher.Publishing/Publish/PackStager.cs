using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Core.Transactions;

namespace VmpLauncher.Publishing.Publish;

/// <summary>
/// Copies a verified export into the packs repository layout: <c>packs/&lt;id&gt;/&lt;version&gt;/</c>, the mutable
/// top-level files (channel pointer, current policy, pack definition), the changelog, catalog and share file.
/// Pure file work; git is the publisher's business.
/// </summary>
public sealed class PackStager
{
    public sealed record Request(
        string RepoDir,
        string ExportVersionDir,
        string ExportChannelFile,
        PackLock Lock,
        string? Notes,
        string? PolicySummary,
        PublisherKey? Key,
        string? CatalogUrl,
        DateTimeOffset Now);

    public sealed record Result(
        string PackDir,
        string VersionDir,
        IReadOnlyList<string> Written,
        string ChangelogEntry,
        bool PolicyChanged,
        bool ServersJsonUpdated,
        PackLock? PreviousLock,
        LockDiff? Diff);

    private static readonly string[] VersionFiles =
    {
        "inventory.json.gz", "KSPModControl.xml", "client-settings-template.xml", "capture-report.md",
    };

    public Result Stage(Request r)
    {
        var id = r.Lock.Pack.Id;
        var version = r.Lock.Pack.Version;
        var packDir = Path.Combine(r.RepoDir, "packs", id);
        var versionDir = Path.Combine(packDir, version);
        var written = new List<string>();
        Directory.CreateDirectory(versionDir);

        // Previous state, for the diff and the changelog.
        PackLock? previous = null;
        var prevChannel = Path.Combine(packDir, "channel.json");
        if (File.Exists(prevChannel))
        {
            var ch = JsonNode.Parse(File.ReadAllText(prevChannel))!;
            var rel = ch["channels"]?["stable"]?["lock"]?.GetValue<string>();
            if (rel is not null && File.Exists(Path.Combine(packDir, rel)))
            {
                previous = LauncherJson.ReadFile<PackLock>(Path.Combine(packDir, rel));
            }
        }
        if (previous is not null && previous.Pack.Version == version)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"Version {version} is already the published version of {id}. Pick a new version.", RecoveryHint.None);
        }
        var diff = previous is null ? null : LockDiff.Compute(previous, r.Lock);

        // Immutable version folder.
        var lockName = Directory.EnumerateFiles(r.ExportVersionDir, "*.vmpack.lock.json").Select(Path.GetFileName).First()!;
        foreach (var name in new[] { lockName, lockName + ".sig", $"{id}.vmpack.json" }.Concat(VersionFiles))
        {
            var src = Path.Combine(r.ExportVersionDir, name);
            if (File.Exists(src))
            {
                Copy(src, Path.Combine(versionDir, name), r.RepoDir, written);
            }
        }

        // Mutable top-level files.
        var newPolicy = Path.Combine(r.ExportVersionDir, "KSPModControl.xml");
        var curPolicy = Path.Combine(packDir, "KSPModControl.xml");
        var policyChanged = File.Exists(newPolicy) && !(File.Exists(curPolicy) && Sha256Util.HexEquals(Sha256Util.HashFile(newPolicy), Sha256Util.HashFile(curPolicy)));
        if (File.Exists(newPolicy))
        {
            Copy(newPolicy, curPolicy, r.RepoDir, written);
        }
        Copy(Path.Combine(r.ExportVersionDir, $"{id}.vmpack.json"), Path.Combine(packDir, $"{id}.vmpack.json"), r.RepoDir, written);
        if (!File.Exists(r.ExportChannelFile))
        {
            throw new LauncherException(ErrorCode.InvalidPack, "The export has no channel.json (was it signed?).", RecoveryHint.None);
        }
        Copy(r.ExportChannelFile, prevChannel, r.RepoDir, written);
        if (File.Exists(r.ExportChannelFile + ".sig"))
        {
            Copy(r.ExportChannelFile + ".sig", prevChannel + ".sig", r.RepoDir, written);
        }

        // servers.json must demand the client build the pack installs.
        var serversUpdated = false;
        var serversPath = Path.Combine(packDir, "servers.json");
        if (File.Exists(serversPath) && r.Lock.Multiplayer.RequiredClientVersion is { } required)
        {
            var node = JsonNode.Parse(File.ReadAllText(serversPath))!;
            var current = node["server"]?["requiredBuild"]?.GetValue<string>();
            if (!string.Equals(current, required, StringComparison.Ordinal))
            {
                if (r.Key is null)
                {
                    throw new LauncherException(ErrorCode.InvalidPack, $"servers.json requires build {current} but the pack installs {required}; a signing key is needed to update it.", RecoveryHint.None);
                }
                node["server"]!["requiredBuild"] = required;
                node["sequence"] = (node["sequence"]?.GetValue<long>() ?? 0) + 1;
                node["issuedAt"] = r.Now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                File.WriteAllText(serversPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
                r.Key.SignFile(serversPath);
                written.Add(Rel(r.RepoDir, serversPath));
                written.Add(Rel(r.RepoDir, serversPath + ".sig"));
                serversUpdated = true;
            }
        }

        // Changelog.
        var entry = BuildChangelogEntry(version, r.Notes, diff, r.Lock, policyChanged ? r.PolicySummary ?? "updated" : "unchanged", r.Now);
        var changelog = Path.Combine(packDir, "CHANGELOG.md");
        var header = $"# {r.Lock.Pack.Name}";
        var existing = File.Exists(changelog) ? File.ReadAllText(changelog) : header + "\n";
        var body = existing.StartsWith(header, StringComparison.Ordinal) ? existing[header.Length..].TrimStart('\r', '\n') : existing;
        File.WriteAllText(changelog, header + "\n\n" + entry + "\n" + body, new UTF8Encoding(false));
        written.Add(Rel(r.RepoDir, changelog));

        // Catalog entry, share file, byte-exact storage.
        var catalog = Path.Combine(r.RepoDir, "catalog.json");
        var arr = File.Exists(catalog) ? JsonNode.Parse(File.ReadAllText(catalog)) as JsonArray ?? new JsonArray() : new JsonArray();
        if (!arr.Any(e => e?["id"]?.GetValue<string>() == id))
        {
            arr.Add(new JsonObject { ["id"] = id, ["name"] = r.Lock.Pack.Name, ["path"] = $"packs/{id}", ["bannerUrl"] = null, ["minLauncherVersion"] = "0.1.0" });
            File.WriteAllText(catalog, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            written.Add("catalog.json");
        }
        var share = Path.Combine(packDir, "share", $"{id}.vmpack");
        if (!File.Exists(share) && r.CatalogUrl is not null && r.Key is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(share)!);
            LauncherJson.WriteFile(share, new { schema = "vmpack-share/1", catalogUrl = r.CatalogUrl, packId = id, publisherKeyId = r.Key.KeyId });
            written.Add(Rel(r.RepoDir, share));
        }
        var attributes = Path.Combine(r.RepoDir, ".gitattributes");
        if (!File.Exists(attributes) || !File.ReadLines(attributes).Any(l => l.Trim() == "* -text"))
        {
            File.AppendAllText(attributes, (File.Exists(attributes) ? "\n" : "") + "# Pack files are served byte-for-byte and some are signed over exact bytes: never rewrite line endings.\n* -text\n", new UTF8Encoding(false));
            written.Add(".gitattributes");
        }

        return new Result(packDir, versionDir, written, entry, policyChanged, serversUpdated, previous, diff);
    }

    public static string BuildChangelogEntry(string version, string? notes, LockDiff? diff, PackLock lk, string policySummary, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(version).Append(" (").Append(now.ToString("yyyy-MM-dd")).AppendLine(")");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(notes))
        {
            sb.AppendLine(notes.Trim());
            sb.AppendLine();
        }
        if (diff is null)
        {
            var packages = lk.Ckan.Modules.Count(m => m.Kind == ModuleKindDto.Package);
            sb.AppendLine($"- First published version: {packages} packages pinned to exact versions.");
        }
        else
        {
            foreach (var m in diff.Added.OrderBy(m => m.Identifier, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- Added: {m.Identifier} {m.Version}");
            }
            foreach (var (o, n) in diff.Changed.OrderBy(c => c.New.Identifier, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(o.Version == n.Version ? $"- Rebuilt: {n.Identifier} {n.Version}" : $"- Updated: {n.Identifier} {o.Version} -> {n.Version}");
            }
            foreach (var m in diff.Removed.OrderBy(m => m.Identifier, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- Removed: {m.Identifier} {m.Version}");
            }
            if (diff.OverridesAdded.Count + diff.OverridesChanged.Count + diff.OverridesRemoved.Count > 0)
            {
                sb.AppendLine($"- Overrides: +{diff.OverridesAdded.Count} ~{diff.OverridesChanged.Count} -{diff.OverridesRemoved.Count}");
            }
            if (diff.Added.Count + diff.Changed.Count + diff.Removed.Count == 0)
            {
                sb.AppendLine("- No module changes.");
            }
        }
        var sideload = lk.Ckan.Modules.Where(m => m.Artifact?.Distribution == "private-sideload").ToList();
        if (diff is not null && sideload.Any(s => diff.Added.Concat(diff.Changed.Select(c => c.New)).Any(c => c.Identifier == s.Identifier)))
        {
            sb.AppendLine($"- Private package(s) changed: ask the host for {string.Join(", ", sideload.Select(s => s.Artifact!.FileName))}.");
        }
        sb.AppendLine($"- Server policy: {policySummary}.");
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal); // the changelog is LF like the rest of the repository
    }

    private static void Copy(string src, string dst, string repoDir, List<string> written)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
        written.Add(Rel(repoDir, dst));
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
