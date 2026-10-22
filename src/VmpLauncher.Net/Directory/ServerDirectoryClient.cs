using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;

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
/// </summary>
public sealed class ServerDirectoryClient
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int MaxSignatureBytes = 64 * 1024;

    private readonly HttpClient _http;
    private readonly ISignedDocumentVerifier _verifier;
    private readonly TimeProvider _time;

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
    }

    public string PackId { get; }
    public string CacheDirectory { get; }
    public string CacheFile => Path.Combine(CacheDirectory, PackId + ".json");
    public string SignatureFile => CacheFile + ".sig";
    public string ETagFile => CacheFile + ".etag";

    /// <summary>Last verified cached copy, re-verified from disk; null if absent or no longer valid.</summary>
    public ServerDirectoryResult? LoadCached()
    {
        var cached = TryLoadCache(out _);
        return cached is null ? null : ToResult(cached.Value.Doc, true, cached.Value.KeyId, null);
    }

    public async Task<ServerDirectoryResult> FetchAsync(Uri documentUri, Uri? signatureUri = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        signatureUri ??= new Uri(documentUri.AbsoluteUri + ".sig");
        var cached = TryLoadCache(out var cacheProblem);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, documentUri);
            var etag = cached is null ? null : ReadSmallText(ETagFile);
            if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                File.SetLastWriteTimeUtc(CacheFile, _time.GetUtcNow().UtcDateTime);
                return ToResult(cached.Value.Doc, false, cached.Value.KeyId, null);
            }
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimitedAsync(response.Content, ServerDirectory.MaxBytes, timeout.Token).ConfigureAwait(false);
            var sigBytes = await GetLimitedAsync(signatureUri, MaxSignatureBytes, timeout.Token).ConfigureAwait(false);
            var signature = Encoding.UTF8.GetString(sigBytes);

            var (doc, keyId) = VerifyAndParse(bytes, signature);
            if (cached is { } c)
            {
                if (doc.Sequence < c.Doc.Sequence || (doc.Sequence == c.Doc.Sequence && !bytes.AsSpan().SequenceEqual(c.Bytes)))
                {
                    throw new LauncherException(ErrorCode.Tampered,
                        $"The downloaded server list (sequence {doc.Sequence}) is not newer than the one already trusted (sequence {c.Doc.Sequence}). It was refused.",
                        RecoveryHint.AskHost);
                }
            }
            WriteCache(bytes, signature, response.Headers.ETag?.ToString());
            return ToResult(doc, false, keyId, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or LauncherException
                                   || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var why = e switch
            {
                OperationCanceledException => $"the download timed out after {RequestTimeout.TotalSeconds:0} s",
                LauncherException le => le.Message,
                HttpRequestException he when he.StatusCode is { } sc => $"the server answered {(int)sc} {sc}",
                _ => e.Message,
            };
            if (cached is not null)
            {
                return ToResult(cached.Value.Doc, true, cached.Value.KeyId, $"Couldn't refresh the server list ({why}); using the saved copy.");
            }
            if (e is LauncherException)
            {
                throw;
            }
            throw new LauncherException(ErrorCode.ArtifactUnavailable,
                $"Couldn't download the server list: {why}." + (cacheProblem is null ? string.Empty : $" The saved copy is unusable: {cacheProblem}"),
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
        var doc = ServerDirectory.Parse(bytes);
        if (!string.Equals(doc.PackId, PackId, StringComparison.Ordinal))
        {
            throw new LauncherException(ErrorCode.Tampered, $"The server list is for pack '{doc.PackId}', not '{PackId}'. It was refused.", RecoveryHint.AskHost);
        }
        return (doc, keyId);
    }

    private ServerDirectoryResult ToResult(ServerDirectory doc, bool fromCache, string? keyId, string? warning)
    {
        var fetchedAt = File.Exists(CacheFile) ? new DateTimeOffset(File.GetLastWriteTimeUtc(CacheFile), TimeSpan.Zero) : _time.GetUtcNow();
        var age = _time.GetUtcNow() - fetchedAt;
        return new ServerDirectoryResult(doc, fromCache, fetchedAt, age < TimeSpan.Zero ? TimeSpan.Zero : age, keyId, warning);
    }

    private (ServerDirectory Doc, byte[] Bytes, string? KeyId)? TryLoadCache(out string? problem)
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
            return (doc, bytes, keyId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LauncherException)
        {
            problem = e.Message;
            return null;
        }
    }

    private void WriteCache(byte[] bytes, string signature, string? etag)
    {
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
        File.SetLastWriteTimeUtc(CacheFile, _time.GetUtcNow().UtcDateTime);
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static string? ReadSmallText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<byte[]> GetLimitedAsync(Uri uri, int maxBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response.Content, maxBytes, ct).ConfigureAwait(false);
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
