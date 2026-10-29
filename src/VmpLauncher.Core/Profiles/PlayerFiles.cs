using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Security;

namespace VmpLauncher.Core.Profiles;

/// <summary>
/// The player's own files in a profile's game folder: saves, craft, missions, screenshots, KSP's settings.cfg, and the
/// GameData files the pack marks per-player or as settings mods rewrite (PluginData and the like). A reinstall moves
/// these into the fresh copy; everything else is rebuilt from the pack.
/// </summary>
public static class PlayerFiles
{
    private static readonly string[] Folders = { "saves", "Ships", "Missions", "Screenshots", "PluginData" };
    private static readonly string[] RootFiles = { "settings.cfg" };

    public sealed record Result(int Folders, int Files);

    /// <summary>Moves the player's files from <paramref name="fromGameDir"/> into <paramref name="toGameDir"/> (same drive),
    /// replacing what the fresh copy has at those paths. Junctions and symbolic links are left where they are.</summary>
    public static Result MoveInto(string fromGameDir, string toGameDir, FileRules rules, TextWriter? log = null)
    {
        var folders = 0;
        var files = 0;
        foreach (var folder in Folders)
        {
            var src = Path.Combine(fromGameDir, folder);
            if (!Directory.Exists(src) || SafePath.IsReparsePoint(src))
            {
                continue;
            }
            var dst = Path.Combine(toGameDir, folder);
            if (Directory.Exists(dst))
            {
                // The fresh copy's own version (empty, or the craft copied from the player's KSP when it was seeded).
                Directory.Delete(dst, recursive: true);
            }
            Directory.Move(src, dst);
            folders++;
        }
        foreach (var name in RootFiles)
        {
            var src = Path.Combine(fromGameDir, name);
            if (File.Exists(src))
            {
                File.Move(src, Path.Combine(toGameDir, name), overwrite: true);
                files++;
            }
        }
        var set = new FileRuleSet(rules);
        var gameData = Path.Combine(fromGameDir, "GameData");
        if (Directory.Exists(gameData) && !SafePath.IsReparsePoint(gameData))
        {
            foreach (var abs in Directory.EnumerateFiles(gameData, "*", SafePath.RecurseSkippingReparsePoints()).ToList())
            {
                var rel = Path.GetRelativePath(fromGameDir, abs).Replace('\\', '/');
                // Part icons count as mutable only because KSP redraws them; the fresh copy's are the right ones.
                if (rel.Contains("/@thumbs/", StringComparison.OrdinalIgnoreCase) || !(set.PerPlayer.IsMatch(rel) || set.MutableOwned.IsMatch(rel)))
                {
                    continue;
                }
                var dst = Path.Combine(toGameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(abs, dst, overwrite: true);
                files++;
            }
        }
        log?.WriteLine($"Kept your files: {folders} folders (saves, craft, screenshots...) and {files} settings files.");
        return new Result(folders, files);
    }
}
