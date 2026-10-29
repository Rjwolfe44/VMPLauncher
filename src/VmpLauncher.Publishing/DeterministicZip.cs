using System.IO.Compression;
using System.Text;
using VmpLauncher.Core.Packs;

namespace VmpLauncher.Publishing;

/// <summary>
/// Zips a directory so that identical content always yields identical bytes: ordinal-sorted entries, fixed
/// timestamps, fixed compression level, forward slashes, no extra fields. Identical content ⇒ identical sha256,
/// so re-exporting an unchanged package is a no-op for publishing.
/// </summary>
public static class DeterministicZip
{
    private static readonly DateTimeOffset FixedStamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public sealed record Entry(string ZipPath, string SourceFile, long Size);

    /// <summary>Zips <paramref name="sourceDir"/> under <paramref name="rootInZip"/> (e.g. "GameData/VladMultiplayer"), skipping paths matched by <paramref name="exclude"/> (relative to sourceDir).</summary>
    public static IReadOnlyList<Entry> Create(string sourceDir, string rootInZip, string outputZip, IEnumerable<string> exclude)
    {
        var excludeMatcher = new GlobMatcher(exclude);
        var entries = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(sourceDir, f).Replace('\\', '/'), File: f))
            .Where(x => !excludeMatcher.IsMatch(x.Rel))
            .OrderBy(x => x.Rel, StringComparer.Ordinal)
            .Select(x => new Entry($"{rootInZip.TrimEnd('/')}/{x.Rel}", x.File, new FileInfo(x.File).Length))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZip))!);
        var tmp = outputZip + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
        {
            foreach (var e in entries)
            {
                var entry = zip.CreateEntry(e.ZipPath, CompressionLevel.Optimal);
                entry.LastWriteTime = FixedStamp;
                using var dst = entry.Open();
                using var src = File.OpenRead(e.SourceFile);
                src.CopyTo(dst);
            }
        }
        File.Move(tmp, outputZip, overwrite: true);
        return entries;
    }
}
