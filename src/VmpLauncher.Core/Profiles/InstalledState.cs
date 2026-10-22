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
