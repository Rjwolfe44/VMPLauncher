using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Ckan.Backend;

/// <summary>
/// CKAN's cache only validates hashes when it stores a download; a later cache hit is just "a file with that
/// URL hash exists". This index remembers (size, mtime) → sha256 for archives we have verified ourselves,
/// so a trusted hit costs a stat, not a 100 MB hash.
/// </summary>
public sealed class VerifiedArtifactIndex
{
    private sealed class Entry
    {
        public long Size { get; set; }
        public long MtimeTicks { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class IndexFile
    {
        public Dictionary<string, Entry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _path;
    private readonly IndexFile _data;
    private readonly object _gate = new();
    private bool _dirty;

    public VerifiedArtifactIndex(string indexPath)
    {
        _path = indexPath;
        _data = File.Exists(indexPath) ? SafeRead(indexPath) : new IndexFile();
    }

    private static IndexFile SafeRead(string path)
    {
        try
        {
            return LauncherJson.ReadFile<IndexFile>(path, 64L * 1024 * 1024);
        }
        catch (Exception)
        {
            return new IndexFile();
        }
    }

    /// <summary>Returns the verified sha256 of the archive, hashing it only when the fingerprint changed.</summary>
    public string Sha256Of(string archivePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(archivePath);
        lock (_gate)
        {
            if (_data.Files.TryGetValue(fi.FullName, out var e) && e.Size == fi.Length && e.MtimeTicks == fi.LastWriteTimeUtc.Ticks)
            {
                return e.Sha256;
            }
        }
        var sha = Sha256Util.HashFile(archivePath, ct);
        lock (_gate)
        {
            _data.Files[fi.FullName] = new Entry { Size = fi.Length, MtimeTicks = fi.LastWriteTimeUtc.Ticks, Sha256 = sha };
            _dirty = true;
        }
        return sha;
    }

    public void Forget(string archivePath)
    {
        lock (_gate)
        {
            if (_data.Files.Remove(Path.GetFullPath(archivePath)))
            {
                _dirty = true;
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            if (_dirty)
            {
                LauncherJson.WriteFile(_path, _data);
                _dirty = false;
            }
        }
    }
}
