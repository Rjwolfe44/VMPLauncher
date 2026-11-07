using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace VmpLauncher.Core.Transactions;

/// <summary>
/// How many threads per-file work (hard links, deletes, file identity, hashing) uses on a profile's drive.
/// Many small metadata operations on an SSD scale with threads until the file system's own locks flatten the gain;
/// on a spinning disk extra threads only add seeks. Two cores stay free so the window stays responsive.
/// </summary>
public static class FileParallelism
{
    /// <summary>Upper bound from measurements on a 32-thread machine with an NVMe drive: snapshot and hashing still gain from 8 to 16 threads, nothing beyond.</summary>
    public const int MaxThreads = 16;

    /// <summary>Spinning disks: one reader plus one hashing thread.</summary>
    public const int SpinningDiskThreads = 2;

    /// <summary>When the drive type cannot be read (network share, unusual driver): the cap the launcher used before.</summary>
    public const int UnknownDriveThreads = 4;

    private static readonly Dictionary<string, bool?> SeekPenaltyByVolume = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests and the performance harness pin the degree here; null means adaptive.</summary>
    public static int? Override { get; set; }

    public static int For(string path) => Override ?? Adaptive(Environment.ProcessorCount, HasSeekPenalty(path));

    /// <param name="spinningDisk">True for a hard disk, false for an SSD, null when unknown.</param>
    public static int Adaptive(int processorCount, bool? spinningDisk)
    {
        var threads = Math.Clamp(processorCount - 2, 1, MaxThreads);
        return spinningDisk switch
        {
            true => Math.Min(threads, SpinningDiskThreads),
            null => Math.Min(threads, UnknownDriveThreads),
            false => threads,
        };
    }

    public static ParallelOptions Options(string path, CancellationToken ct)
        => new() { MaxDegreeOfParallelism = For(path), CancellationToken = ct };

    /// <summary>
    /// True when the volume holding <paramref name="path"/> reports a seek penalty (a hard disk), false for an SSD, null
    /// when it cannot be told. The volume is found with GetVolumePathName, so a drive mounted in a folder resolves to
    /// that drive, not to the drive letter above it.
    /// </summary>
    public static bool? HasSeekPenalty(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var volume = VolumePath(path);
        if (volume is null)
        {
            return null;
        }
        lock (SeekPenaltyByVolume)
        {
            if (!SeekPenaltyByVolume.TryGetValue(volume, out var penalty))
            {
                penalty = QuerySeekPenalty(volume);
                SeekPenaltyByVolume[volume] = penalty;
            }
            return penalty;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? VolumePath(string path)
    {
        try
        {
            var buffer = new char[1024];
            return GetVolumePathNameW(Profiles.NativeFiles.LongPath(Path.GetFullPath(path)), buffer, buffer.Length)
                ? new string(buffer).TrimEnd('\0')
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool? QuerySeekPenalty(string volumePath)
    {
        try
        {
            // The volume's device name (\\?\Volume{guid}) works for drive letters and folder mount points alike; opened
            // with no access rights, which is enough for a storage property query and needs no elevation.
            var mountPoint = volumePath.EndsWith('\\') ? volumePath : volumePath + "\\";
            var name = new char[100];
            if (!GetVolumeNameForVolumeMountPointW(mountPoint, name, name.Length))
            {
                return null;
            }
            var device = new string(name).TrimEnd('\0').TrimEnd('\\');
            using var volume = CreateFileW(device, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (volume.IsInvalid)
            {
                return null;
            }
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = 7 /* StorageDeviceSeekPenaltyProperty */, QueryType = 0 };
            return DeviceIoControl(volume, 0x2D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, ref query, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                       out DEVICE_SEEK_PENALTY_DESCRIPTOR result, Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(), out _, IntPtr.Zero)
                ? result.IncursSeekPenalty
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string lpszFileName, [Out] char[] lpszVolumePathName, int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, [Out] char[] lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref STORAGE_PROPERTY_QUERY lpInBuffer, int nInBufferSize,
        out DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
}
