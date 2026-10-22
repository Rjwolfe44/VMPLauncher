using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace VmpLauncher.Core.Profiles;

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
        if (!CreateHardLinkW(newPath, existingPath, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateHardLink failed for {newPath}");
        }
    }

    public static bool TryCreateHardLink(string newPath, string existingPath, out int error)
    {
        if (CreateHardLinkW(newPath, existingPath, IntPtr.Zero))
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>(volume serial, file index) identifies the underlying NTFS file regardless of which link opened it.</summary>
    public static (uint Volume, ulong FileId, uint Links) GetFileIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetFileInformationByHandle failed for {path}");
        }
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    public static string FileIdString(string path)
    {
        var (vol, id, _) = GetFileIdentity(path);
        return $"{vol:x8}:{id:x16}";
    }

    public const int ERROR_TOO_MANY_LINKS = 1142;
    public const int ERROR_NOT_SAME_DEVICE = 17;
}
