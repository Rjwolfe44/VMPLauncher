using System.IO.Compression;
using Encoding = System.Text.Encoding;
using CKAN;

namespace VmpLauncher.Ckan.Repo;

/// <summary>
/// Builds a CKAN repository archive (a zip of <c>*.ckan</c> JSON files) containing exactly the given modules.
/// Registered as a profile's <b>only</b> repository, it makes the CKAN resolver unable to pick any version
/// other than the locked ones. The archive is deterministic: sorted entries, fixed timestamps.
/// </summary>
public static class PackRepoBuilder
{
    private static readonly DateTime FixedStamp = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string Build(IEnumerable<CkanModule> modules, string outputZipPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath)!);
        var entries = modules
            .Select(m => (Name: EntryName(m), Json: m.ToJson()))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var tmp = outputZipPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
        {
            foreach (var (name, json) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = FixedStamp;
                using var s = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(json);
                s.Write(bytes, 0, bytes.Length);
            }
        }
        File.Move(tmp, outputZipPath, overwrite: true);
        return outputZipPath;
    }

    /// <summary>CKAN-meta layout: identifier/identifier-version.ckan. Epoch ':' is illegal in file names.</summary>
    public static string EntryName(CkanModule m)
    {
        var ver = m.version.ToString().Replace(':', '_');
        return $"{m.identifier}/{m.identifier}-{ver}.ckan";
    }
}
