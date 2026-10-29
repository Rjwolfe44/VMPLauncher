using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.Core.Profiles;

public static class ProfilePathValidator
{
    /// <summary>Windows MAX_PATH budget we keep for the game, since Unity/Mono cannot be assumed long-path aware.</summary>
    public const int MaxPathBudget = 259;

    public static void Validate(string profileRoot, int maxRelPathLength, string? sourceKspDir, string? cacheDir)
    {
        var full = Path.GetFullPath(profileRoot);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new LauncherException(ErrorCode.ProfilePathInvalid, "Profiles cannot live on a network (UNC) path.", RecoveryHint.ChooseAnotherFolder);
        }
        // The VMP client cuts DLL paths at the first "gamedata" in the lowercase full path; a profile root containing
        // that word would corrupt every path it reports to the server.
        if (full.Contains("gamedata", StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(ErrorCode.ProfilePathInvalid, "The profile folder path must not contain the word \"GameData\" (VMP would misreport mod paths).", RecoveryHint.ChooseAnotherFolder);
        }
        var gameRootLen = Path.Combine(full, "game").Length + 1; // + separator
        if (gameRootLen + maxRelPathLength > MaxPathBudget)
        {
            throw new LauncherException(ErrorCode.ProfilePathInvalid,
                $"The profile folder is too deep: the longest pack file would exceed {MaxPathBudget} characters. Choose a shorter path (e.g. C:\\VMP).",
                RecoveryHint.ChooseAnotherFolder);
        }
        var root = Path.GetPathRoot(full);
        if (root is null)
        {
            throw new LauncherException(ErrorCode.ProfilePathInvalid, "Could not determine the drive of the profile folder.", RecoveryHint.ChooseAnotherFolder);
        }
        var drive = new DriveInfo(root);
        if (drive.IsReady && !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(ErrorCode.ProfilePathInvalid, $"The profile drive is {drive.DriveFormat}; NTFS is required (hard links and file ids).", RecoveryHint.ChooseAnotherFolder);
        }
        if (sourceKspDir is not null && IsInside(full, sourceKspDir))
        {
            throw new LauncherException(ErrorCode.ProfilePathInvalid, "The profile cannot be inside your existing KSP folder.", RecoveryHint.ChooseAnotherFolder);
        }
        if (cacheDir is not null && (IsInside(full, cacheDir) || IsInside(cacheDir, full)))
        {
            throw new LauncherException(ErrorCode.ProfilePathInvalid, "The profile and the download cache must not contain each other.", RecoveryHint.ChooseAnotherFolder);
        }
    }

    public static bool IsInside(string candidate, string container)
    {
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var k = Path.GetFullPath(container).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(k, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameVolume(string a, string b)
        => string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
