using System.IO.Compression;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;

namespace VmpLauncher.Core.Security;

/// <summary>
/// Guards for archives that come from packs or private sideloads: bounded entry count and expansion, no
/// symlinks, no case-duplicate entries, no encryption, safe entry names. Also a bounded single-entry extractor.
/// </summary>
public static class SafeZip
{
    public const int MaxEntries = 100_000;
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxCompressionRatio = 200;

    public sealed record ScanResult(int Entries, long DeclaredBytes);

    public static ScanResult Scan(string zipPath, long? declaredInstallSize = null)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new LauncherException(ErrorCode.Tampered, $"{Path.GetFileName(zipPath)} has {zip.Entries.Count} entries; the limit is {MaxEntries}.");
        }
        var limit = declaredInstallSize is { } d ? Math.Min(MaxTotalBytes, 4 * d + 64L * 1024 * 1024) : MaxTotalBytes;
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
            {
                continue;
            }
            SafePath.Normalize(e.FullName, maxLength: 400);
            // Unix mode in the high 16 bits of ExternalAttributes: S_IFLNK = 0xA000
            var unixMode = (e.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
            {
                throw new LauncherException(ErrorCode.Tampered, $"{Path.GetFileName(zipPath)} contains a symbolic link entry ({e.FullName}).");
            }
            if (!seen.Add(e.FullName.Replace('\\', '/')))
            {
                throw new LauncherException(ErrorCode.Tampered, $"{Path.GetFileName(zipPath)} contains duplicate entry {e.FullName} (case-insensitive).");
            }
            if (e.CompressedLength > 0 && e.Length / Math.Max(1, e.CompressedLength) > MaxCompressionRatio && e.Length > 1024 * 1024)
            {
                throw new LauncherException(ErrorCode.Tampered, $"{Path.GetFileName(zipPath)} entry {e.FullName} has a suspicious compression ratio.");
            }
            total += e.Length;
            if (total > limit)
            {
                throw new LauncherException(ErrorCode.Tampered, $"{Path.GetFileName(zipPath)} would expand to more than {limit:N0} bytes.");
            }
        }
        return new ScanResult(zip.Entries.Count, total);
    }

    /// <summary>
    /// Extracts one entry to a temp file next to the destination, verifying the hash before the rename.
    /// A negative <paramref name="expectedSize"/> means the size is not known; the hash alone decides.
    /// </summary>
    public static void ExtractEntryVerified(string zipPath, string entryName, string destination, string expectedSha256, long expectedSize)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), entryName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    ?? throw new LauncherException(ErrorCode.ArtifactCorrupt, $"Entry {entryName} not found in {Path.GetFileName(zipPath)}.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".vmpl-tmp";
        var sizeKnown = expectedSize >= 0;
        try
        {
            using (var src = entry.Open())
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                CopyBounded(src, dst, sizeKnown ? expectedSize + 1 : Math.Min(entry.Length, MaxTotalBytes) + 1);
            }
            var fi = new FileInfo(tmp);
            var sha = Sha256Util.HashFile(tmp);
            if ((sizeKnown && fi.Length != expectedSize) || !Sha256Util.HexEquals(sha, expectedSha256))
            {
                throw new LauncherException(ErrorCode.ArtifactCorrupt, $"{entryName} in {Path.GetFileName(zipPath)} does not match the pack's hash.");
            }
            File.Move(tmp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
    }

    private static void CopyBounded(Stream src, Stream dst, long maxBytes)
    {
        var buffer = new byte[256 * 1024];
        long total = 0;
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new LauncherException(ErrorCode.Tampered, "Archive entry is larger than declared.");
            }
            dst.Write(buffer, 0, read);
        }
    }
}
