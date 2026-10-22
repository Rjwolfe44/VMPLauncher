using System.Text.Json;
using VmpLauncher.Core.Packs;

namespace VmpLauncher.Core.Locking;

/// <summary>
/// The resolved, signed lock (<c>*.vmpack.lock.json</c>). This is the only thing a client installs from.
/// Everything needed to reproduce the install is pinned here by exact version and content hash.
/// </summary>
public sealed record PackLock
{
    public string Schema { get; init; } = "vmpack-lock/1";
    public required LockPackInfo Pack { get; init; }
    public required LockGenerationInfo Generated { get; init; }
    public required LockGameInfo Game { get; init; }
    public required LockCkanSection Ckan { get; init; }
    public IReadOnlyList<LockOverride> Overrides { get; init; } = Array.Empty<LockOverride>();
    public required InventoryRef Inventory { get; init; }
    public FileRules FileRules { get; init; } = FileRules.Default;
    public LockMultiplayer Multiplayer { get; init; } = new();
}

public sealed record LockPackInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    /// <summary>SHA-256 of the pack definition file this lock was generated from.</summary>
    public string? PackSha256 { get; init; }
    public PackLinks Links { get; init; } = new();
}

public sealed record LockGenerationInfo
{
    public required DateTimeOffset Utc { get; init; }
    public required string Tool { get; init; }
    public required string Ckan { get; init; }
    public string? HostCkan { get; init; }
    public string? FromRegistrySha256 { get; init; }
}

public sealed record LockGameInfo
{
    public required string KspVersion { get; init; }
    public required string BuildId64 { get; init; }
    public IReadOnlyList<DlcRequirement> RequiredDlc { get; init; } = Array.Empty<DlcRequirement>();
    public IReadOnlyList<string> CompatibleGameVersions { get; init; } = Array.Empty<string>();
}

public sealed record LockCkanSection
{
    public ResolverOptions ResolverOptions { get; init; } = new();
    public IReadOnlyList<LockedModule> Modules { get; init; } = Array.Empty<LockedModule>();
    public IReadOnlyList<LockedDlc> Dlc { get; init; } = Array.Empty<LockedDlc>();
    /// <summary>Modules whose install stanzas write directly into the game root (shown to the user).</summary>
    public IReadOnlyList<string> RootInstallOwners { get; init; } = Array.Empty<string>();
}

public sealed record ResolverOptions
{
    public bool WithRecommends { get; init; }
    public bool WithSuggests { get; init; }
    public bool AllowIncompatible { get; init; } = true;
}

public enum ModuleKindDto
{
    Package,
    Metapackage,
    Dlc,
}

public enum ModuleOrigin
{
    /// <summary>Metadata captured from the CKAN-meta index via the host registry.</summary>
    CkanMeta,
    /// <summary>Synthetic metadata generated for a custom package (VMP, ApexGuidance).</summary>
    Custom,
}

public sealed record LockedModule
{
    public required string Identifier { get; init; }
    public required string Version { get; init; }
    public ModuleKindDto Kind { get; init; } = ModuleKindDto.Package;
    public ModuleOrigin Origin { get; init; } = ModuleOrigin.CkanMeta;
    public bool AutoInstalled { get; init; }
    /// <summary>SHA-256 of the JCS-canonical form of <see cref="Metadata"/>.</summary>
    public required string MetadataSha256 { get; init; }
    /// <summary>Verbatim CKAN module JSON (what the CKAN library will consume).</summary>
    public required JsonElement Metadata { get; init; }
    /// <summary>Null for metapackages.</summary>
    public ArtifactRef? Artifact { get; init; }
}

public sealed record LockedDlc(string Identifier, string Version);

/// <summary>Where an archive comes from and how to prove it is the right bytes.</summary>
public sealed record ArtifactRef
{
    public required string Sha256 { get; init; }
    public string? Sha1 { get; init; }
    public required long Size { get; init; }
    public IReadOnlyList<string> Urls { get; init; } = Array.Empty<string>();
    /// <summary>"download" (default) or "private-sideload" (the host hands the file over privately).</summary>
    public string Distribution { get; init; } = "download";
    /// <summary>Suggested file name, mainly for sideload prompts.</summary>
    public string? FileName { get; init; }
}

public sealed record LockOverride
{
    public required string Id { get; init; }
    /// <summary>"add" | "replace" | "disable".</summary>
    public required string Op { get; init; }
    public required string Path { get; init; }
    public string? Owner { get; init; }
    /// <summary>Hash the owned file must have before a replace/disable applies.</summary>
    public string? BaseSha256 { get; init; }
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
    public string? RenameTo { get; init; }
    public OverrideContent? Content { get; init; }
}

public sealed record OverrideContent
{
    /// <summary>Inline content for tiny files (≤ 1 KiB).</summary>
    public string? InlineBase64 { get; init; }
    /// <summary>Otherwise, an archive plus entry name.</summary>
    public ArtifactRef? Artifact { get; init; }
    public string? Entry { get; init; }
}

public sealed record InventoryRef
{
    public string File { get; init; } = "inventory.json.gz";
    public required string Sha256 { get; init; }
    public required int FileCount { get; init; }
    public required long TotalBytes { get; init; }
    public required int MaxRelPathLength { get; init; }
}

public sealed record LockMultiplayer
{
    public string? RequiredClientVersion { get; init; }
    public IReadOnlyList<ServerPreset> Servers { get; init; } = Array.Empty<ServerPreset>();
    public PolicyRef? Policy { get; init; }
    public string? ServerDirectoryUrl { get; init; }
}

public sealed record PolicyRef(string Url, string? Sha256AtLockTime);
