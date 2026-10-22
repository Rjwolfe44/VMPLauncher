using System.Buffers;
using System.Security.Cryptography;

namespace VmpLauncher.Core.Hashing;

public static class Sha256Util
{
    public const int BufferSize = 1024 * 1024;

    /// <summary>Lowercase hex SHA-256 of a file, streamed with a 1 MiB pooled buffer.</summary>
    public static string HashFile(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        return HashStream(fs, ct);
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return await HashStreamAsync(fs, ct).ConfigureAwait(false);
    }

    public static string HashStream(Stream stream, CancellationToken ct = default)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.AppendData(buffer, 0, read);
            }
            return ToHex(hasher.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<string> HashStreamAsync(Stream stream, CancellationToken ct = default)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }
            return ToHex(hasher.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Hash plus byte count in one pass (for zip entries whose declared size we do not trust).</summary>
    public static (string Sha256, long Length) HashStreamCounted(Stream stream, long maxBytes, CancellationToken ct = default)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += read;
                if (total > maxBytes)
                {
                    throw new InvalidDataException($"Stream exceeded the allowed {maxBytes} bytes.");
                }
                hasher.AppendData(buffer, 0, read);
            }
            return (ToHex(hasher.GetHashAndReset()), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes) => ToHex(SHA256.HashData(bytes));

    public static string HashUtf8(string text) => HashBytes(System.Text.Encoding.UTF8.GetBytes(text));

    public static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>VMP mod-control format: uppercase hex with dashes (BitConverter.ToString).</summary>
    public static string ToVmpDashHex(string lowercaseHex)
        => string.Join('-', Enumerable.Range(0, lowercaseHex.Length / 2).Select(i => lowercaseHex.Substring(i * 2, 2).ToUpperInvariant()));

    public static bool HexEquals(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
