using Microsoft.Win32;
using VmpLauncher.Core.Profiles;

namespace VmpLauncher.Game;

/// <summary>Facts about one KSP install folder, as shown in the Welcome wizard checklist.</summary>
public sealed record KspInstallInfo
{
    public required string Path { get; init; }
    public required string Source { get; init; } // "steam" | "remembered" | "browse" | "default"
    public bool HasExecutable { get; init; }
    public string? BuildId { get; init; }
    public string? Version { get; init; }
    public bool HasMakingHistory { get; init; }
    public bool HasBreakingGround { get; init; }
    public long FreeBytesOnDrive { get; init; }

    public bool MatchesBuild(string requiredBuildId) => string.Equals(BuildId, requiredBuildId, StringComparison.Ordinal);
    public bool IsComplete(string requiredBuildId) => HasExecutable && MatchesBuild(requiredBuildId) && HasMakingHistory && HasBreakingGround;
}

/// <summary>Finds KSP installs: Steam libraries (registry → libraryfolders.vdf → appmanifest_220200.acf), remembered paths, common defaults.</summary>
public static class KspLocator
{
    public const int SteamAppId = 220200;

    public static IReadOnlyList<KspInstallInfo> Discover(IEnumerable<string>? rememberedPaths = null)
    {
        var found = new List<KspInstallInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in SteamLibraries())
        {
            var manifest = System.IO.Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf");
            string installDirName = "Kerbal Space Program";
            if (File.Exists(manifest))
            {
                try
                {
                    var node = VdfParser.Parse(File.ReadAllText(manifest));
                    installDirName = node.GetString("AppState", "installdir") ?? installDirName;
                }
                catch (Exception)
                {
                }
            }
            var dir = System.IO.Path.Combine(lib, "steamapps", "common", installDirName);
            if (Directory.Exists(dir) && seen.Add(dir))
            {
                found.Add(Inspect(dir, "steam"));
            }
        }
        foreach (var p in rememberedPaths ?? Array.Empty<string>())
        {
            if (Directory.Exists(p) && seen.Add(p))
            {
                found.Add(Inspect(p, "remembered"));
            }
        }
        foreach (var p in DefaultLocations())
        {
            if (Directory.Exists(p) && seen.Add(p))
            {
                found.Add(Inspect(p, "default"));
            }
        }
        return found;
    }

    public static KspInstallInfo Inspect(string dir, string source = "browse")
    {
        var exe = System.IO.Path.Combine(dir, "KSP_x64.exe");
        var buildFile = System.IO.Path.Combine(dir, "buildID64.txt");
        string? buildId = null;
        string? version = null;
        if (File.Exists(buildFile))
        {
            buildId = ProfileSeeder.ReadBuildId(buildFile);
        }
        var readme = System.IO.Path.Combine(dir, "readme.txt");
        if (File.Exists(readme))
        {
            foreach (var line in File.ReadLines(readme).Take(40))
            {
                var t = line.Trim();
                if (t.StartsWith("Version ", StringComparison.OrdinalIgnoreCase))
                {
                    version = t[8..].Trim();
                    break;
                }
            }
        }
        long free = 0;
        try
        {
            free = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(dir))!).AvailableFreeSpace;
        }
        catch (Exception)
        {
        }
        return new KspInstallInfo
        {
            Path = System.IO.Path.GetFullPath(dir),
            Source = source,
            HasExecutable = File.Exists(exe),
            BuildId = buildId,
            Version = version,
            HasMakingHistory = File.Exists(System.IO.Path.Combine(dir, "GameData", "SquadExpansion", "MakingHistory", "readme.txt")),
            HasBreakingGround = File.Exists(System.IO.Path.Combine(dir, "GameData", "SquadExpansion", "Serenity", "readme.txt")),
            FreeBytesOnDrive = free,
        };
    }

    public static IReadOnlyList<string> SteamLibraries()
    {
        var libs = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return libs;
        }
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath") as string;
        }
        catch (Exception)
        {
        }
        if (steamPath is null)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                steamPath = key?.GetValue("InstallPath") as string;
            }
            catch (Exception)
            {
            }
        }
        if (steamPath is null)
        {
            return libs;
        }
        steamPath = steamPath.Replace('/', '\\');
        libs.Add(steamPath);
        var vdf = System.IO.Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var root = VdfParser.Parse(File.ReadAllText(vdf));
                var folders = root["libraryfolders"];
                if (folders is not null)
                {
                    foreach (var entry in folders.Children.Values)
                    {
                        var p = entry.GetString("path");
                        if (p is not null && !libs.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            libs.Add(p);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        return libs;
    }

    private static IEnumerable<string> DefaultLocations()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return System.IO.Path.Combine(desktop, "Kerbal Space Program");
        yield return @"C:\Kerbal Space Program";
        yield return @"C:\Games\Kerbal Space Program";
        yield return @"D:\Kerbal Space Program";
        yield return @"D:\Games\Kerbal Space Program";
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return System.IO.Path.Combine(pf86, "Steam", "steamapps", "common", "Kerbal Space Program");
    }
}
