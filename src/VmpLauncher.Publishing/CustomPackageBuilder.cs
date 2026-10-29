using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CKAN;
using VmpLauncher.Ckan.Locking;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using VmpLauncher.Game;

namespace VmpLauncher.Publishing;

/// <summary>
/// Turns a local mod folder (VMP client build output, ApexGuidance publish output) into a CKAN-shaped custom
/// package: deterministic zip + synthetic CKAN metadata + lock artifact reference. Custom packages install
/// through the same CKAN path as everything else, so they get per-file ownership and conflict protection.
/// </summary>
public static class CustomPackageBuilder
{
    public sealed record Built(CustomPackageSource Source, string Version, string ZipPath, string FileName, string Sha256, string Sha1, long Size, long InstallSize, LockedModule Locked, CkanModule Module, IReadOnlyList<string> Warnings);

    public static Built Build(CustomPackageSource src, string packDir, string outputDir, bool allowDirty)
    {
        var warnings = new List<string>();
        var sourceDir = Path.GetFullPath(Path.Combine(packDir, src.SourceDir));
        if (!Directory.Exists(sourceDir))
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"Custom package {src.Id}: source directory {sourceDir} does not exist. Build it first.");
        }
        var version = ResolveVersion(src, sourceDir, packDir, allowDirty, warnings);
        var root = src.InstallAs.Replace('\\', '/').TrimEnd('/');
        var rootName = root[(root.LastIndexOf('/') + 1)..];
        var fileName = $"{rootName}-{version.Replace(':', '_').Replace('+', '_')}.zip";
        var zipPath = Path.Combine(outputDir, fileName);
        var entries = DeterministicZip.Create(sourceDir, root, zipPath, src.Exclude);
        if (entries.Count == 0)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"Custom package {src.Id}: no files to package after exclusions.");
        }
        var bytes = File.ReadAllBytes(zipPath);
        var sha256 = Sha256Util.HashBytes(bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var installSize = entries.Sum(e => e.Size);

        var distribution = src.Distribution.Kind;
        string downloadUrl;
        IReadOnlyList<string> lockUrls;
        switch (distribution)
        {
            case "github-release":
                if (string.IsNullOrWhiteSpace(src.Distribution.Repo))
                {
                    throw new LauncherException(ErrorCode.InvalidPack, $"Custom package {src.Id}: github-release distribution needs a repo.");
                }
                downloadUrl = $"https://github.com/{src.Distribution.Repo}/releases/download/{src.Distribution.TagPrefix}{version}/{fileName}";
                lockUrls = new[] { downloadUrl };
                break;
            case "url":
                downloadUrl = src.Distribution.Url ?? throw new LauncherException(ErrorCode.InvalidPack, $"Custom package {src.Id}: url distribution needs a url.");
                lockUrls = new[] { downloadUrl };
                break;
            case "private-sideload":
            case "private-github":
                // CKAN metadata requires a download URL; .invalid never resolves, and the launcher never tries it
                // because the lock marks the artifact as private. Both private kinds use the same placeholder so
                // switching between them does not change the module metadata (no spurious reinstall).
                downloadUrl = $"https://sideload.invalid/{src.Id}/{fileName}";
                lockUrls = Array.Empty<string>();
                break;
            default:
                throw new LauncherException(ErrorCode.InvalidPack, $"Custom package {src.Id}: unknown distribution kind '{distribution}'.");
        }

        var rel = (string kind, IReadOnlyList<string> names) => names.Count == 0 ? string.Empty : $",\n  \"{kind}\": [{string.Join(", ", names.Select(n => $"{{ \"name\": \"{n}\" }}"))}]";
        var provides = src.Provides.Count == 0 ? string.Empty : $",\n  \"provides\": [{string.Join(", ", src.Provides.Select(p => $"\"{p}\""))}]";
        var json = $$"""
            {
              "spec_version": "v1.34",
              "identifier": "{{src.Id}}",
              "name": {{System.Text.Json.JsonSerializer.Serialize(src.Name ?? src.Id)}},
              "abstract": {{System.Text.Json.JsonSerializer.Serialize(src.Abstract ?? $"Custom package {src.Id}")}},
              "author": "Rjwolfe44",
              "license": {{System.Text.Json.JsonSerializer.Serialize(src.License ?? "restricted")}},
              "version": "{{version}}",
              "ksp_version": "1.12",
              "download": "{{downloadUrl}}",
              "download_size": {{bytes.Length}},
              "download_hash": { "sha1": "{{sha1.ToUpperInvariant()}}", "sha256": "{{sha256.ToUpperInvariant()}}" },
              "download_content_type": "application/zip",
              "install_size": {{installSize}},
              "install": [ { "find": "{{rootName}}", "install_to": "{{root[..root.LastIndexOf('/')]}}" } ]{{rel("conflicts", src.Conflicts)}}{{provides}}
            }
            """;
        // No release_date: the metadata must be a pure function of the package content so that an
        // unchanged custom package is "unchanged" in LockDiff across exports (release_date is optional in CKAN).
        var module = CkanModule.FromJson(json);
        PrivateSourceRef? privateSource = null;
        if (distribution == "private-github")
        {
            if (!Core.Private.InviteCode.IsValidRepo(src.Distribution.Repo))
            {
                throw new LauncherException(ErrorCode.InvalidPack, $"Custom package {src.Id}: private-github distribution needs a repo (owner/name).");
            }
            privateSource = new PrivateSourceRef { Repo = src.Distribution.Repo!, Tag = PrivateTag(src.Distribution.TagPrefix, version), Asset = fileName };
        }
        var artifact = new ArtifactRef
        {
            Sha256 = sha256, Sha1 = sha1, Size = bytes.Length, Urls = lockUrls,
            // Old launchers only know "private-sideload": they show the hand-over prompt and ignore Private.
            Distribution = IsPrivate(distribution) ? "private-sideload" : "download",
            FileName = fileName,
            Private = privateSource,
        };
        var locked = RegistryLockBuilder.FromModule(module, autoInstalled: false, ModuleOrigin.Custom, artifact);
        return new Built(src, version, zipPath, fileName, sha256, sha1, bytes.Length, installSize, locked, module, warnings);
    }

    public static bool IsPrivate(string distributionKind) => distributionKind is "private-sideload" or "private-github";

    /// <summary>Release tag for a private package: prefix + version with anything outside [A-Za-z0-9._-] replaced by '_'.</summary>
    public static string PrivateTag(string? prefix, string version)
        => (prefix ?? string.Empty) + Regex.Replace(version, "[^A-Za-z0-9._-]", "_");

    private static string ResolveVersion(CustomPackageSource src, string sourceDir, string packDir, bool allowDirty, List<string> warnings)
    {
        var v = src.VersionFrom;
        if (v.Fixed is not null)
        {
            return v.Fixed;
        }
        if (v.AssemblyInformationalVersion is not null)
        {
            var dll = Path.Combine(sourceDir, v.AssemblyInformationalVersion.Replace('/', Path.DirectorySeparatorChar));
            var info = LmpClientInspector.InspectDll(dll);
            return info.InformationalVersion ?? info.AssemblyVersion ?? throw new LauncherException(ErrorCode.InvalidPack, $"{dll} has no version information.");
        }
        if (v.GitDescribe is not null)
        {
            var repo = Path.GetFullPath(Path.Combine(packDir, v.GitDescribe));
            var describe = Git(repo, "describe --tags --long --dirty --always");
            // v2.15.0-275-g332cad92[-dirty] -> 2.15.0.275+g332cad92
            var m = Regex.Match(describe, @"^v?(?<tag>\d+(?:\.\d+)*)-(?<n>\d+)-g(?<sha>[0-9a-f]+)(?<dirty>-dirty)?$");
            string version;
            if (m.Success)
            {
                version = $"{m.Groups["tag"].Value}.{m.Groups["n"].Value}+g{m.Groups["sha"].Value}";
            }
            else
            {
                version = "0.0.0+g" + describe.Replace("-dirty", string.Empty, StringComparison.Ordinal);
            }
            if (describe.EndsWith("-dirty", StringComparison.Ordinal))
            {
                if (!allowDirty)
                {
                    throw new LauncherException(ErrorCode.InvalidPack, $"Custom package {src.Id}: the git tree at {repo} has uncommitted changes. Commit them or pass --allow-dirty.");
                }
                var contentHash = ContentHash(sourceDir, src.Exclude)[..8];
                version += $".dirty{contentHash}";
                warnings.Add($"{src.Id}: built from a dirty git tree; version {version} carries a content hash.");
            }
            return version;
        }
        throw new LauncherException(ErrorCode.InvalidPack, $"Custom package {src.Id}: no versionFrom source configured.");
    }

    private static string ContentHash(string dir, IEnumerable<string> exclude)
    {
        var matcher = new GlobMatcher(exclude);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (matcher.IsMatch(rel))
            {
                continue;
            }
            hasher.AppendData(System.Text.Encoding.UTF8.GetBytes(rel + "\0" + Sha256Util.HashFile(f) + "\n"));
        }
        return Sha256Util.ToHex(hasher.GetHashAndReset());
    }

    private static string Git(string repo, string args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo.TrimEnd('\\', '/'));
        foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new LauncherException(ErrorCode.InvalidPack, "git is not available.");
        var stderr = p.StandardError.ReadToEndAsync(); // read both streams concurrently: no pipe deadlock
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"git {args} failed in {repo}: {stderr.GetAwaiter().GetResult().Trim()}");
        }
        return output;
    }
}
