using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Net.Directory;

namespace VmpLauncher.Net.Library;

/// <param name="Index">The verified library.</param>
/// <param name="IsFromCache">True when the network could not provide a fresh copy and the last verified cache was used.</param>
/// <param name="FetchedAt">When this content was last confirmed from the network (200 or 304).</param>
/// <param name="KeyId">Signing key id reported by the verifier.</param>
/// <param name="Warning">Plain-language note when a refresh failed or was refused.</param>
public sealed record LibraryResult(LibraryIndex Index, bool IsFromCache, DateTimeOffset FetchedAt, string? KeyId, string? Warning = null);

/// <summary>
/// Downloads <c>library.json</c> + detached <c>.sig</c>, verifies them against the pinned publisher key, enforces a
/// non-decreasing <c>sequence</c> (anti-rollback) and keeps the last verified copy under <c>cache\library</c>. Works exactly
/// like <see cref="ServerDirectoryClient"/>, which has been through the same failure modes (CDN generations that disagree,
/// torn writes, rolled-back caches).
/// </summary>
public sealed class LibraryClient
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const int MaxSignatureBytes = 64 * 1024;
    private const string CacheBusterParameter = "vmpl";
    private static readonly ConcurrentDictionary<string, object> CacheLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly ISignedDocumentVerifier _verifier;
    private readonly TimeProvider _time;
    private readonly object _gate;

    public LibraryClient(LauncherPaths paths, HttpClient http, ISignedDocumentVerifier verifier, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _time = time ?? TimeProvider.System;
        CacheDirectory = Path.Combine(paths.CacheDir, "library");
        _gate = CacheLocks.GetOrAdd(Path.GetFullPath(CacheFile), static _ => new object());
    }

    public string CacheDirectory { get; }
    public string CacheFile => Path.Combine(CacheDirectory, LibraryIndex.VladLibraryId + ".json");
    public string SignatureFile => CacheFile + ".sig";
    public string ETagFile => CacheFile + ".etag";

    /// <summary>Highest sequence (and its document hash) this machine ever accepted: the anti-rollback floor.</summary>
    public string SequenceFile => CacheFile + ".seq";

    /// <summary>The last verified cached copy, re-verified from disk; null if absent, no longer valid, or older than the newest ever accepted.</summary>
    public LibraryResult? LoadCached()
    {
        var cached = LoadState().Usable;
        return cached is null ? null : ToResult(cached.Index, true, cached.KeyId, null);
    }

    public async Task<LibraryResult> FetchAsync(Uri documentUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        var signatureUri = new Uri(documentUri.AbsoluteUri + ".sig");
        var state = LoadState();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, documentUri);
            if (state.Usable is not null && state.ETag is not null && EntityTagHeaderValue.TryParse(state.ETag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && state.Usable is { } fresh)
            {
                Touch();
                return ToResult(fresh.Index, false, fresh.KeyId, null);
            }
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimitedAsync(response.Content, LibraryIndex.MaxBytes, timeout.Token).ConfigureAwait(false);
            var etag = response.Headers.ETag?.ToString();
            var (firstSignature, _) = await GetWithETagAsync(signatureUri, MaxSignatureBytes, timeout.Token).ConfigureAwait(false);
            var signature = Encoding.UTF8.GetString(firstSignature);

            if (!_verifier.Verify(bytes, signature, out var keyId))
            {
                // raw.githubusercontent.com caches each URL for minutes, so right after a push the file and its .sig can come
                // from different generations. Ask for both again with the same unique query string, which bypasses that cache.
                var token = _time.GetUtcNow().Ticks.ToString(CultureInfo.InvariantCulture);
                (bytes, etag) = await GetWithETagAsync(ServerDirectoryClient.WithCacheBuster(documentUri, token), LibraryIndex.MaxBytes, timeout.Token).ConfigureAwait(false);
                (var sigBytes, _) = await GetWithETagAsync(ServerDirectoryClient.WithCacheBuster(signatureUri, token), MaxSignatureBytes, timeout.Token).ConfigureAwait(false);
                signature = Encoding.UTF8.GetString(sigBytes);
                if (!_verifier.Verify(bytes, signature, out keyId))
                {
                    throw new LauncherException(ErrorCode.Tampered,
                        "The mod library's signature didn't match even after a retry. It may be mid-update or tampered with, so it was refused; try again in a few minutes.",
                        RecoveryHint.AskHost);
                }
            }

            var index = ParseChecked(bytes);
            Commit(index, bytes, signature, etag);
            return ToResult(index, false, keyId, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or LauncherException
                                   || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var why = e switch
            {
                OperationCanceledException => $"the download timed out after {RequestTimeout.TotalSeconds:0} s",
                LauncherException le => le.Message,
                HttpRequestException he when he.StatusCode is { } sc => $"GitHub answered {(int)sc} {sc}",
                _ => e.Message,
            };
            if ((LoadState().Usable ?? state.Usable) is { } c)
            {
                return ToResult(c.Index, true, c.KeyId, $"Couldn't refresh Vlad's mods ({why}); using the saved list.");
            }
            if (e is LauncherException)
            {
                throw;
            }
            throw new LauncherException(ErrorCode.ArtifactUnavailable, $"Couldn't download Vlad's mods: {why}.", RecoveryHint.Retry, e);
        }
    }

    /// <summary>Reads a library from a folder holding <c>library.json</c> and <c>library.json.sig</c> (the host's own publish output). Same checks, no cache.</summary>
    public LibraryResult LoadFromFolder(string folder)
    {
        var bytes = File.ReadAllBytes(Path.Combine(folder, "library.json"));
        var sig = File.ReadAllText(Path.Combine(folder, "library.json.sig"));
        var (index, keyId) = VerifyAndParse(bytes, sig);
        return new LibraryResult(index, false, _time.GetUtcNow(), keyId);
    }

    /// <summary>Verifies the detached signature over the exact bytes, then parses strictly and checks the library id.</summary>
    public (LibraryIndex Index, string? KeyId) VerifyAndParse(ReadOnlySpan<byte> bytes, string signatureJson)
    {
        if (!_verifier.Verify(bytes, signatureJson, out var keyId))
        {
            throw new LauncherException(ErrorCode.Tampered, "The mod library's signature is not valid. It was refused.", RecoveryHint.AskHost);
        }
        return (ParseChecked(bytes), keyId);
    }

    private static LibraryIndex ParseChecked(ReadOnlySpan<byte> bytes)
    {
        var index = LibraryIndex.Parse(bytes);
        if (!string.Equals(index.LibraryId, LibraryIndex.VladLibraryId, StringComparison.Ordinal))
        {
            throw new LauncherException(ErrorCode.Tampered, $"The mod library is '{index.LibraryId}', not '{LibraryIndex.VladLibraryId}'. It was refused.", RecoveryHint.AskHost);
        }
        return index;
    }

    private LibraryResult ToResult(LibraryIndex index, bool fromCache, string? keyId, string? warning)
    {
        var fetchedAt = File.Exists(CacheFile) ? new DateTimeOffset(File.GetLastWriteTimeUtc(CacheFile), TimeSpan.Zero) : _time.GetUtcNow();
        return new LibraryResult(index, fromCache, fetchedAt, keyId, warning);
    }

    private sealed record CachedCopy(LibraryIndex Index, byte[] Bytes, string? KeyId);

    private readonly record struct Accepted(long Sequence, string? Sha256);

    private sealed record CacheState(CachedCopy? Verified, Accepted? Highest, string? ETag)
    {
        public CachedCopy? Usable => Verified is { } v && (Highest is not { } h || v.Index.Sequence >= h.Sequence) ? v : null;
    }

    private CacheState LoadState()
    {
        lock (_gate)
        {
            var verified = TryLoadCache();
            var highest = ReadAccepted();
            if (verified is not null)
            {
                var hash = Sha256Util.HashBytes(verified.Bytes);
                if (highest is not { } h || verified.Index.Sequence > h.Sequence)
                {
                    highest = new Accepted(verified.Index.Sequence, hash);
                }
                else if (verified.Index.Sequence == h.Sequence && h.Sha256 is null)
                {
                    highest = h with { Sha256 = hash };
                }
            }
            return new CacheState(verified, highest, verified is null ? null : ReadSmallText(ETagFile));
        }
    }

    private CachedCopy? TryLoadCache()
    {
        if (!File.Exists(CacheFile) || !File.Exists(SignatureFile))
        {
            return null;
        }
        try
        {
            var bytes = File.ReadAllBytes(CacheFile);
            var (index, keyId) = VerifyAndParse(bytes, File.ReadAllText(SignatureFile));
            return new CachedCopy(index, bytes, keyId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LauncherException)
        {
            return null;
        }
    }

    private Accepted? ReadAccepted()
    {
        var text = ReadSmallText(SequenceFile);
        if (text is null)
        {
            return null;
        }
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return null;
        }
        var sha = parts.Length > 1 && parts[1].Length == 64 && parts[1].All(Uri.IsHexDigit) ? parts[1].ToLowerInvariant() : null;
        return new Accepted(sequence, sha);
    }

    private void Commit(LibraryIndex index, byte[] bytes, string signature, string? etag)
    {
        lock (_gate)
        {
            var floor = LoadState().Highest;
            if (floor is { } f && (index.Sequence < f.Sequence
                                   || (index.Sequence == f.Sequence && f.Sha256 is not null && !string.Equals(f.Sha256, Sha256Util.HashBytes(bytes), StringComparison.Ordinal))))
            {
                throw new LauncherException(ErrorCode.Tampered,
                    $"The downloaded mod library (sequence {index.Sequence}) is not newer than the one already trusted (sequence {f.Sequence}). It was refused.", RecoveryHint.AskHost);
            }
            System.IO.Directory.CreateDirectory(CacheDirectory);
            WriteAtomic(SignatureFile, Encoding.UTF8.GetBytes(signature));
            WriteAtomic(CacheFile, bytes);
            if (etag is null)
            {
                File.Delete(ETagFile);
            }
            else
            {
                WriteAtomic(ETagFile, Encoding.UTF8.GetBytes(etag));
            }
            // Last, so a crash between the files only ever leaves the floor behind the cache, never ahead of it.
            WriteAtomic(SequenceFile, Encoding.ASCII.GetBytes(FormattableString.Invariant($"{index.Sequence} {Sha256Util.HashBytes(bytes)}")));
            File.SetLastWriteTimeUtc(CacheFile, _time.GetUtcNow().UtcDateTime);
        }
    }

    private void Touch()
    {
        lock (_gate)
        {
            try
            {
                File.SetLastWriteTimeUtc(CacheFile, _time.GetUtcNow().UtcDateTime);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only the "last confirmed" timestamp; the verified copy itself is fine.
            }
        }
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort; a stray uniquely-named .tmp is harmless.
            }
        }
    }

    private static string? ReadSmallText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<(byte[] Bytes, string? ETag)> GetWithETagAsync(Uri uri, int maxBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await ReadLimitedAsync(response.Content, maxBytes, ct).ConfigureAwait(false), response.Headers.ETag?.ToString());
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The download is larger than the {maxBytes}-byte limit.", RecoveryHint.AskHost);
        }
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                throw new LauncherException(ErrorCode.InvalidPack, $"The download is larger than the {maxBytes}-byte limit.", RecoveryHint.AskHost);
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
