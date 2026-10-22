using System.Text.Json;
using CKAN;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;

namespace VmpLauncher.Ckan.Locking;

/// <summary>Turns the host registry's installed modules into <see cref="LockedModule"/> records (verbatim metadata).</summary>
public static class RegistryLockBuilder
{
    public static LockedModule FromInstalled(InstalledModule im) => FromModule(im.Module, im.AutoInstalled, ModuleOrigin.CkanMeta);

    public static LockedModule FromModule(CkanModule m, bool autoInstalled, ModuleOrigin origin, ArtifactRef? artifactOverride = null)
    {
        var json = m.ToJson();
        var metadata = JsonDocument.Parse(json).RootElement.Clone();
        var kind = m.IsDLC ? ModuleKindDto.Dlc : m.IsMetapackage ? ModuleKindDto.Metapackage : ModuleKindDto.Package;
        ArtifactRef? artifact = artifactOverride;
        if (artifact is null && kind == ModuleKindDto.Package)
        {
            if (m.download_hash?.sha256 is null)
            {
                throw new InvalidDataException($"{m.identifier} {m.version} has no sha256 in its metadata; cannot lock it.");
            }
            artifact = new ArtifactRef
            {
                Sha256 = m.download_hash.sha256.ToLowerInvariant(),
                Sha1 = m.download_hash.sha1?.ToLowerInvariant(),
                Size = m.download_size,
                Urls = (m.download ?? new List<Uri>()).Select(u => u.ToString()).ToList(),
            };
        }
        return new LockedModule
        {
            Identifier = m.identifier,
            Version = m.version.ToString(),
            Kind = kind,
            Origin = origin,
            AutoInstalled = autoInstalled,
            MetadataSha256 = JsonCanonicalizer.Sha256(json),
            Metadata = metadata,
            Artifact = artifact,
        };
    }

    /// <summary>Rebuilds the CKAN module object from a lock entry (what the pack repository is generated from).</summary>
    public static CkanModule ToCkanModule(LockedModule locked)
    {
        var module = CkanModule.FromJson(locked.Metadata.GetRawText());
        var actual = JsonCanonicalizer.Sha256(module.ToJson());
        if (!Sha256Util.HexEquals(actual, locked.MetadataSha256))
        {
            throw new InvalidDataException($"Lock metadata for {locked.Identifier} does not match its recorded hash.");
        }
        return module;
    }
}
