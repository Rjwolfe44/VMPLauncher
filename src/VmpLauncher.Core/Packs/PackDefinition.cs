namespace VmpLauncher.Core.Packs;

/// <summary>The host-edited pack definition (<c>*.vmpack.json</c>). Small, human-readable, never contains secrets.</summary>
public sealed record PackDefinition
{
    public string Schema { get; init; } = "vmpack/1";
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Author { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public GameRequirements Game { get; init; } = new();
    public CkanSelection Ckan { get; init; } = new();
    public IReadOnlyList<CustomPackageSource> CustomPackages { get; init; } = Array.Empty<CustomPackageSource>();
    public IReadOnlyList<OverrideSource> Overrides { get; init; } = Array.Empty<OverrideSource>();
    public FileRules FileRules { get; init; } = FileRules.Default;
    public MultiplayerDefinition Multiplayer { get; init; } = new();
    public PackLinks Links { get; init; } = new();
}

public sealed record GameRequirements
{
    public string KspVersion { get; init; } = "1.12.5";
    public string BuildId64 { get; init; } = "03190";
    public IReadOnlyList<DlcRequirement> RequiredDlc { get; init; } = new[]
    {
        new DlcRequirement("MakingHistory-DLC", "SquadExpansion/MakingHistory", "1.12.1", "MakingHistory"),
        new DlcRequirement("BreakingGround-DLC", "SquadExpansion/Serenity", "1.7.1", "Serenity"),
    };
    public IReadOnlyList<string> CompatibleGameVersions { get; init; } = new[] { "1.12", "1.11", "1.10", "1.9", "1.8", "1.7", "1.6" };
}

/// <param name="Identifier">CKAN identifier, e.g. MakingHistory-DLC.</param>
/// <param name="Folder">GameData-relative folder.</param>
/// <param name="Version">Required DLC version.</param>
/// <param name="PolicyName">Name used by KSPModControl.xml RequiredExpansions.</param>
public sealed record DlcRequirement(string Identifier, string Folder, string Version, string PolicyName);

public sealed record CkanSelection
{
    /// <summary>"registry": capture everything installed in the host registry; "explicit": only <see cref="Modules"/>.</summary>
    public string Source { get; init; } = "registry";
    public IReadOnlyList<ModulePin> Modules { get; init; } = Array.Empty<ModulePin>();
    public IReadOnlyList<string> Metapackages { get; init; } = Array.Empty<string>();
    public bool WithRecommends { get; init; }
}

public sealed record ModulePin(string Id, string Version);

public sealed record CustomPackageSource
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Abstract { get; init; }
    public string? License { get; init; }
    /// <summary>Directory holding the package content (the folder that becomes GameData/&lt;Root&gt;).</summary>
    public required string SourceDir { get; init; }
    /// <summary>Destination, e.g. GameData/VladMultiplayer.</summary>
    public required string InstallAs { get; init; }
    public VersionSource VersionFrom { get; init; } = new();
    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Provides { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public DistributionSpec Distribution { get; init; } = new();
}

public sealed record VersionSource
{
    /// <summary>Relative path (inside SourceDir) of a .NET assembly whose informational version is the package version.</summary>
    public string? AssemblyInformationalVersion { get; init; }
    /// <summary>Path to a git working tree; version from `git describe --tags --long`.</summary>
    public string? GitDescribe { get; init; }
    /// <summary>Fixed version string.</summary>
    public string? Fixed { get; init; }
}

public sealed record DistributionSpec
{
    /// <summary>"github-release" | "private-sideload" | "url".</summary>
    public string Kind { get; init; } = "github-release";
    public string? Repo { get; init; }
    public string? TagPrefix { get; init; }
    public string? Url { get; init; }
    public string? Hint { get; init; }
}

public sealed record OverrideSource
{
    /// <summary>"add" | "replace" | "disable".</summary>
    public required string Op { get; init; }
    public required string Path { get; init; }
    public string? Owner { get; init; }
    public string? RenameTo { get; init; }
    /// <summary>"capture" reads the content from the host install; otherwise a file path.</summary>
    public string Source { get; init; } = "capture";
}

public sealed record FileRules
{
    public IReadOnlyList<string> Generated { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PerPlayer { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MutableOwned { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Stock { get; init; } = Array.Empty<string>();

    public static FileRules Default { get; } = new()
    {
        Generated = new[]
        {
            "GameData/ModuleManager.ConfigCache", "GameData/ModuleManager.ConfigSHA", "GameData/ModuleManager.Physics",
            "GameData/ModuleManager.TechTree", "GameData/toolbar-settings.dat", "GameData/**/*.log", "GameData/**/*.tmp",
            "GameData/ScattererAtmosphereCache/**", "GameData/**/ScattererAtmosphereCache/**",
            // Written by SigmaReplacements on first run.
            "GameData/Sigma/Replacements/Settings.cfg",
            // Runtime caches and per-install state written by mods (never shipped, never verified).
            "GameData/**/Cache/**", "GameData/**/*.hash", "GameData/**/*.TechTree",
            "GameData/Kopernicus/Config/Kopernicus_Config.cfg", "GameData/Astrogator/AstrogatorSettings.cfg",
            "GameData/KerbalKonstructs/PluginDataWindowPositions.cfg", "GameData/KSP-AVC/Plugins/KSP-AVC.xml",
        },
        PerPlayer = new[] { "GameData/VladMultiplayer/Data/**", "GameData/VladMultiplayer/Traces/**", "GameData/VladMultiplayer/logs/**" },
        MutableOwned = new[]
        {
            "GameData/**/PluginData/**",
            // Known mods that rewrite their own settings files at runtime (found by capturing the host install).
            "GameData/KerbalEngineer/Settings/**",
            "GameData/AtmosphereAutopilot/Global_settings.txt",
        },
        Stock = new[] { "GameData/Squad/**", "GameData/SquadExpansion/**" },
    };
}

public sealed record MultiplayerDefinition
{
    /// <summary>"auto" = read from the VMP client package at lock time.</summary>
    public string RequiredClientVersion { get; init; } = "auto";
    public IReadOnlyList<ServerPreset> Servers { get; init; } = Array.Empty<ServerPreset>();
    public string? PolicyUrl { get; init; }
    public string? ServerDirectoryUrl { get; init; }
}

/// <param name="Exportable">False keeps the preset on the host only (e.g. LAN addresses).</param>
public sealed record ServerPreset(string Name, string Address, int Port, bool Exportable = true);

public sealed record PackLinks
{
    public string? Changelog { get; init; }
    public string? Homepage { get; init; }
    public string? Banner { get; init; }
}
