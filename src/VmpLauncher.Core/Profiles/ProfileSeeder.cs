using System.Diagnostics;
using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.Core.Profiles;

/// <summary>
/// Creates the <c>game\</c> folder of a profile from the player's own KSP install: stock game files, DLC, engine.
/// Same volume → per-file hard links (saves ~5.6 GiB); otherwise copies. Never junctions directories, never
/// touches the source, never copies saves, logs, mods, or CKAN state.
/// </summary>
public sealed class ProfileSeeder
{
    /// <param name="SkipRelativePaths">Game-relative paths (forward slashes) owned by the pack; never seeded from the source.</param>
    public sealed record Plan(string SourceDir, string TargetGameDir, bool HardLink, IReadOnlyCollection<string> SkipRelativePaths);

    public sealed record Result(int FilesLinked, int FilesCopied, long BytesLinked, long BytesCopied, TimeSpan Elapsed, IReadOnlyList<string> Warnings);

    private static readonly string[] RootFilesToSeed =
    {
        "KSP_x64.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", "SDL2.dll", "XInputInterface.dll",
        "buildID64.txt", "buildID.txt", "readme.txt", "LegalNotice.txt", "steam_appid.txt",
        "SDL_LICENSE", "XINPUTDOTNET_LICENSE", "Unity Companion License.txt", "KSP_LICENSE",
    };

    private static readonly string[] RootFilesToCopy = { "settings.cfg", "Physics.cfg" };

    private static readonly string[] DirectoriesToLink = { "KSP_x64_Data", "MonoBleedingEdge", @"GameData\Squad", @"GameData\SquadExpansion" };

    private static readonly string[] DirectoriesToCopy = { "Ships" };

    private static readonly string[] EmptyDirectories =
    {
        "saves", "Missions", @"Ships\VAB", @"Ships\SPH", @"Ships\@thumbs", "Screenshots", "Logs", "PluginData",
        "Internals", "Resources", "sounds", "Parts", "Plugins", "GameData",
    };

    private readonly TextWriter? _log;

    public ProfileSeeder(TextWriter? log = null)
    {
        _log = log;
    }

    /// <summary>Checks the source install and returns its build id, or throws a LauncherException.</summary>
    public static string ValidateSource(string sourceDir, string requiredBuildId64, IEnumerable<(string Folder, string Name)> requiredDlc)
    {
        if (!File.Exists(Path.Combine(sourceDir, "KSP_x64.exe")))
        {
            throw new LauncherException(ErrorCode.GameNotFound, $"No KSP_x64.exe in {sourceDir}. Choose your Kerbal Space Program folder.", RecoveryHint.ChooseAnotherFolder);
        }
        var buildFile = Path.Combine(sourceDir, "buildID64.txt");
        if (!File.Exists(buildFile))
        {
            throw new LauncherException(ErrorCode.WrongGameVersion, "buildID64.txt is missing; this does not look like a complete 64-bit KSP install.", RecoveryHint.ChooseAnotherFolder);
        }
        var buildId = ReadBuildId(buildFile);
        if (!string.Equals(buildId, requiredBuildId64, StringComparison.Ordinal))
        {
            throw new LauncherException(ErrorCode.WrongGameVersion, $"This pack needs KSP build {requiredBuildId64} (1.12.5); your install is build {buildId ?? "unknown"}.", RecoveryHint.None);
        }
        var missing = requiredDlc.Where(d => !File.Exists(Path.Combine(sourceDir, "GameData", d.Folder.Replace('/', Path.DirectorySeparatorChar), "readme.txt"))).Select(d => d.Name).ToList();
        if (missing.Count > 0)
        {
            throw new LauncherException(ErrorCode.MissingDlc, $"Missing DLC: {string.Join(", ", missing)}. The pack requires Making History and Breaking Ground.", RecoveryHint.None);
        }
        return buildId!;
    }

    public static string? ReadBuildId(string buildIdFile)
    {
        foreach (var line in File.ReadLines(buildIdFile))
        {
            var t = line.Trim();
            if (t.StartsWith("build id", StringComparison.OrdinalIgnoreCase))
            {
                var idx = t.IndexOf('=');
                if (idx > 0)
                {
                    return t[(idx + 1)..].Trim();
                }
            }
        }
        return null;
    }

