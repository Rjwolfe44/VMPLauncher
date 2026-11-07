using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace VmpLauncher.Core.Profiles;

/// <summary>Size, last write time (UTC ticks) and NTFS identity (<c>volume:fileindex</c>) of a file.</summary>
public readonly record struct FileStat(long Size, long LastWriteTicks, string FileId);

/// <summary>Hard links and NTFS file identity. Windows only.</summary>
[SupportedOSPlatform("windows")]
public static class NativeFiles
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    /// <summary>Creates a hard link; throws Win32Exception on failure (different volume, too many links, etc.).</summary>
    public static void CreateHardLink(string newPath, string existingPath)
    {
        if (!CreateHardLinkW(LongPath(newPath), LongPath(existingPath), IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateHardLink failed for {newPath}");
        }
    }

    public static bool TryCreateHardLink(string newPath, string existingPath, out int error)
    {
        if (CreateHardLinkW(LongPath(newPath), LongPath(existingPath), IntPtr.Zero))
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>(volume serial, file index) identifies the underlying NTFS file regardless of which link opened it.</summary>
    /// <remarks>
    /// The handle asks only for attribute access, not read access: no file data is opened, so an antivirus scanner has
    /// nothing to scan and the call costs a fraction of a read open (it runs once per file in snapshots and checks).
    /// </remarks>
    public static (uint Volume, ulong FileId, uint Links) GetFileIdentity(string path)
    {
        using var handle = CreateFileW(LongPath(path), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            // The same exception types File.OpenHandle throws, so callers' IOException/UnauthorizedAccessException filters still apply.
            throw error switch
            {
                2 or 3 => new FileNotFoundException($"Could not find file '{path}'.", path),
                5 => new UnauthorizedAccessException($"Access to the path '{path}' is denied."),
                _ => new IOException($"Could not open {path} to read its file identity: {new Win32Exception(error).Message}", unchecked((int)0x80070000) | error),
            };
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetFileInformationByHandle failed for {path}");
        }
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    public static string FileIdString(string path)
    {
        var (vol, id, _) = GetFileIdentity(path);
        return FormatFileId(vol, id);
    }

    private static string FormatFileId(uint volume, ulong fileId) => $"{volume:x8}:{fileId:x16}";

    /// <summary>Size, last-write time (UTC ticks) and file id of a file from one attribute-only open. False when the file does not exist.</summary>
    /// <remarks>
    /// The handle's view is authoritative, unlike the size and time a directory listing reports, which NTFS updates
    /// lazily for files with several hard links.
    /// </remarks>
    public static bool TryGetFileStat(string path, out FileStat stat)
    {
        using var handle = CreateFileW(LongPath(path), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3)
            {
                stat = default;
                return false;
            }
            throw error == 5
                ? new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
                : new IOException($"Could not open {path} to read its attributes: {new Win32Exception(error).Message}", unchecked((int)0x80070000) | error);
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException($"GetFileInformationByHandle failed for {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
        if ((info.FileAttributes & 0x10) != 0)
        {
            stat = default; // a directory, not a file
            return false;
        }
        var ticks = DateTime.FromFileTimeUtc(((long)(uint)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime).Ticks;
        stat = new FileStat(((long)info.FileSizeHigh << 32) | info.FileSizeLow, ticks, FormatFileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow));
        return true;
    }

    /// <summary>Full paths of 260 characters or more get the <c>\\?\</c> prefix the raw Win32 calls need for long paths.</summary>
    public static string LongPath(string path)
    {
        if (path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_ALL = 0x7; // read | write | delete
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    public const int ERROR_TOO_MANY_LINKS = 1142;
    public const int ERROR_NOT_SAME_DEVICE = 17;
}
