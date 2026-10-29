using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Net.Library;

namespace VmpLauncher.Publishing.Library;

/// <summary>
/// Checks what friends will actually get: fetches <c>library.json</c> and its <c>.sig</c> fresh over HTTPS, verifies the
/// signature against the launcher's pinned keys, parses it with the launcher's own strict parser and confirms every download
/// answers. Used right after a publish (with retries, because raw.githubusercontent.com caches) and by <c>vmpl library verify-remote</c>.
/// </summary>
public sealed class LibraryRemoteVerifier
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/Rjwolfe44/VMP-Packs/main/library/library.json";

    public sealed record ModuleLine(string Identifier, string Version, string Download);

    public sealed record Result(
        long? Sequence,
        DateTimeOffset? IssuedAt,
        string? Sha256,
        IReadOnlyList<ModuleLine> Modules,
        IReadOnlyList<string> Lines,
        IReadOnlyList<string> Problems)
    {
        public bool Ok => Problems.Count == 0;
    }

    private readonly HttpClient _http;
    private readonly SignatureVerifier _verifier;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public LibraryRemoteVerifier(HttpClient? http = null, SignatureVerifier? verifier = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _verifier = verifier ?? SignatureVerifier.Default();
        _delay = delay ?? Task.Delay;
    }

    /// <summary>One fetch and check: the document, its signature, its sequence (when expected) and the reachability of every download.</summary>
    public async Task<Result> VerifyAsync(string url, long? expectedSequence = null, string? expectedSha256 = null, CancellationToken ct = default)
    {
        var check = await CheckDocumentAsync(url, expectedSequence, expectedSha256, ct).ConfigureAwait(false);
        if (check.Index is { } index)
        {
            await CheckDownloadsAsync(index, check.Lines, check.Problems, ct).ConfigureAwait(false);
        }
        return check.ToResult();
    }

    /// <summary>
    /// Like <see cref="VerifyAsync"/> but waits for the CDN: the document is fetched again until it is the expected one (or the
    /// attempts run out), then the downloads are checked once. Progress goes to <paramref name="log"/>.
    /// </summary>
    public async Task<Result> VerifyWithRetryAsync(string url, long expectedSequence, string expectedSha256, int attempts, TimeSpan interval, TextWriter? log = null, CancellationToken ct = default)
    {
        DocumentCheck check = await CheckDocumentAsync(url, expectedSequence, expectedSha256, ct).ConfigureAwait(false);
        for (var attempt = 1; !check.Ok && attempt < attempts; attempt++)
        {
            log?.WriteLine($"  remote check {attempt}/{attempts}: {check.Problems[0]}; trying again in {interval.TotalSeconds:0} s");
            await _delay(interval, ct).ConfigureAwait(false);
            check = await CheckDocumentAsync(url, expectedSequence, expectedSha256, ct).ConfigureAwait(false);
        }
        if (check.Index is { } index)
        {
            await CheckDownloadsAsync(index, check.Lines, check.Problems, ct).ConfigureAwait(false);
        }
        return check.ToResult();
    }

    private sealed class DocumentCheck
    {
        public long? Sequence { get; set; }
        public DateTimeOffset? IssuedAt { get; set; }
        public string? Sha256 { get; set; }
        public LibraryIndex? Index { get; set; }
        public List<string> Lines { get; } = new();
        public List<string> Problems { get; } = new();
        public bool Ok => Problems.Count == 0;

        public Result ToResult() => new(
            Sequence, IssuedAt, Sha256,
            Index is null ? Array.Empty<ModuleLine>() : Index.Modules.Select(m => new ModuleLine(m.Identifier, m.Version, DownloadOf(m))).ToList(),
            Lines, Problems);
    }

    private async Task<DocumentCheck> CheckDocumentAsync(string url, long? expectedSequence, string? expectedSha256, CancellationToken ct)
    {
        var check = new DocumentCheck();
        try
        {
            var bytes = await GetAsync(url, ct).ConfigureAwait(false);
            var sig = await TryGetAsync(url + ".sig", ct).ConfigureAwait(false);
            check.Sha256 = Sha256Util.HashBytes(bytes);
            check.Lines.Add($"library.json: {bytes.Length:N0} bytes");
            CheckSignature(bytes, sig, check);
            var index = LibraryIndex.Parse(bytes);
            check.Index = index;
            check.Sequence = index.Sequence;
            check.IssuedAt = index.IssuedAt;
            check.Lines.Add($"library: {index.Name} ({index.LibraryId}), sequence {index.Sequence}, issued {index.IssuedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}, {index.Modules.Count} module(s)");
            if (!string.Equals(index.LibraryId, LibraryIndex.VladLibraryId, StringComparison.Ordinal))
            {
                check.Problems.Add($"the library is '{index.LibraryId}', the launcher only accepts '{LibraryIndex.VladLibraryId}'");
            }
            if (expectedSequence is { } expected && index.Sequence != expected)
            {
                check.Problems.Add($"the published library has sequence {index.Sequence}, expected {expected} (CDN cache? wait a few minutes)");
            }
            else if (expectedSha256 is not null && !Sha256Util.HexEquals(check.Sha256, expectedSha256))
            {
                check.Problems.Add("the published library.json is not the file that was signed and pushed (CDN cache? wait a few minutes)");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LauncherException or JsonException or InvalidOperationException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }
            check.Problems.Add($"remote check failed for {url}: {ex.Message}");
        }
        return check;
    }

    private void CheckSignature(byte[] document, byte[]? sig, DocumentCheck check)
    {
        if (sig is null)
        {
            check.Problems.Add("library.json: no .sig next to it");
            return;
        }
        SignatureEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignatureEnvelope>(sig, LauncherJson.Pretty);
        }
        catch (JsonException)
        {
            check.Problems.Add("library.json: .sig is not a signature envelope");
            return;
        }
        var trust = _verifier.Verify(document, envelope, out var keyId, out var reason);
        if (trust == TrustLevel.VerifiedPublisher)
        {
            check.Lines.Add($"library.json: {SignatureVerifier.DescribeTrust(trust)} ({keyId})");
        }
        else
        {
            check.Problems.Add($"library.json: {SignatureVerifier.DescribeTrust(trust)}{(reason is null ? string.Empty : " - " + reason)}");
        }
    }

    private async Task CheckDownloadsAsync(LibraryIndex index, List<string> lines, List<string> problems, CancellationToken ct)
    {
        foreach (var m in index.Modules)
        {
            var url = DownloadOf(m);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    lines.Add($"download: {m.Identifier} {m.Version} reachable ({resp.Content.Headers.ContentLength?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} bytes)");
                }
                else
                {
                    problems.Add($"download for {m.Identifier} {m.Version} not reachable: {url} ({(int)resp.StatusCode})");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                problems.Add($"download for {m.Identifier} {m.Version} not reachable: {url} ({ex.Message})");
            }
        }
    }

    private static string DownloadOf(LibraryModule m)
        => m.Json.TryGetProperty("download", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;

    private async Task<byte[]> GetAsync(string url, CancellationToken ct)
    {
        // raw.githubusercontent.com caches for a few minutes; a unique query string makes the CDN fetch the current file.
        var busted = url + (url.Contains('?') ? "&" : "?") + "vmpl=" + DateTimeOffset.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        using var req = new HttpRequestMessage(HttpMethod.Get, busted);
        req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var length = resp.Content.Headers.ContentLength;
        if (length > LibraryIndex.MaxBytes)
        {
            throw new InvalidOperationException($"{url} is {length} bytes; the library limit is {LibraryIndex.MaxBytes}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> TryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            return await GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