    public Result Seed(Plan plan, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        int linked = 0, copied = 0;
        long bytesLinked = 0, bytesCopied = 0;
        var skip = new HashSet<string>(plan.SkipRelativePaths.Select(p => p.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(plan.TargetGameDir);

        foreach (var name in RootFilesToSeed)
        {
            var src = Path.Combine(plan.SourceDir, name);
            if (!File.Exists(src))
            {
                continue;
            }
            if (skip.Contains(name))
            {
                _log?.WriteLine($"  skip {name} (owned by a pack module)");
                continue;
            }
            Place(src, Path.Combine(plan.TargetGameDir, name), plan.HardLink, ref linked, ref copied, ref bytesLinked, ref bytesCopied, warnings);
        }
        foreach (var name in RootFilesToCopy)
        {
            var src = Path.Combine(plan.SourceDir, name);
            if (File.Exists(src))
            {
                Place(src, Path.Combine(plan.TargetGameDir, name), hardLink: false, ref linked, ref copied, ref bytesLinked, ref bytesCopied, warnings);
            }
        }
        foreach (var rel in DirectoriesToLink)
        {
            var src = Path.Combine(plan.SourceDir, rel);
            if (!Directory.Exists(src))
            {
                warnings.Add($"Source has no {rel}; skipped.");
                continue;
            }
            _log?.WriteLine($"  {(plan.HardLink ? "linking" : "copying")} {rel}");
            PlaceTree(src, Path.Combine(plan.TargetGameDir, rel), rel, plan.HardLink, skip, ref linked, ref copied, ref bytesLinked, ref bytesCopied, warnings, ct);
        }
        foreach (var rel in DirectoriesToCopy)
        {
            var src = Path.Combine(plan.SourceDir, rel);
            if (Directory.Exists(src))
            {
                PlaceTree(src, Path.Combine(plan.TargetGameDir, rel), rel, hardLink: false, skip, ref linked, ref copied, ref bytesLinked, ref bytesCopied, warnings, ct, skipThumbs: true);
            }
        }
        foreach (var rel in EmptyDirectories)
        {
            Directory.CreateDirectory(Path.Combine(plan.TargetGameDir, rel));
        }
        return new Result(linked, copied, bytesLinked, bytesCopied, sw.Elapsed, warnings);
    }

    private void PlaceTree(string srcDir, string dstDir, string gameRel, bool hardLink, HashSet<string> skip, ref int linked, ref int copied, ref long bytesLinked, ref long bytesCopied, List<string> warnings, CancellationToken ct, bool skipThumbs = false)
    {
        Directory.CreateDirectory(dstDir);
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(srcDir, file);
            if (skipThumbs && rel.StartsWith("@thumbs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var gamePath = (gameRel + "/" + rel).Replace('\\', '/');
            if (skip.Contains(gamePath))
            {
                skipped++;
                continue;
            }
            var dst = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            Place(file, dst, hardLink, ref linked, ref copied, ref bytesLinked, ref bytesCopied, warnings);
        }
        if (skipped > 0)
        {
            _log?.WriteLine($"  skipped {skipped} file(s) under {gameRel} that the pack owns");
        }
    }

    private static void Place(string src, string dst, bool hardLink, ref int linked, ref int copied, ref long bytesLinked, ref long bytesCopied, List<string> warnings)
    {
        var size = new FileInfo(src).Length;
        if (File.Exists(dst))
        {
            File.Delete(dst);
        }
        if (hardLink && OperatingSystem.IsWindows())
        {
            if (NativeFiles.TryCreateHardLink(dst, src, out var error))
            {
                linked++;
                bytesLinked += size;
                return;
            }
            if (error is not (NativeFiles.ERROR_TOO_MANY_LINKS or NativeFiles.ERROR_NOT_SAME_DEVICE))
            {
                warnings.Add($"Hard link failed for {Path.GetFileName(src)} (error {error}); copied instead.");
            }
        }
        File.Copy(src, dst, overwrite: true);
        copied++;
        bytesCopied += size;
    }
}
