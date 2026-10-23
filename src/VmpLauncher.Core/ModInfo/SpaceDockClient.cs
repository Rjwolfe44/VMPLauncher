using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VmpLauncher.Core.ModInfo;

/// <summary>What SpaceDock knows about one mod, ready for a store-style page. Every optional field may be null or empty.</summary>
/// <param name="Description">The author's description as Markdown (feed it to <see cref="MarkdownLite"/>).</param>
/// <param name="HeaderImage">The https header image, with relative paths already resolved against spacedock.info.</param>
/// <param name="Versions">Newest first, as SpaceDock returns them.</param>
/// <param name="FetchedUtc">When this data came from SpaceDock (older than now when served from the cache).</param>
public sealed record ModInfo(
    int SpaceDockId,
    string Name,
    string? ShortDescription,
    string? Description,
    int Downloads,
    int Followers,
    string? Author,
    IReadOnlyList<string> SharedAuthors,
    Uri? HeaderImage,
    string? License,
    Uri? Website,
    Uri? Donations,
    Uri? SourceCode,
    Uri PageUrl,
    IReadOnlyList<ModInfoVersion> Versions,
    DateTimeOffset FetchedUtc);

/// <summary>One release of a mod on SpaceDock.</summary>
public sealed record ModInfoVersion(string FriendlyVersion, string? GameVersion, DateTimeOffset? Created, string? Changelog, int Downloads, Uri? DownloadUrl);

/// <summary>
/// Fetches mod pictures, descriptions and stats from SpaceDock (<c>/api/mod/{id}</c>) and keeps them on disk: mod data
/// under <c>spacedock\</c> (reused until it is older than <c>maxAge</c>, and served stale when SpaceDock cannot be
/// reached) and images under <c>img\</c>. All content comes from mod authors, so every URL is checked, sizes are
/// capped, and no network failure ever throws; only cancellation does.
/// </summary>
public sealed class SpaceDockClient
{
    private const string UserAgent = "VmpLauncher (+https://github.com/Rjwolfe44/VMPLauncher)";
    private const int MaxJsonBytes = 4 * 1024 * 1024;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const int DefaultParallelRequests = 4;

