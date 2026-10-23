namespace VmpLauncher.Core.Catalog;

/// <summary>One mod as the Browse page shows it: its newest version for the setup plus that setup's install state.</summary>
public sealed record CatalogEntry
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public string Abstract { get; init; } = string.Empty;
    public string Authors { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    /// <summary>CKAN's download count (0 when unknown).</summary>
    public int Downloads { get; init; }
    public long DownloadSize { get; init; }
    public long InstallSize { get; init; }
    public DateTime? ReleaseDate { get; init; }
    /// <summary>Newest version compatible with the setup's game, or the newest at all when none is.</summary>
    public string LatestVersion { get; init; } = string.Empty;
    public string? InstalledVersion { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsAutoInstalled { get; init; }
    /// <summary>Found in GameData but not installed through CKAN (a manual install).</summary>
    public bool IsAutodetected { get; init; }
    public bool HasUpdate { get; init; }
    public bool IsCompatible { get; init; }
    /// <summary>Game versions this mod supports, e.g. "KSP 1.8–1.12".</summary>
    public string GameVersions { get; init; } = string.Empty;
    public bool IsCached { get; init; }
    /// <summary>package, metapackage or dlc.</summary>
    public string Kind { get; init; } = "package";
    public string? License { get; init; }
    public Uri? SpaceDock { get; init; }
    public Uri? Homepage { get; init; }
    public Uri? Repository { get; init; }
    public Uri? Forum { get; init; }
    public Uri? BugTracker { get; init; }
    /// <summary>Pinned by the pack this setup follows (pack setups only).</summary>
    public bool IsInPack { get; init; }
    /// <summary>Appeared in the last catalog refresh.</summary>
    public bool IsNew { get; init; }
}

/// <summary>The whole catalog for one setup.</summary>
public sealed record CatalogSnapshot(
    IReadOnlyList<CatalogEntry> Entries,
    IReadOnlyList<string> Tags,
    string GameVersion,
    DateTimeOffset? CatalogUpdatedUtc,
    bool ReadOnly,
    string? ReadOnlyReason);

/// <summary>Everything a mod page shows beyond its catalog entry.</summary>
public sealed record ModDetails(
    CatalogEntry Entry,
    string? Description,
    IReadOnlyList<ModVersionRow> Versions,
    IReadOnlyList<RelationRow> Relations);

public sealed record ModVersionRow(string Version, string GameVersions, DateTime? ReleaseDate, long DownloadSize, bool IsCompatible, bool IsInstalled, string ReleaseStatus);

/// <param name="Kind">depends, recommends, suggests, supports or conflicts.</param>
/// <param name="Identifier">The mod to open for this relation, when it names exactly one.</param>
public sealed record RelationRow(string Kind, string Text, string? Identifier, bool Satisfied);

/// <summary>Display helpers shared by the Browse page and the CLI.</summary>
public static class CatalogFormat
{
    /// <summary>"3:v0.0878" → "v0.0878": CKAN's epoch prefix only orders versions and means nothing to players.</summary>
    public static string DisplayVersion(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return string.Empty;
        }
        var colon = version.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && version[..colon].All(char.IsAsciiDigit) ? version[(colon + 1)..] : version;
    }
}
