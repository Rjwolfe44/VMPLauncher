using System.Diagnostics;
using System.Text;
using VmpLauncher.Ckan.Locking;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Game;

namespace VmpLauncher.Publishing;

/// <summary>
/// Host-side export: capture → custom packages → lock+inventory → sanitized settings template → secret scan →
/// signatures → channel pointer. Writes only under the output directory; never publishes.
/// </summary>
public sealed class PackExporter
{
    public sealed record Request(
        string PackFile,
        string HostKspDir,
        string HostCacheDir,
        string OutputRoot,
        LauncherPaths Paths,
        bool AcceptOverrides,
        bool AllowDirty,
        string? PolicyFile,
        string? PrivateConnectionFile,
        PublisherKey? SigningKey,
        string? VersionOverride);

    public sealed record Report(string VersionDir, string LockPath, IReadOnlyList<string> ReleaseAssets, IReadOnlyList<string> SideloadAssets, HostCapture.Result Capture, IReadOnlyList<string> Warnings, TimeSpan Elapsed);

    private readonly TextWriter _log;

    public PackExporter(TextWriter log)
    {
        _log = log;
    }

    public Report Export(Request r, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var pack = LauncherJson.ReadFile<PackDefinition>(r.PackFile);
        if (r.VersionOverride is not null)
        {
            pack = pack with { Version = r.VersionOverride };
        }
        var packDir = Path.GetDirectoryName(Path.GetFullPath(r.PackFile))!;
        var versionDir = Path.Combine(r.OutputRoot, pack.Id, pack.Version);
        var assetsDir = Path.Combine(versionDir, "release-assets");
        var sideloadDir = Path.Combine(versionDir, "private-sideload");
        Directory.CreateDirectory(versionDir);

        // 1. Classify the host install.
        var capture = new HostCapture(_log).Run(r.HostKspDir, r.HostCacheDir, pack, packDir, r.Paths, r.AcceptOverrides, ct);
        if (capture.Errors.Count > 0)
        {
            throw new LauncherException(ErrorCode.InvalidPack, "The host install cannot be exported as-is.", RecoveryHint.None, details: capture.Errors);
        }

        // 2. Custom packages.
        var customModules = new List<LockedModule>();
        var customInventory = new List<InventoryEntry>();
        var releaseAssets = new List<string>();
        var sideloadAssets = new List<string>();
        string? requiredClient = null;
        var game = new CKAN.Games.KerbalSpaceProgram.KerbalSpaceProgram();
        var indexer = new ArchiveContentIndexer(Path.Combine(r.Paths.DataRoot, "capture-cache.json"), game);
        foreach (var src in pack.CustomPackages)
        {
            ct.ThrowIfCancellationRequested();
            var isSideload = src.Distribution.Kind == "private-sideload";
            var built = CustomPackageBuilder.Build(src, packDir, isSideload ? sideloadDir : assetsDir, r.AllowDirty || isSideload);
            warnings.AddRange(built.Warnings);
            _log.WriteLine($"Custom package {built.Source.Id} {built.Version}: {built.FileName} ({built.Size:N0} bytes, {built.InstallSize:N0} installed){(isSideload ? " [private sideload]" : "")}");
            customModules.Add(built.Locked);
            foreach (var ef in indexer.Index(built.Module, built.ZipPath, built.Locked.MetadataSha256, ct))
            {
                customInventory.Add(new InventoryEntry { Path = ef.Path, Size = ef.Size, Sha256 = ef.Sha256, Owner = built.Source.Id, Class = FileClass.Custom });
            }
            (isSideload ? sideloadAssets : releaseAssets).Add(built.ZipPath);
            if (src.InstallAs.EndsWith("VladMultiplayer", StringComparison.OrdinalIgnoreCase))
            {
                requiredClient = built.Version;
                var installed = LmpClientInspector.Inspect(r.HostKspDir);
                if (installed?.ExactBuild is not null && installed.ExactBuild != built.Version)
                {
                    warnings.Add($"The VMP client installed on the host ({installed.ExactBuild}) differs from the packaged build ({built.Version}).");
                }
            }
        }
        indexer.Save();

        // 3. Policy file.
        string? policySha = null;
        if (r.PolicyFile is not null && File.Exists(r.PolicyFile))
        {
            var dst = Path.Combine(versionDir, "KSPModControl.xml");
            File.Copy(r.PolicyFile, dst, overwrite: true);
            policySha = Sha256Util.HashFile(dst, ct);
        }

        // 4. Lock + inventory. The pack definition shipped with the version carries the version actually built.
        var packCopy = Path.Combine(versionDir, Path.GetFileName(r.PackFile));
        if (r.VersionOverride is not null)
        {
            LauncherJson.WriteFile(packCopy, pack);
        }
        else
        {
            File.Copy(r.PackFile, packCopy, overwrite: true);
        }
        var packSha = Sha256Util.HashFile(packCopy, ct);
        var gen = new LockGenerator(_log).Generate(new LockGenerator.Request(r.HostKspDir, r.HostCacheDir, pack, versionDir, r.Paths, VerifyHostHashes: false, packSha,
            new LockGenerator.Extras(customModules, customInventory, capture.Overrides, requiredClient, policySha)), ct);
        warnings.AddRange(gen.Warnings);

        // 5. Sanitized client settings template (allow-list built, never copied from the real file).
        WriteSettingsTemplate(Path.Combine(versionDir, "client-settings-template.xml"), pack);

        // 6. Secret scan over everything we are about to hand out.
        var scanner = new SecretScanner();
        scanner.LoadFromHost(VmpSettingsFile.PathFor(r.HostKspDir), r.PrivateConnectionFile);
        var findings = scanner.ScanDirectory(versionDir);
        if (findings.Count > 0)
        {
            throw new LauncherException(ErrorCode.Tampered, "Secret scan failed: the export contains sensitive data and was NOT completed.", RecoveryHint.None,
                details: findings.Select(f => $"{Path.GetRelativePath(versionDir, f.File)}{(f.Entry is null ? "" : "!" + f.Entry)}: {f.What}").ToList());
        }
        _log.WriteLine($"Secret scan: clean ({scanner.NeedleCount} needles).");

        // 7. Signatures + channel pointer.
        if (r.SigningKey is not null)
        {
            r.SigningKey.SignFile(gen.LockPath);
            var channel = new { schema = "vmpack-channel/1", packId = pack.Id, channels = new { stable = new { version = pack.Version, @lock = $"{pack.Version}/{Path.GetFileName(gen.LockPath)}", sha256 = Sha256Util.HashFile(gen.LockPath, ct) } } };
            var channelPath = Path.Combine(r.OutputRoot, pack.Id, "channel.json");
            LauncherJson.WriteFile(channelPath, channel);
            r.SigningKey.SignFile(channelPath);
        }
        else
        {
            warnings.Add("No signing key: lock and channel are unsigned (friends will see 'Unverified pack').");
        }

        // 8. Capture report.
        var report = new StringBuilder();
        report.AppendLine($"# Capture report {pack.Id} {pack.Version}");
        foreach (var c in Enum.GetValues<HostCapture.Class>())
        {
            var n = capture.Count(c);
            if (n > 0)
            {
                report.AppendLine($"- {c}: {n}");
            }
        }
        foreach (var row in capture.Rows.Where(x => x.Class is HostCapture.Class.OverrideReplace or HostCapture.Class.OverrideDisable or HostCapture.Class.OverrideAdd or HostCapture.Class.Unknown or HostCapture.Class.RuntimeMutated))
        {
            report.AppendLine($"  {row.Class,-16} {row.Path} {(row.Owner is null ? "" : $"({row.Owner})")} {row.Detail}");
        }
        foreach (var w in warnings)
        {
            report.AppendLine($"warning: {w}");
        }
        File.WriteAllText(Path.Combine(versionDir, "capture-report.md"), report.ToString());
        return new Report(versionDir, gen.LockPath, releaseAssets, sideloadAssets, capture, warnings, sw.Elapsed);
    }

    private static void WriteSettingsTemplate(string path, PackDefinition pack)
    {
        var doc = VmpSettingsFile.CreateMinimal();
        foreach (var s in pack.Multiplayer.Servers.Where(s => s.Exportable))
        {
            VmpSettingsFile.UpsertServer(doc, new VmpSettingsFile.ServerEntry(s.Name, s.Address, s.Port, string.Empty));
        }
        VmpSettingsFile.Save(doc, path);
    }
}
