using System.Text.Json;
using System.Text.RegularExpressions;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Net.Library;

namespace VmpLauncher.Publishing.Library;

/// <summary>
/// The host-edited definition of Vlad's mod library (<c>library\vlad.library.json</c>, schema <c>vmplib/1</c>): which mods are in
/// it and how the "Vlad's mods" tab presents them. The publisher turns it into the signed <c>library.json</c>; paths inside it
/// are relative to the definition file's own folder (the same rule a pack definition's custom packages follow).
/// </summary>
public sealed partial record LibraryDefinition
{
    public const string CurrentSchema = "vmplib/1";

    /// <summary>The only distribution a library mod may have: a public release asset in one of Vlad's own repositories.</summary>
    public const string AllowedDistributionKind = "github-release";

    private const long MaxDefinitionBytes = 1024 * 1024;

    public string Schema { get; init; } = CurrentSchema;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Tagline { get; init; } = string.Empty;
    public IReadOnlyList<LibraryModDefinition> Mods { get; init; } = Array.Empty<LibraryModDefinition>();

    /// <summary>The default definition: <c>library\vlad.library.json</c> in the first folder at or above <paramref name="startFile"/> that has one.</summary>
    public static string? FindNextTo(string startFile)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(startFile))!); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "library", "vlad.library.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Reads and validates a definition file; every problem is reported at once, in plain words.</summary>
    public static LibraryDefinition Load(string path)
    {
        LibraryDefinition definition;
        try
        {
            definition = LauncherJson.ReadFile<LibraryDefinition>(path, MaxDefinitionBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The library definition {path} could not be read: {ex.Message}", RecoveryHint.None, ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The library definition {path} is not valid: {ex.Message}", RecoveryHint.None, ex);
        }
        definition.Validate(path);
        return definition;
    }

    /// <summary>Checks everything the publisher can say about a definition before it builds a single zip.</summary>
    public void Validate(string source)
    {
        var problems = new List<string>();
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
        {
            problems.Add($"schema '{Schema}' is not supported (expected '{CurrentSchema}')");
        }
        if (!string.Equals(Id, LibraryIndex.VladLibraryId, StringComparison.Ordinal))
        {
            problems.Add($"id must be '{LibraryIndex.VladLibraryId}' (the launcher only accepts that library), not '{Id}'");
        }
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
        {
            problems.Add("name must be 1 to 80 characters");
        }
        if (Tagline.Length > 200)
        {
            problems.Add("tagline must be at most 200 characters");
        }
        if (Mods.Count == 0)
        {
            problems.Add("mods is empty: a library needs at least one mod");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in Mods)
        {
            var package = mod.Package;
            var label = string.IsNullOrWhiteSpace(package.Id) ? "(a mod without an id)" : package.Id;
            if (!seen.Add(package.Id))
            {
                problems.Add($"{label} is listed twice");
            }
            CheckDistribution(label, package.Distribution, problems);
            foreach (var relation in mod.Depends.Concat(mod.Recommends))
            {
                if (string.IsNullOrWhiteSpace(relation.Name))
                {
                    problems.Add($"{label}: a depends/recommends entry has no name");
                }
            }
            if (mod.Showcase is { } showcase && !AccentPattern().IsMatch(showcase.Accent))
            {
                problems.Add($"{label}: showcase.accent must look like #7C5CFF");
            }
        }
        if (problems.Count > 0)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The library definition {source} has problems: {string.Join("; ", problems.Take(6))}.",
                RecoveryHint.None, details: problems);
        }
    }

    private static void CheckDistribution(string label, DistributionSpec distribution, List<string> problems)
    {
        if (!string.Equals(distribution.Kind, AllowedDistributionKind, StringComparison.Ordinal))
        {
            var kind = distribution.Kind;
            problems.Add(kind is "private-github" or "private-sideload"
                ? $"{label}: distribution kind '{kind}' is private, and a private mod must never be listed in a public library. Only '{AllowedDistributionKind}' is allowed"
                : $"{label}: distribution kind '{kind}' is not allowed in the mod library. Only '{AllowedDistributionKind}' is (a public release asset of one of Vlad's repositories)");
            return;
        }
        if (string.IsNullOrWhiteSpace(distribution.Repo) || !RepoPattern().IsMatch(distribution.Repo)
            || !$"https://github.com/{distribution.Repo}/".StartsWith(LibraryIndex.AllowedDownloadPrefix, StringComparison.Ordinal))
        {
            problems.Add($"{label}: distribution.repo must be one of Vlad's repositories (Rjwolfe44/<name>), the launcher refuses downloads from anywhere else; it is '{distribution.Repo}'");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccentPattern();
}

/// <summary>One mod of the library: the package the existing builder zips, plus the CKAN fields and showcase text around it.</summary>
public sealed record LibraryModDefinition
{
    /// <summary>Same shape as a pack's custom package; only the <c>github-release</c> distribution is accepted.</summary>
    public required CustomPackageSource Package { get; init; }

    /// <summary>Long text for the mod's page (CKAN <c>description</c>).</summary>
    public string? Description { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>CKAN resources: homepage, repository, bugtracker, ... Every value must be an https link.</summary>
    public IReadOnlyDictionary<string, string> Resources { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<LibraryRelation> Depends { get; init; } = Array.Empty<LibraryRelation>();
    public IReadOnlyList<LibraryRelation> Recommends { get; init; } = Array.Empty<LibraryRelation>();

    /// <summary>When either is set they replace the builder's fixed <c>ksp_version</c> ("1.12").</summary>
    public string? KspVersionMin { get; init; }
    public string? KspVersionMax { get; init; }

    public LibraryShowcaseDefinition? Showcase { get; init; }
}

/// <summary>A CKAN relationship: <c>{ "name": "Harmony2" }</c>, optionally with version bounds.</summary>
public sealed record LibraryRelation
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? MinVersion { get; init; }
    public string? MaxVersion { get; init; }
}

/// <summary>How the "Vlad's mods" tab presents a mod (the <c>showcase</c> entry of <c>library.json</c>).</summary>
public sealed record LibraryShowcaseDefinition
{
    public string Tagline { get; init; } = string.Empty;
    public IReadOnlyList<string> Highlights { get; init; } = Array.Empty<string>();
    public string Accent { get; init; } = "#7C5CFF";
    public int Order { get; init; } = 100;

    /// <summary>A picture in one of Vlad's GitHub repositories (checked by the launcher's strict parser).</summary>
    public string? Banner { get; init; }
}
