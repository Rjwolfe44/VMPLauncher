using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Game;

namespace VmpLauncher.App.Services;

/// <summary>One KSP folder the launcher manages. The pack's own copy is always listed first.</summary>
public sealed record SetupInfo(string Id, string Name, string Kind, string GameDir, string Root, string? PackId)
{
    public bool IsPack => Kind == SetupKinds.Pack;
    public bool CanChangeMods => !IsPack;
    public bool IsReady => File.Exists(Path.Combine(GameDir, "KSP_x64.exe"));

    public string KindLabel => Kind switch
    {
        SetupKinds.Pack => "Multiplayer pack",
        SetupKinds.Existing => "Your KSP folder",
        _ => "Launcher copy",
    };

    public override string ToString() => Name;
}

/// <summary>A KSP install found on this PC that could become a setup.</summary>
public sealed record FoundInstall(string Path, string Source, string? Version);

/// <summary>
/// Setups besides the pack: launcher-made stock copies (hard links, so they cost almost no space) and KSP folders the
/// player already has. Never deletes a folder the launcher did not make.
/// </summary>
public sealed class SetupService
{
    private readonly LauncherPaths _paths;
    private readonly AppSettingsStore _settings;
    private readonly LauncherService _launcher;
    private readonly OperationRunner _runner;
    private readonly ILogger<SetupService> _log;

    public SetupService(LauncherPaths paths, AppSettingsStore settings, LauncherService launcher, OperationRunner runner, ILogger<SetupService> log)
    {
        _paths = paths;
        _settings = settings;
        _launcher = launcher;
        _runner = runner;
        _log = log;
    }

    /// <summary>Raised after a setup was added, removed, renamed or selected.</summary>
    public event Action? Changed;

    public SetupInfo PackSetup
    {
        get
        {
            var profile = _launcher.ProfilePaths;
            var name = _launcher.CurrentPack?.Lock.Pack.Name ?? "VMP pack";
            return new SetupInfo(_launcher.PackId, name, SetupKinds.Pack, profile.GameDir, profile.Root, _launcher.PackId);
        }
    }

