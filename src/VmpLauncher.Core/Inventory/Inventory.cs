using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Inventory;

public enum FileClass
{
    /// <summary>Immutable, owned by a CKAN module.</summary>
    Owned,
    /// <summary>Owned but legitimately mutated at runtime (PluginData); drift is informational.</summary>
    MutableOwned,
    /// <summary>Produced by an override entry.</summary>
    Override,
    /// <summary>Owned by a custom package (VMP, Apex). Verified like Owned.</summary>
    Custom,
}

/// <summary>One installed file. Short property names keep the gzipped inventory small (25k rows ≈ 1 MB).</summary>
public sealed record InventoryEntry
{
    [JsonPropertyName("p")] public required string Path { get; init; }
    [JsonPropertyName("s")] public required long Size { get; init; }
    [JsonPropertyName("h")] public required string Sha256 { get; init; }
    [JsonPropertyName("o")] public required string Owner { get; init; }
    [JsonPropertyName("c")] public FileClass Class { get; init; } = FileClass.Owned;
}

public sealed record InventoryDocument
{
    public string Schema { get; init; } = "vmpack-inventory/1";
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public required IReadOnlyList<InventoryEntry> Files { get; init; }
}

public static class InventoryFile
{
    private static readonly JsonSerializerOptions Options = new(LauncherJson.Compact)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Writes the gzipped inventory and returns (sha256 of the gz bytes, file count, total bytes, max path length).</summary>
    public static (string Sha256, int FileCount, long TotalBytes, int MaxRelPathLength) Write(string path, InventoryDocument doc)
    {
        var sorted = doc with { Files = doc.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList() };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            JsonSerializer.Serialize(gz, sorted, Options);
        }
        var bytes = ms.ToArray();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return (Sha256Util.HashBytes(bytes), sorted.Files.Count, sorted.Files.Sum(f => f.Size), sorted.Files.Count == 0 ? 0 : sorted.Files.Max(f => f.Path.Length));
    }

    public static InventoryDocument Read(string path, string? expectedSha256 = null)
    {
        var bytes = File.ReadAllBytes(path);
        if (expectedSha256 is not null && !Sha256Util.HexEquals(Sha256Util.HashBytes(bytes), expectedSha256))
        {
            throw new InvalidDataException($"Inventory {path} does not match the hash recorded in the lock.");
        }
        using var ms = new MemoryStream(bytes);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var bounded = new BoundedReadStream(gz, LauncherJson.MaxInventoryBytes);
        return JsonSerializer.Deserialize<InventoryDocument>(bounded, Options) ?? throw new InvalidDataException("Empty inventory.");
    }

    /// <summary>Stops a decompression bomb before it fills memory.</summary>
    private sealed class BoundedReadStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > max)
            {
                throw new InvalidDataException($"Inventory exceeds {max} bytes when decompressed.");
            }
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