    private static readonly Uri Site = new("https://spacedock.info/");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient SharedHttp = CreateClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });
    private static readonly string[] AuthorNameKeys = ["user", "username", "name"];

    private readonly string _dataDir;
    private readonly string _imageDir;
    private readonly TimeSpan _maxAge;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate;
    private readonly object _availability = new();
    private DateTimeOffset? _unavailableSince;

    /// <param name="cacheDir">Root folder for the cache; <c>spacedock\</c> and <c>img\</c> are created under it.</param>
    /// <param name="handler">Tests only: replaces the network. The caller keeps ownership of it.</param>
    /// <param name="maxAge">How long cached mod data counts as fresh; 7 days when omitted.</param>
    /// <param name="maxParallelRequests">Requests in flight at once (4 when omitted).</param>
    public SpaceDockClient(string cacheDir, HttpMessageHandler? handler = null, TimeSpan? maxAge = null, int maxParallelRequests = DefaultParallelRequests)
    {
        _gate = new SemaphoreSlim(Math.Clamp(maxParallelRequests, 1, 16), Math.Clamp(maxParallelRequests, 1, 16));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);
        _dataDir = Path.Combine(cacheDir, "spacedock");
        _imageDir = Path.Combine(cacheDir, "img");
        _maxAge = maxAge ?? TimeSpan.FromDays(7);
        _http = handler is null ? SharedHttp : CreateClient(handler);
    }

    /// <summary>When SpaceDock started failing (server errors, timeouts, no network); null while it answers.</summary>
    public DateTimeOffset? UnavailableSinceUtc
    {
        get { lock (_availability) { return _unavailableSince; } }
    }

    /// <summary>Raised (on a background thread) when SpaceDock goes down or comes back.</summary>
    public event Action? AvailabilityChanged;

    private void Report(bool answered)
    {
        bool changed;
        lock (_availability)
        {
            var wasDown = _unavailableSince is not null;
            if (answered)
            {
                _unavailableSince = null;
            }
            else
            {
                _unavailableSince ??= DateTimeOffset.UtcNow;
            }
            changed = wasDown != (_unavailableSince is not null);
        }
        if (changed)
        {
            AvailabilityChanged?.Invoke();
        }
    }

    /// <summary>One uncached request to see whether SpaceDock answers again; true when it does.</summary>
    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        var (outcome, _) = await FetchJsonAsync(141, ct).ConfigureAwait(false);
        return outcome != FetchOutcome.Failed;
    }

    /// <summary>"https://spacedock.info/mod/1234/Some%20Name" or ".../mod/1234" gives 1234; anything else gives null.</summary>
    public static int? IdFromUrl(Uri? spaceDockModUrl)
    {
        if (spaceDockModUrl is null || !spaceDockModUrl.IsAbsoluteUri)
        {
            return null;
        }
        if (spaceDockModUrl.Scheme != Uri.UriSchemeHttps && spaceDockModUrl.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }
        var host = spaceDockModUrl.Host;
        if (!host.Equals("spacedock.info", StringComparison.OrdinalIgnoreCase) && !host.Equals("www.spacedock.info", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var parts = spaceDockModUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("mod", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// The mod's details: from the cache when fresh, else from SpaceDock. On a network failure the stale cached copy is
    /// returned if there is one. Null when the mod does not exist (remembered for <c>maxAge</c>) or nothing is available.
    /// </summary>
    public async Task<ModInfo?> GetAsync(int id, CancellationToken ct)
    {
        if (id <= 0)
        {
            return null;
        }
        var cached = await ReadCacheAsync(id, ct).ConfigureAwait(false);
        if (cached is not null && IsFresh(cached.FetchedUtc))
        {
            return cached.Info;
        }

        var (outcome, body) = await FetchJsonAsync(id, ct).ConfigureAwait(false);
        if (outcome == FetchOutcome.NotFound)
        {
            await WriteCacheAsync(id, DateTimeOffset.UtcNow, null, ct).ConfigureAwait(false);
            return null;
        }
        if (outcome == FetchOutcome.Found && body is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (IsModObject(doc.RootElement))
                {
                    var now = DateTimeOffset.UtcNow;
                    await WriteCacheAsync(id, now, doc.RootElement, ct).ConfigureAwait(false);
                    return Parse(doc.RootElement, id, now);
                }
            }
            catch (JsonException)
            {
                // Not the JSON we expected (a proxy error page, say): treat like any other failed fetch.
            }
        }
        return cached?.Info;
    }

    /// <summary>
    /// Downloads an https image once into the cache and returns the file path, or null when the URL is not a public
    /// https address, the server answers with anything but an image (or over 8 MB), or the network fails.
    /// </summary>
    public async Task<string?> GetImageFileAsync(Uri imageUrl, CancellationToken ct)
    {
        if (imageUrl is null || !imageUrl.IsAbsoluteUri || imageUrl.Scheme != Uri.UriSchemeHttps || IsLocalOrPrivateHost(imageUrl))
        {
            return null;
        }
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl.AbsoluteUri)))[..16].ToLowerInvariant();
        if (FindCachedImage(stem) is { } hit)
        {
            return hit;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (FindCachedImage(stem) is { } downloadedMeanwhile)
            {
                return downloadedMeanwhile;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 500 && IsSpaceDock(imageUrl))
            {
                Report(answered: false);
            }
            else if (IsSpaceDock(imageUrl))
            {
                Report(answered: true);
            }
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }
            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            // SVG is an image/* type but nothing here can draw it.
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || mediaType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            Directory.CreateDirectory(_imageDir);
            var destination = Path.Combine(_imageDir, stem + ImageExtension(imageUrl, mediaType));
            // The temp name does not start with "<stem>." so it is never mistaken for a finished download.
            var tmp = Path.Combine(_imageDir, $"{stem}-{Guid.NewGuid():N}.tmp");
            try
            {
                long total = 0;
                await using (var input = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    int read;
                    while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxImageBytes)
                        {
                            return null;
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }
                }
                if (total == 0)
                {
                    return null;
                }
                File.Move(tmp, destination, overwrite: true);
                return destination;
            }
            finally
            {
                TryDelete(tmp);
            }
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            if (IsSpaceDock(imageUrl))
            {
                Report(answered: false);
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsSpaceDock(Uri url) => url.Host.EndsWith("spacedock.info", StringComparison.OrdinalIgnoreCase);

    // --- network ---

    private enum FetchOutcome { Found, NotFound, Failed }

    private async Task<(FetchOutcome Outcome, byte[]? Body)> FetchJsonAsync(int id, CancellationToken ct)
    {
        var url = new Uri(Site, "api/mod/" + id.ToString(CultureInfo.InvariantCulture));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                Report(answered: true);
                return (FetchOutcome.NotFound, null);
            }
            if (!resp.IsSuccessStatusCode)
            {
                Report(answered: (int)resp.StatusCode < 500);
                return (FetchOutcome.Failed, null);
            }
            var body = await ReadCappedAsync(resp.Content, MaxJsonBytes, timeout.Token).ConfigureAwait(false);
            Report(answered: true);
            return body is null ? (FetchOutcome.Failed, null) : (FetchOutcome.Found, body);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            Report(answered: false);
            return (FetchOutcome.Failed, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int max, CancellationToken ct)
    {
        if (content.Headers.ContentLength > max)
        {
            return null;
        }
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[1 << 14];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > max)
            {
                return null;
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    // A timeout surfaces as a cancellation that the caller did not ask for; a real cancel must still propagate.
    private static bool IsNetworkFailure(Exception ex, CancellationToken ct)
        => ex is HttpRequestException or IOException or UnauthorizedAccessException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // --- images ---

    private string? FindCachedImage(string stem)
    {
        try
        {
            if (!Directory.Exists(_imageDir))
            {
                return null;
            }
            return Directory.EnumerateFiles(_imageDir, stem + ".*").FirstOrDefault(f => new FileInfo(f).Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ImageExtension(Uri url, string mediaType)
    {
        var fromUrl = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        if (fromUrl is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
        {
            return fromUrl;
        }
        return mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".img",
        };
    }

    /// <summary>Image URLs come from mod descriptions, so they must not be able to poke at the user's own network.</summary>
    private static bool IsLocalOrPrivateHost(Uri url)
    {
        if (url.IsLoopback || url.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IPAddress.TryParse(url.DnsSafeHost, out var ip))
        {
            return false;
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC || ip.Equals(IPAddress.IPv6Any);
        }
        var v4 = ip.GetAddressBytes();
        return v4[0] is 0 or 10 or 127
            || (v4[0] == 100 && v4[1] is >= 64 and <= 127)
            || (v4[0] == 169 && v4[1] == 254)
            || (v4[0] == 172 && v4[1] is >= 16 and <= 31)
            || (v4[0] == 192 && v4[1] == 168)
            || v4[0] >= 224;
    }

    // --- cache ---

    /// <summary>A cache file: <c>{"fetchedUtc": "...", "missing": bool, "data": {raw SpaceDock response}}</c>. A null <paramref name="Info"/> means SpaceDock said 404.</summary>
    private sealed record CacheEntry(DateTimeOffset FetchedUtc, ModInfo? Info);

    private string CachePath(int id) => Path.Combine(_dataDir, id.ToString(CultureInfo.InvariantCulture) + ".json");

    private bool IsFresh(DateTimeOffset fetchedUtc)
    {
        if (_maxAge <= TimeSpan.Zero)
        {
            return false;
        }
        var age = DateTimeOffset.UtcNow - fetchedUtc;
        // A timestamp far in the future (clock changes) is not trusted.
        return age < _maxAge && age > TimeSpan.FromDays(-1);
    }

    private async Task<CacheEntry?> ReadCacheAsync(int id, CancellationToken ct)
    {
        var path = CachePath(id);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 2L * MaxJsonBytes)
            {
                return null;
            }
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var fetched = DateTimeOffset.TryParse(Str(root, "fetchedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var f)
                ? f
                : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            if (root.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.True)
            {
                return new CacheEntry(fetched, null);
            }
            return root.TryGetProperty("data", out var data) && IsModObject(data) ? new CacheEntry(fetched, Parse(data, id, fetched)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(int id, DateTimeOffset fetched, JsonElement? data, CancellationToken ct)
    {
        var path = CachePath(id);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("fetchedUtc", fetched.ToString("O", CultureInfo.InvariantCulture));
                w.WriteBoolean("missing", data is null);
                if (data is { } element)
                {
                    w.WritePropertyName("data");
                    element.WriteTo(w);
                }
                w.WriteEndObject();
            }
            Directory.CreateDirectory(_dataDir);
            await File.WriteAllBytesAsync(tmp, ms.ToArray(), ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only costs a refetch next time.
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    // --- JSON mapping (lenient: any field may be missing, null or the wrong type) ---

    private static bool IsModObject(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && !(root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True);

    private static ModInfo Parse(JsonElement root, int requestedId, DateTimeOffset fetched)
    {
        var id = Int(root, "id");
        if (id <= 0)
        {
            id = requestedId;
        }
        var page = Link(Str(root, "url"), httpsOnly: true, Site, bareHost: false);
        if (page is null || !string.Equals(page.Host, Site.Host, StringComparison.OrdinalIgnoreCase))
        {
            page = new Uri(Site, "mod/" + id.ToString(CultureInfo.InvariantCulture));
        }
        return new ModInfo(
            id,
            Str(root, "name") ?? $"SpaceDock mod {id.ToString(CultureInfo.InvariantCulture)}",
            Str(root, "short_description"),
            Str(root, "description"),
            Int(root, "downloads"),
            Int(root, "followers"),
            Str(root, "author"),
            SharedAuthors(root),
            Link(Str(root, "background"), httpsOnly: true, Site, bareHost: false),
            Str(root, "license"),
            Link(Str(root, "website"), httpsOnly: false, null, bareHost: true),
            Link(Str(root, "donations"), httpsOnly: false, null, bareHost: true),
            Link(Str(root, "source_code"), httpsOnly: false, null, bareHost: true),
            page,
            Versions(root),
            fetched);
    }

    private static List<string> SharedAuthors(JsonElement root)
    {
        var names = new List<string>();
        if (!root.TryGetProperty("shared_authors", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return names;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list.EnumerateArray())
        {
            // SpaceDock lists invitations that were never accepted too; those are not authors yet.
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False)
            {
                continue;
            }
            var name = NameOf(entry);
            if (name is not null && seen.Add(name))
            {
                names.Add(name);
            }
        }
        return names;
    }

    private static string? NameOf(JsonElement entry)
    {
        switch (entry.ValueKind)
        {
            case JsonValueKind.String:
                return Clean(entry.GetString());
            case JsonValueKind.Object:
                foreach (var key in AuthorNameKeys)
                {
                    if (entry.TryGetProperty(key, out var inner) && NameOf(inner) is { } name)
                    {
                        return name;
                    }
                }
                return null;
            default:
                return null;
        }
    }

    private static List<ModInfoVersion> Versions(JsonElement root)
    {
        var versions = new List<ModInfoVersion>();
        if (!root.TryGetProperty("versions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return versions;
        }
        foreach (var v in list.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            versions.Add(new ModInfoVersion(
                Str(v, "friendly_version") ?? "unknown",
                Str(v, "game_version"),
                Date(v, "created"),
                Str(v, "changelog"),
                Int(v, "downloads"),
                Link(Str(v, "download_path"), httpsOnly: true, Site, bareHost: false)));
        }
        return versions;
    }

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => Clean(v.GetString()),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static int Int(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
        {
            return 0;
        }
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (v.TryGetInt32(out var i))
                {
                    return Math.Max(0, i);
                }
                return v.TryGetDouble(out var d) && d > 0 ? (d >= int.MaxValue ? int.MaxValue : (int)d) : 0;
            case JsonValueKind.String:
                return int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Max(0, parsed) : 0;
            default:
                return 0;
        }
    }

    private static DateTimeOffset? Date(JsonElement obj, string name)
        => DateTimeOffset.TryParse(Str(obj, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>
    /// A clickable web address from author-supplied text: http(s) only (https only when asked), no credentials,
    /// "/path" resolved against <paramref name="relativeTo"/>, and for links people type by hand a bare "example.com" gets https://.
    /// </summary>
    private static Uri? Link(string? raw, bool httpsOnly, Uri? relativeTo, bool bareHost)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var s = raw.Trim();
        if (s.StartsWith("//", StringComparison.Ordinal))
        {
            s = "https:" + s;
        }
        Uri? uri;
        if (s.StartsWith('/'))
        {
            if (relativeTo is null || !Uri.TryCreate(relativeTo, s, out uri))
            {
                return null;
            }
        }
        else if (!Uri.TryCreate(s, UriKind.Absolute, out uri))
        {
            if (bareHost && s.Contains('.', StringComparison.Ordinal) && !s.Contains(' ', StringComparison.Ordinal))
            {
                if (!Uri.TryCreate("https://" + s, UriKind.Absolute, out uri))
                {
                    return null;
                }
            }
            else if (relativeTo is null || !Uri.TryCreate(relativeTo, s, out uri))
            {
                return null;
            }
        }
        var schemeOk = uri.Scheme == Uri.UriSchemeHttps || (!httpsOnly && uri.Scheme == Uri.UriSchemeHttp);
        return schemeOk && uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri : null;
    }
}
