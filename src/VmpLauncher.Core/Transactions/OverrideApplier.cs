using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Security;

namespace VmpLauncher.Core.Transactions;

/// <summary>
/// Applies the lock's deliberate deviations from CKAN content: <c>add</c> a file no module owns,
/// <c>replace</c> an owned file (base hash checked), <c>disable</c> an owned file by renaming it.
/// Idempotent, so it can run after every CKAN apply.
/// </summary>
public static class OverrideApplier
{
    public delegate string ResolveArtifact(ArtifactRef artifact);

    public static IReadOnlyList<string> Apply(ProfilePaths profile, IReadOnlyList<LockOverride> overrides, ResolveArtifact resolveArtifact)
    {
        var notes = new List<string>();
        foreach (var o in overrides)
        {
            var abs = SafePath.Resolve(profile.GameDir, o.Path);
            switch (o.Op)
            {
                case "add":
                case "replace":
                {
                    if (o.Op == "replace")
                    {
                        if (!File.Exists(abs))
                        {
                            throw new LauncherException(ErrorCode.InstallFailed, $"Override {o.Id} replaces {o.Path} but the file is not installed.", RecoveryHint.Repair);
                        }
                        var current = Sha256Util.HashFile(abs);
                        if (Sha256Util.HexEquals(current, o.Sha256))
                        {
                            notes.Add($"{o.Id}: already applied");
                            continue;
                        }
                        if (!Sha256Util.HexEquals(current, o.BaseSha256))
                        {
                            throw new LauncherException(ErrorCode.InstallFailed, $"Override {o.Id}: {o.Path} is neither the original nor the override content.", RecoveryHint.Repair,
                                details: new[] { $"found {current}", $"expected base {o.BaseSha256}" });
                        }
                    }
                    else if (File.Exists(abs) && Sha256Util.HexEquals(Sha256Util.HashFile(abs), o.Sha256))
                    {
                        notes.Add($"{o.Id}: already applied");
                        continue;
                    }
                    WriteContent(o, abs, resolveArtifact);
                    notes.Add($"{o.Id}: {o.Op} {o.Path}");
                    break;
                }
                case "disable":
                {
                    var renamed = SafePath.Resolve(Path.GetDirectoryName(abs)!, o.RenameTo!);
                    if (!File.Exists(abs))
                    {
                        if (File.Exists(renamed))
                        {
                            notes.Add($"{o.Id}: already disabled");
                            continue;
                        }
                        throw new LauncherException(ErrorCode.InstallFailed, $"Override {o.Id} disables {o.Path} but the file is missing.", RecoveryHint.Repair);
                    }
                    var current = Sha256Util.HashFile(abs);
                    if (!Sha256Util.HexEquals(current, o.BaseSha256))
                    {
                        throw new LauncherException(ErrorCode.InstallFailed, $"Override {o.Id}: {o.Path} does not have the expected content to disable.", RecoveryHint.Repair);
                    }
                    File.Move(abs, renamed, overwrite: true);
                    notes.Add($"{o.Id}: disabled {o.Path} -> {o.RenameTo}");
                    break;
                }
                default:
                    throw new LauncherException(ErrorCode.InvalidLock, $"Unknown override op '{o.Op}' ({o.Id}).");
            }
        }
        return notes;
    }

    /// <summary>Undo the overrides touching modules about to be re-installed, so CKAN sees its own files.</summary>
    public static void Unapply(ProfilePaths profile, IEnumerable<LockOverride> overrides)
    {
        foreach (var o in overrides)
        {
            var abs = SafePath.Resolve(profile.GameDir, o.Path);
            switch (o.Op)
            {
                case "add":
                    if (File.Exists(abs))
                    {
                        File.Delete(abs);
                    }
                    break;
                case "disable":
                {
                    var renamed = SafePath.Resolve(Path.GetDirectoryName(abs)!, o.RenameTo!);
                    if (File.Exists(renamed))
                    {
                        File.Delete(renamed);
                    }
                    break;
                }
                case "replace":
                    // CKAN's uninstall removes the file (it owns the path); nothing to do.
                    break;
            }
        }
    }

    private static void WriteContent(LockOverride o, string destination, ResolveArtifact resolveArtifact)
    {
        var content = o.Content ?? throw new LauncherException(ErrorCode.InvalidLock, $"Override {o.Id} has no content.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (content.InlineBase64 is not null)
        {
            var bytes = Convert.FromBase64String(content.InlineBase64);
            if (!Sha256Util.HexEquals(Sha256Util.HashBytes(bytes), o.Sha256))
            {
                throw new LauncherException(ErrorCode.Tampered, $"Override {o.Id} inline content does not match its hash.");
            }
            var tmp = destination + ".vmpl-tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, destination, overwrite: true);
            return;
        }
        if (content.Artifact is null || content.Entry is null)
        {
            throw new LauncherException(ErrorCode.InvalidLock, $"Override {o.Id} content has neither inline data nor an artifact entry.");
        }
        var zip = resolveArtifact(content.Artifact);
        SafeZip.ExtractEntryVerified(zip, content.Entry, destination, o.Sha256!, o.Size ?? -1);
    }
}
