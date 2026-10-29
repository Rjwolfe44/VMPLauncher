using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Profiles;

/// <summary>What a profile (shown to players as a "setup") is for.</summary>
public static class SetupKinds
{
    /// <summary>Locked to a signed pack: installs, updates and repairs come from the pack only.</summary>
    public const string Pack = "pack";
    /// <summary>A launcher-made copy of KSP where the player picks any mods.</summary>
    public const string Custom = "custom";
    /// <summary>A KSP folder the player already had (Steam, a CKAN instance); the launcher never deletes it.</summary>
    public const string Existing = "existing";
}

/// <summary>profile.json: identity and seed information of a managed KSP profile.</summary>
public sealed record ProfileManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? PackId { get; init; }
    /// <summary><see cref="SetupKinds"/>; profiles from before 0.3 have none and are packs when they name one.</summary>
    public string? Kind { get; init; }
    /// <summary>The game folder when it is not the profile's own <c>game</c> folder (existing installs).</summary>
    public string? GameDir { get; init; }
    public string? ChannelUrl { get; init; }
    public required SeedInfo Seed { get; init; }
    /// <summary>Optional override for the shared download cache (e.g. an existing CKAN cache).</summary>
    public string? CacheDir { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string EffectiveKind => Kind ?? (PackId is null ? SetupKinds.Custom : SetupKinds.Pack);
}

public sealed record SeedInfo
{
    public required string FromPath { get; init; }
    public required string BuildId64 { get; init; }
    public required string Mode { get; init; } // "hardlink" | "copy"
    public DateTimeOffset Utc { get; init; } = DateTimeOffset.UtcNow;
    public long BytesLinked { get; init; }
    public long BytesCopied { get; init; }
    public int FilesLinked { get; init; }
    public int FilesCopied { get; init; }
}

/// <summary>Layout of one profile directory.</summary>
public sealed class ProfilePaths
{
    public ProfilePaths(string profileDir)
    {
        Root = Path.GetFullPath(profileDir);
    }

    private string? _gameDir;

    public string Root { get; }
    public string ManifestFile => Path.Combine(Root, "profile.json");
    /// <summary>The profile's own <c>game</c> folder, or the existing install its manifest points at.</summary>
    public string GameDir => _gameDir ??= ExternalGameDir() ?? Path.Combine(Root, "game");
    public string GameDataDir => Path.Combine(GameDir, "GameData");
    public string CkanDir => Path.Combine(GameDir, "CKAN");
    public string StateDir => Path.Combine(Root, "state");
    public string InstalledStateFile => Path.Combine(StateDir, "installed.json");
    public string LockCopyFile => Path.Combine(StateDir, "current.lock.json");
    public string InventoryCopyFile => Path.Combine(StateDir, "current.inventory.json.gz");
    public string PackRepoDir => Path.Combine(StateDir, "packrepo");
    public string LockFile => Path.Combine(StateDir, "profile.lock");
    public string JournalDir => Path.Combine(Root, "journal");
    public string SnapshotsDir => Path.Combine(Root, "snapshots");
    public string QuarantineDir => Path.Combine(Root, "quarantine");
    public string SecretsFile => Path.Combine(Root, "secrets.dat");
    public string LogsDir => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, StateDir, PackRepoDir, JournalDir, SnapshotsDir, QuarantineDir, LogsDir })
        {
            Directory.CreateDirectory(d);
        }
    }

    public ProfileManifest ReadManifest() => LauncherJson.ReadFile<ProfileManifest>(ManifestFile);

    public void WriteManifest(ProfileManifest m)
    {
        LauncherJson.WriteFile(ManifestFile, m);
        _gameDir = m.GameDir is { Length: > 0 } g ? Path.GetFullPath(g) : null;
    }

    private string? ExternalGameDir()
    {
        try
        {
            return Exists && ReadManifest().GameDir is { Length: > 0 } g ? Path.GetFullPath(g) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
    public bool Exists => File.Exists(ManifestFile);
}

/// <summary>
/// Finds profiles: by default under <c>&lt;data&gt;\profiles\&lt;id&gt;</c>; profiles created at a custom root are
/// remembered in <c>profiles\index.json</c>.
/// </summary>
public sealed class ProfileStore
{
    private sealed class Index
    {
        public Dictionary<string, string> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly LauncherPaths _paths;

    public ProfileStore(LauncherPaths paths)
    {
        _paths = paths;
    }

    private string IndexFile => Path.Combine(_paths.ProfilesDir, "index.json");

    private Index ReadIndex() => File.Exists(IndexFile) ? LauncherJson.ReadFile<Index>(IndexFile) : new Index();

    public ProfilePaths Get(string id)
    {
        var idx = ReadIndex();
        return idx.Roots.TryGetValue(id, out var root) ? new ProfilePaths(root) : new ProfilePaths(Path.Combine(_paths.ProfilesDir, id));
    }

    /// <summary>Registers a profile that lives outside the default profiles folder.</summary>
    public void Register(string id, string root)
    {
        var idx = ReadIndex();
        idx.Roots[id] = Path.GetFullPath(root);
        Directory.CreateDirectory(_paths.ProfilesDir);
        LauncherJson.WriteFile(IndexFile, idx);
    }

    public void Unregister(string id)
    {
        var idx = ReadIndex();
        if (idx.Roots.Remove(id))
        {
            LauncherJson.WriteFile(IndexFile, idx);
        }
    }

    public IEnumerable<(ProfilePaths Paths, ProfileManifest Manifest)> List()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ReadIndex().Roots.Values)
        {
            var p = new ProfilePaths(root);
            if (p.Exists && seen.Add(p.Root))
            {
                yield return (p, p.ReadManifest());
            }
        }
        if (!Directory.Exists(_paths.ProfilesDir))
        {
            yield break;
        }
        foreach (var dir in Directory.EnumerateDirectories(_paths.ProfilesDir))
        {
            var p = new ProfilePaths(dir);
            if (p.Exists && seen.Add(p.Root))
            {
                yield return (p, p.ReadManifest());
            }
        }
    }
}
