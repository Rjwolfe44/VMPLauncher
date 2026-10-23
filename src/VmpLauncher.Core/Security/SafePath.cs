using System.Text;

namespace VmpLauncher.Core.Security;

/// <summary>
/// Validation for relative paths that come from untrusted data (packs, locks, inventories, zip entries).
/// Rejects anything that could escape the profile or behave differently on NTFS than it looks.
/// </summary>
public static class SafePath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int DefaultMaxLength = 220;

    /// <summary>Returns the normalized relative path (forward slashes, NFC) or throws <see cref="UnsafePathException"/>.</summary>
    public static string Normalize(string relativePath, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new UnsafePathException(relativePath, "empty path");
        }
        var p = relativePath.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (p.Length > maxLength)
        {
            throw new UnsafePathException(relativePath, $"longer than {maxLength} characters");
        }
        if (p.StartsWith('/') || p.StartsWith("//", StringComparison.Ordinal))
        {
            throw new UnsafePathException(relativePath, "rooted path");
        }
        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
        {
            throw new UnsafePathException(relativePath, "drive-letter path");
        }
        if (p.Contains(':'))
        {
            throw new UnsafePathException(relativePath, "contains ':' (alternate data stream or drive)");
        }
        foreach (var c in p)
        {
            if (c < 0x20 || c == 0x7f || c is '<' or '>' or '"' or '|' or '?' or '*')
            {
                throw new UnsafePathException(relativePath, $"illegal character U+{(int)c:X4}");
            }
        }
        var segments = p.Split('/');
        foreach (var seg in segments)
        {
            if (seg.Length == 0)
            {
                throw new UnsafePathException(relativePath, "empty path segment");
            }
            if (seg is "." or "..")
            {
                throw new UnsafePathException(relativePath, "relative segment '.' or '..'");
            }
            if (seg.EndsWith('.') || seg.EndsWith(' '))
            {
                throw new UnsafePathException(relativePath, $"segment '{seg}' ends with a dot or space");
            }
            var stem = seg.Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                throw new UnsafePathException(relativePath, $"reserved device name '{seg}'");
            }
        }
        return p;
    }

    public static bool TryNormalize(string relativePath, out string normalized, out string? reason, int maxLength = DefaultMaxLength)
    {
        try
        {
            normalized = Normalize(relativePath, maxLength);
            reason = null;
            return true;
        }
        catch (UnsafePathException ex)
        {
            normalized = relativePath;
            reason = ex.Reason;
            return false;
        }
    }

    /// <summary>Combines a trusted root with an untrusted relative path and proves the result stays under the root.</summary>
    public static string Resolve(string root, string relativePath, int maxLength = DefaultMaxLength)
    {
        var rel = Normalize(relativePath, maxLength);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafePathException(relativePath, "escapes the root directory");
        }
        return full;
    }

    /// <summary>True when any directory between root (exclusive) and the file (exclusive) is a reparse point.</summary>
    public static bool HasReparsePointAbove(string root, string fullPath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(fullPath);
        while (dir is not null && dir.Length > fullRoot.Length)
        {
            var di = new DirectoryInfo(dir);
            if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    /// <summary>True when the file or directory itself is a reparse point (junction, symbolic link). False when it does not exist.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Recursive enumeration that neither returns nor descends into reparse points, so a junction inside a game folder
    /// never leads the launcher to snapshot, quarantine or delete files elsewhere on the disk.
    /// </summary>
    public static EnumerationOptions RecurseSkippingReparsePoints() => new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        MatchType = MatchType.Win32,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Throws when a directory between <paramref name="root"/> and <paramref name="fullPath"/> is a reparse point.</summary>
    public static void ThrowIfReparsePointAbove(string root, string fullPath, string relativePath)
    {
        if (HasReparsePointAbove(root, fullPath))
        {
            throw new Diagnostics.LauncherException(Diagnostics.ErrorCode.UnsafePath,
                $"A folder on the path {relativePath} is a junction or symbolic link; the launcher does not write through those.", Diagnostics.RecoveryHint.None);
        }
    }
}

public sealed class UnsafePathException(string path, string reason)
    : IOException($"Unsafe path '{path}': {reason}")
{
    public string Path { get; } = path;
    public string Reason { get; } = reason;
}
