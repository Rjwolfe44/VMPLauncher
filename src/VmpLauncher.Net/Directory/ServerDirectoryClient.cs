using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;

namespace VmpLauncher.Net.Directory;

/// <param name="Directory">The verified server list.</param>
/// <param name="IsFromCache">True when the network could not provide a fresh copy and the last verified cache was used.</param>
/// <param name="FetchedAt">When this content was last confirmed from the network (200 or 304).</param>
/// <param name="Age">Now minus <paramref name="FetchedAt"/>.</param>
/// <param name="KeyId">Signing key id reported by the verifier.</param>
/// <param name="Warning">Plain-language note when a refresh failed or was refused.</param>
public sealed record ServerDirectoryResult(ServerDirectory Directory, bool IsFromCache, DateTimeOffset FetchedAt, TimeSpan Age, string? KeyId, string? Warning = null);

/// <summary>
/// Downloads <c>servers.json</c> + detached <c>.sig</c>, verifies them, enforces monotonically increasing
/// <c>sequence</c> (anti-rollback) and keeps the last verified copy at <c>&lt;dataRoot&gt;\cache\servers\&lt;packId&gt;.json</c>.
/// The highest sequence ever accepted is stored on its own (<c>&lt;packId&gt;.json.seq</c>), so rollback protection
/// survives a cached copy that no longer verifies (torn write, rotated key, tampering).
/// </summary>
public sealed class ServerDirectoryClient
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int MaxSignatureBytes = 64 * 1024;
    private const string CacheBusterParameter = "vmpl";

    private const string SignatureRetryFailedWhy =
        "the signature didn't match even after a retry, so the server list may be mid-update or tampered";

    /// <summary>
    /// The Play page and the Doctor each build their own client for the same pack. Every read or write of a pack's cache
    /// files goes through the lock for that pack's cache path, so one client never sees (or leaves) a half-written pair.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> CacheLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly ISignedDocumentVerifier _verifier;
    private readonly TimeProvider _time;
    private readonly object _gate;

    public ServerDirectoryClient(LauncherPaths paths, string packId, HttpClient http, ISignedDocumentVerifier verifier, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        if (packId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || packId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("packId is not a safe file name.", nameof(packId));
        }
        PackId = packId;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _time = time ?? TimeProvider.System;
        CacheDirectory = Path.Combine(paths.CacheDir, "servers");
        _gate = CacheLocks.GetOrAdd(Path.GetFullPath(CacheFile), static _ => new object());
    }

    public string PackId { get; }
    public string CacheDirectory { get; }
    public string CacheFile => Path.Combine(CacheDirectory, PackId + ".json");
    public string SignatureFile => CacheFile + ".sig";
    public string ETagFile => CacheFile + ".etag";

    /// <summary>Highest sequence (and its document hash) this machine ever accepted for the pack; the anti-rollback floor.</summary>
    public string SequenceFile => CacheFile + ".seq";

    /// <summary>Last verified cached copy, re-verified from disk; null if absent, no longer valid, or older than the newest list ever accepted.</summary>
    public ServerDirectoryResult? LoadCached()
    {
        var cached = LoadState().Usable;
        return cached is null ? null : ToResult(cached.Doc, true, cached.KeyId, null);
    }

    public async Task<ServerDirectoryResult> FetchAsync(Uri documentUri, Uri? signatureUri = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        signatureUri ??= new Uri(documentUri.AbsoluteUri + ".sig");
        var state = LoadState();
        var signatureRetryFailed = false;
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
                return ToResult(fresh.Doc, false, fresh.KeyId, null);
            }
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimitedAsync(response.Content, ServerDirectory.MaxBytes, timeout.Token).ConfigureAwait(false);
            var etag = response.Headers.ETag?.ToString();
            var signature = Encoding.UTF8.GetString(await GetLimitedAsync(signatureUri, MaxSignatureBytes, timeout.Token).ConfigureAwait(false));

            if (!_verifier.Verify(bytes, signature, out var keyId))
            {
                // raw.githubusercontent.com caches each URL for minutes, so right after a push the list and its .sig can come
                // from different generations. Ask for both again with the same unique query string, which bypasses that cache.
                var token = _time.GetUtcNow().Ticks.ToString(CultureInfo.InvariantCulture);
                (bytes, etag) = await GetWithETagAsync(WithCacheBuster(documentUri, token), ServerDirectory.MaxBytes, timeout.Token).ConfigureAwait(false);
                (var sigBytes, _) = await GetWithETagAsync(WithCacheBuster(signatureUri, token), MaxSignatureBytes, timeout.Token).ConfigureAwait(false);
                signature = Encoding.UTF8.GetString(sigBytes);
                if (!_verifier.Verify(bytes, signature, out keyId))
                {
                    signatureRetryFailed = true;
                    throw new LauncherException(ErrorCode.Tampered,
                        "The server list's signature didn't match even after a retry. The list may be mid-update or tampered with, so it was refused; try again in a few minutes.",
                        RecoveryHint.AskHost);
                }
            }

            var doc = ParseChecked(bytes);
            Commit(doc, bytes, signature, etag);
            return ToResult(doc, false, keyId, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or LauncherException
                                   || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var why = e switch
            {
                OperationCanceledException => $"the download timed out after {RequestTimeout.TotalSeconds:0} s",
                LauncherException when signatureRetryFailed => SignatureRetryFailedWhy,
                LauncherException le => le.Message,
                HttpRequestException he when he.StatusCode is { } sc => $"the server answered {(int)sc} {sc}",
                _ => e.Message,
            };
            // Re-read: another client may have stored a newer verified copy while this download was running.
            if ((LoadState().Usable ?? state.Usable) is { } c)
            {
                return ToResult(c.Doc, true, c.KeyId, $"Couldn't refresh the server list ({why}); using the saved copy.");
            }
            if (e is LauncherException)
            {
                throw;
            }
            throw new LauncherException(ErrorCode.ArtifactUnavailable,
                $"Couldn't download the server list: {why}." + (state.Problem is null ? string.Empty : $" The saved copy is unusable: {state.Problem}"),
                RecoveryHint.Retry, e);
        }
    }

    /// <summary>Verifies the detached signature over the exact bytes, then parses strictly and checks the pack id.</summary>
    public (ServerDirectory Doc, string? KeyId) VerifyAndParse(ReadOnlySpan<byte> bytes, string signatureJson)
    {
        if (!_verifier.Verify(bytes, signatureJson, out var keyId))
        {
            throw new LauncherException(ErrorCode.Tampered, "The server list's signature is not valid. It was refused.", RecoveryHint.AskHost);
        }
        return (ParseChecked(bytes), keyId);
    }

    private ServerDirectory ParseChecked(ReadOnlySpan<byte> bytes)
    {
        var doc = ServerDirectory.Parse(bytes);
        if (!string.Equals(doc.PackId, PackId, StringComparison.Ordinal))
        {
            throw new LauncherException(ErrorCode.Tampered, $"The server list is for pack '{doc.PackId}', not '{PackId}'. It was refused.", RecoveryHint.AskHost);
        }
        return doc;
    }

    /// <summary>Appends a <c>vmpl=&lt;token&gt;</c> query parameter (keeping any existing query) so CDNs treat the URL as new.</summary>
    internal static Uri WithCacheBuster(Uri uri, string token)
    {
        var withoutFragment = uri.GetLeftPart(UriPartial.Query);
        return new Uri(withoutFragment + (uri.Query.Length == 0 ? "?" : "&") + CacheBusterParameter + "=" + token);
    }

    private ServerDirectoryResult ToResult(ServerDirectory doc, bool fromCache, string? keyId, string? warning)
    {
        var fetchedAt = File.Exists(CacheFile) ? new DateTimeOffset(File.GetLastWriteTimeUtc(CacheFile), TimeSpan.Zero) : _time.GetUtcNow();
        var age = _time.GetUtcNow() - fetchedAt;
        return new ServerDirectoryResult(doc, fromCache, fetchedAt, age < TimeSpan.Zero ? TimeSpan.Zero : age, keyId, warning);
    }

    private sealed record CachedCopy(ServerDirectory Doc, byte[] Bytes, string? KeyId);

    /// <summary>Document sequence and the SHA-256 of its exact bytes (null when only the number is known).</summary>
    private readonly record struct Accepted(long Sequence, string? Sha256);

    /// <param name="Verified">The cached pair, when it still verifies (regardless of age).</param>
    /// <param name="Problem">Why the cached pair is unusable, when it exists but does not verify.</param>
    /// <param name="Highest">The rollback floor: the newest list accepted so far, from the sequence file and/or the verified cache.</param>
    /// <param name="ETag">Validator saved with the cached pair.</param>
    private sealed record CacheState(CachedCopy? Verified, string? Problem, Accepted? Highest, string? ETag)
    {
        /// <summary>The verified copy, unless it is older than a list that was accepted before (a rolled-back cache is not trusted).</summary>
        public CachedCopy? Usable => Verified is { } v && (Highest is not { } h || v.Doc.Sequence >= h.Sequence) ? v : null;
    }

    private CacheState LoadState()
    {
        lock (_gate)
        {
            var verified = TryLoadCache(out var problem);
            var highest = ReadAccepted();
            if (verified is not null)
            {
                var hash = Sha256Util.HashBytes(verified.Bytes);
                if (highest is not { } h || verified.Doc.Sequence > h.Sequence)
                {
                    highest = new Accepted(verified.Doc.Sequence, hash);
                }
                else if (verified.Doc.Sequence == h.Sequence && h.Sha256 is null)
                {
                    highest = h with { Sha256 = hash };
                }
            }
            return new CacheState(verified, problem, highest, verified is null ? null : ReadSmallText(ETagFile));
        }
    }

    private CachedCopy? TryLoadCache(out string? problem)
    {
        problem = null;
        if (!File.Exists(CacheFile) || !File.Exists(SignatureFile))
        {
            return null;
        }
        try
        {
            var bytes = File.ReadAllBytes(CacheFile);
            var sig = File.ReadAllText(SignatureFile);
            var (doc, keyId) = VerifyAndParse(bytes, sig);
            return new CachedCopy(doc, bytes, keyId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LauncherException)
        {
            problem = e.Message;
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
            return null; // unreadable: fall back to whatever the verified cache says rather than locking the list out forever
        }
        var sha = parts.Length > 1 && parts[1].Length == 64 && parts[1].All(Uri.IsHexDigit) ? parts[1].ToLowerInvariant() : null;
        return new Accepted(sequence, sha);
    }

    /// <summary>Anti-rollback: the new list must be newer than the floor, or byte-identical to the list that set it.</summary>
    private static LauncherException? RollbackProblem(ServerDirectory doc, byte[] bytes, Accepted? floor)
    {
        if (floor is not { } f)
        {
            return null;
        }
        if (doc.Sequence < f.Sequence || (doc.Sequence == f.Sequence && f.Sha256 is not null && !string.Equals(f.Sha256, Sha256Util.HashBytes(bytes), StringComparison.Ordinal)))
        {
            return new LauncherException(ErrorCode.Tampered,
                $"The downloaded server list (sequence {doc.Sequence}) is not newer than the one already trusted (sequence {f.Sequence}). It was refused.",
                RecoveryHint.AskHost);
        }
        return null;
    }

    private void Commit(ServerDirectory doc, byte[] bytes, string signature, string? etag)
    {
        lock (_gate)
        {
            // Check against the state as it is now: another client may have stored a newer list while this one downloaded.
            // (Monitor locks are re-entrant, so LoadState can take the same lock again.)
            if (RollbackProblem(doc, bytes, LoadState().Highest) is { } refused)
            {
                throw refused;
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
            WriteAtomic(SequenceFile, Encoding.ASCII.GetBytes(FormattableString.Invariant($"{doc.Sequence} {Sha256Util.HashBytes(bytes)}")));
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

    /// <summary>Unique temp name + move, so concurrent writers (even in other processes) never share a partially written temp file.</summary>
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

    private async Task<byte[]> GetLimitedAsync(Uri uri, int maxBytes, CancellationToken ct)
        => (await GetWithETagAsync(uri, maxBytes, ct).ConfigureAwait(false)).Bytes;

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