    public IReadOnlyList<SetupInfo> List()
    {
        var pack = PackSetup;
        var list = new List<SetupInfo> { pack };
        foreach (var (p, m) in new ProfileStore(_paths).List())
        {
            if (m.EffectiveKind == SetupKinds.Pack || string.Equals(p.Root, pack.Root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            list.Add(new SetupInfo(m.Id, m.Name, m.EffectiveKind, p.GameDir, p.Root, null));
        }
        return list;
    }

    public SetupInfo Active
    {
        get
        {
            var id = _settings.Current.ModsSetupId;
            return (id is null ? null : List().FirstOrDefault(s => s.Id == id)) ?? PackSetup;
        }
    }

    public void Select(string id)
    {
        _settings.Update(s => s with { ModsSetupId = id == _launcher.PackId ? null : id });
        Changed?.Invoke();
    }

    private string NewId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        slug = string.IsNullOrEmpty(slug) ? "setup" : slug[..Math.Min(slug.Length, 32)];
        var store = new ProfileStore(_paths);
        var id = slug;
        for (var n = 2; store.Get(id).Exists || Directory.Exists(Path.Combine(_paths.ProfilesDir, id)) || id == _launcher.PackId; n++)
        {
            id = $"{slug}-{n}";
        }
        return id;
    }

    private static string CleanName(string name)
    {
        var n = name.Trim();
        if (n.Length is 0 or > 60)
        {
            throw new LauncherException(ErrorCode.Unknown, "Give the setup a name of 1 to 60 characters.");
        }
        return n;
    }

    /// <summary>"Career" → "Career 2" when a setup already has that name, so the picker never shows two alike.</summary>
    private string UniqueName(string name)
    {
        var taken = List().Select(s => s.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }
        for (var n = 2; ; n++)
        {
            var candidate = $"{(name.Length > 56 ? name[..56] : name)} {n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A stock copy of <paramref name="sourceKspDir"/> (game files only: no mods, saves or settings).</summary>
    public async Task<SetupInfo> CreateCopyAsync(string name, string sourceKspDir)
    {
        name = UniqueName(CleanName(name));
        var source = Path.GetFullPath(sourceKspDir);
        if (!File.Exists(Path.Combine(source, "KSP_x64.exe")))
        {
            throw new LauncherException(ErrorCode.GameNotFound, $"No KSP_x64.exe in {source}. Choose your Kerbal Space Program folder.", RecoveryHint.ChooseAnotherFolder);
        }
        var id = NewId(name);
        var profile = new ProfilePaths(Path.Combine(_paths.ProfilesDir, id));
        var info = await _runner.RunAsync(OperationKind.Setup, $"Making \"{name}\"", (progress, ct) => Task.Run(() =>
        {
            var hardLink = ProfilePathValidator.SameVolume(source, profile.Root);
            progress.Report(new Ckan.Users.CkanProgress(hardLink ? "Linking the game files (no extra space)…" : "Copying the game files…", -1, null));
            profile.EnsureCreated();
            var buildFile = Path.Combine(source, "buildID64.txt");
            var buildId = File.Exists(buildFile) ? ProfileSeeder.ReadBuildId(buildFile) : null;
            try
            {
                var result = new ProfileSeeder().Seed(new ProfileSeeder.Plan(source, profile.GameDir, hardLink, Array.Empty<string>()), ct);
                profile.WriteManifest(new ProfileManifest
                {
                    Id = id,
                    Name = name,
                    Kind = SetupKinds.Custom,
                    Seed = new SeedInfo
                    {
                        FromPath = source,
                        BuildId64 = buildId ?? string.Empty,
                        Mode = hardLink ? "hardlink" : "copy",
                        BytesLinked = result.BytesLinked,
                        BytesCopied = result.BytesCopied,
                        FilesLinked = result.FilesLinked,
                        FilesCopied = result.FilesCopied,
                    },
                });
                _log.LogInformation("Setup {Id} created from {Source}: {Linked} linked, {Copied} copied", id, source, result.FilesLinked, result.FilesCopied);
            }
            catch
            {
                TryDelete(profile.Root);
                throw;
            }
            return new SetupInfo(id, name, SetupKinds.Custom, profile.GameDir, profile.Root, null);
        }, ct)).ConfigureAwait(true);
        Select(info.Id);
        return info;
    }

    /// <summary>Manages mods in a KSP folder the player already has. The folder itself is never moved or deleted.</summary>
    public SetupInfo AddExisting(string name, string kspDir)
    {
        name = UniqueName(CleanName(name));
        var dir = Path.GetFullPath(kspDir);
        if (!File.Exists(Path.Combine(dir, "KSP_x64.exe")))
        {
            throw new LauncherException(ErrorCode.GameNotFound, $"No KSP_x64.exe in {dir}. Choose your Kerbal Space Program folder.", RecoveryHint.ChooseAnotherFolder);
        }
        if (ProfilePathValidator.IsInside(dir, _paths.DataRoot))
        {
            throw new LauncherException(ErrorCode.ProfilePathInvalid, "That folder is already one of the launcher's own copies.", RecoveryHint.ChooseAnotherFolder);
        }
        if (List().FirstOrDefault(s => string.Equals(Path.GetFullPath(s.GameDir), dir, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            Select(existing.Id);
            return existing;
        }
        var id = NewId(name);
        var profile = new ProfilePaths(Path.Combine(_paths.ProfilesDir, id));
        profile.EnsureCreated();
        var buildFile = Path.Combine(dir, "buildID64.txt");
        profile.WriteManifest(new ProfileManifest
        {
            Id = id,
            Name = name,
            Kind = SetupKinds.Existing,
            GameDir = dir,
            Seed = new SeedInfo { FromPath = dir, BuildId64 = (File.Exists(buildFile) ? ProfileSeeder.ReadBuildId(buildFile) : null) ?? string.Empty, Mode = "existing" },
        });
        _log.LogInformation("Setup {Id} added for existing folder {Dir}", id, dir);
        var info = new SetupInfo(id, name, SetupKinds.Existing, dir, profile.Root, null);
        Select(info.Id);
        return info;
    }

    public void Rename(string id, string name)
    {
        name = CleanName(name);
        var setup = List().FirstOrDefault(s => s.Id == id && !s.IsPack) ?? throw new LauncherException(ErrorCode.Unknown, "That setup no longer exists.");
        var profile = new ProfilePaths(setup.Root);
        profile.WriteManifest(profile.ReadManifest() with { Name = name });
        Changed?.Invoke();
    }

    /// <summary>
    /// Forgets a setup. A launcher copy's files are deleted (they are hard links or copies the launcher made); an existing
    /// KSP folder is left exactly as it is and only the launcher's notes about it go.
    /// </summary>
    public async Task RemoveAsync(string id)
    {
        var setup = List().FirstOrDefault(s => s.Id == id) ?? throw new LauncherException(ErrorCode.Unknown, "That setup no longer exists.");
        if (setup.IsPack)
        {
            throw new LauncherException(ErrorCode.Unknown, "The pack's copy is managed from Home.");
        }
        if (OperatingSystem.IsWindows())
        {
            GameProcessGuard.ThrowIfRunning(setup.GameDir);
        }
        if (setup.Kind == SetupKinds.Existing && ProfilePathValidator.IsInside(setup.GameDir, setup.Root))
        {
            throw new LauncherException(ErrorCode.UnsafePath, "Refusing to delete: the game folder is inside the setup's state folder.");
        }
        if (!ProfilePathValidator.IsInside(setup.Root, _paths.ProfilesDir))
        {
            throw new LauncherException(ErrorCode.UnsafePath, $"Refusing to delete {setup.Root}: it is not inside the launcher's profiles folder.");
        }
        await _runner.RunAsync(OperationKind.Setup, $"Removing \"{setup.Name}\"", (progress, ct) => Task.Run(() =>
        {
            progress.Report(new Ckan.Users.CkanProgress(setup.Kind == SetupKinds.Existing ? "Forgetting the folder (its files stay)…" : "Deleting the copy…", -1, null));
            Directory.Delete(setup.Root, recursive: true);
            _log.LogInformation("Setup {Id} removed ({Kind})", setup.Id, setup.Kind);
            return 0;
        }, ct)).ConfigureAwait(true);
        if (_settings.Current.ModsSetupId == id)
        {
            _settings.Update(s => s with { ModsSetupId = null });
        }
        Changed?.Invoke();
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && ProfilePathValidator.IsInside(dir, _paths.ProfilesDir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not delete the partial setup {Dir}", dir);
        }
    }

    /// <summary>Starts KSP from a custom or existing setup (no server, no VMP settings).</summary>
    public Process Launch(SetupInfo setup)
    {
        if (setup.IsPack)
        {
            throw new LauncherException(ErrorCode.Unknown, "Start the pack from Play.");
        }
        if (OperatingSystem.IsWindows())
        {
            GameProcessGuard.ThrowIfRunning(setup.GameDir);
        }
        var s = _settings.Current;
        var extra = s.KspOnMainMonitor ? new[] { "-monitor", "1" } : Array.Empty<string>();
        _log.LogInformation("Launching KSP from setup {Id} ({Dir})", setup.Id, setup.GameDir);
        return KspLauncher.Launch(new KspLauncher.LaunchSpec(setup.GameDir, null, s.BorderlessWindow, extra));
    }

    /// <summary>KSP installs on this PC (Steam, remembered folders, CKAN's instances) that are not setups yet.</summary>
    public IReadOnlyList<FoundInstall> FindInstalls()
    {
        var known = List().Select(s => Path.GetFullPath(s.GameDir)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<FoundInstall>();
        void Add(string path, string source, string? version)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(Path.Combine(full, "KSP_x64.exe")) && !known.Contains(full) && !ProfilePathValidator.IsInside(full, _paths.DataRoot)
                && found.All(f => !string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new FoundInstall(full, source, version));
            }
        }
        try
        {
            foreach (var k in KspLocator.Discover(_settings.Current.RememberedKspPaths.Append(_settings.Current.SourceKspPath ?? string.Empty).Where(p => p.Length > 0)))
            {
                Add(k.Path, k.Source, k.Version);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "KSP discovery failed");
        }
        foreach (var path in CkanInstancePaths())
        {
            Add(path, "ckan", null);
        }
        return found;
    }

    /// <summary>Game folders registered in the player's own CKAN (read-only; CKAN's config is never written).</summary>
    private List<string> CkanInstancePaths()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CKAN", "config.json");
        var paths = new List<string>();
        try
        {
            if (!File.Exists(config))
            {
                return paths;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            if (doc.RootElement.TryGetProperty("GameInstances", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var inst in list.EnumerateArray())
                {
                    var game = inst.TryGetProperty("Game", out var g) ? g.GetString() : "KSP";
                    if (inst.TryGetProperty("Path", out var p) && p.GetString() is { Length: > 0 } path && (game is null or "KSP"))
                    {
                        paths.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read CKAN's instance list");
        }
        return paths;
    }
}
