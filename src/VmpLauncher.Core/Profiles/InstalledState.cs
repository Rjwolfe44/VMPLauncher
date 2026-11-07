using System.Text.Json.Serialization;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Profiles;

/// <summary>state\installed.json: what the launcher last verified in this profile, with quick-check fingerprints.</summary>
public sealed record InstalledState
{
    public required string LockSha256 { get; init; }
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public required DateTimeOffset AppliedUtc { get; init; }
    public required IReadOnlyList<InstalledFileRecord> Files { get; init; }

    /// <summary>When every pack file was last hashed (install, repair, or an update's full check). Null in older state files.</summary>
    public DateTimeOffset? LastFullCheckUtc { get; init; }

    /// <summary>Updates committed with the incremental check (only changed files hashed) since <see cref="LastFullCheckUtc"/>.</summary>
    public int UpdatesSinceFullCheck { get; init; }

    /// <summary>An update hashes every file again after this long, so bit rot or an in-place edit that kept size, time and file id is still found.</summary>
    public static readonly TimeSpan FullCheckMaxAge = TimeSpan.FromDays(30);

    /// <summary>... or after this many incremental updates, whichever comes first.</summary>
    public const int FullCheckEveryUpdates = 10;

    /// <summary>True when the next update must hash every file instead of only the changed ones.</summary>
    public bool NeedsFullCheck(DateTimeOffset now)
        => LastFullCheckUtc is not { } last || now - last > FullCheckMaxAge || now < last || UpdatesSinceFullCheck >= FullCheckEveryUpdates;

    public static InstalledState? TryRead(ProfilePaths profile)
        => File.Exists(profile.InstalledStateFile) ? LauncherJson.ReadFile<InstalledState>(profile.InstalledStateFile, 256L * 1024 * 1024) : null;

    public void Write(ProfilePaths profile) => LauncherJson.WriteFile(profile.InstalledStateFile, this);
}

/// <summary>p=path, s=size, m=last-write ticks (UTC), f=NTFS file id, h=sha256, o=owner.</summary>
public sealed record InstalledFileRecord
{
    [JsonPropertyName("p")] public required string Path { get; init; }
    [JsonPropertyName("s")] public required long Size { get; init; }
    [JsonPropertyName("m")] public required long LastWriteTicks { get; init; }
    [JsonPropertyName("f")] public string? FileId { get; init; }
    [JsonPropertyName("h")] public required string Sha256 { get; init; }
    [JsonPropertyName("o")] public required string Owner { get; init; }
}
