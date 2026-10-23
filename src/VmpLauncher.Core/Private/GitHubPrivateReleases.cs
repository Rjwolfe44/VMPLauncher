using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;

namespace VmpLauncher.Core.Private;

/// <summary>
/// Downloads release assets from a private GitHub repository with a read-only token. The token is sent only to the
/// GitHub API host; the storage redirect GitHub answers with is followed without it. Every file is streamed to a
/// temp file under a size cap, hashed on the way, and only moved into place when it matches the signed lock.
/// </summary>
public sealed class GitHubPrivateReleases : IDisposable
{
    public enum AccessState
    {
        Ok,
        /// <summary>401: token wrong, revoked or expired.</summary>
        BadToken,
        /// <summary>404: repository missing or the token cannot see it.</summary>
        NoAccess,
        /// <summary>The repository is reachable but public; private packages must never live there.</summary>
        NotPrivate,
        /// <summary>Network trouble or rate limiting.</summary>
        Unreachable,
    }

    public sealed record AccessResult(AccessState State, string Message);

    private static readonly Uri DefaultApi = new("https://api.github.com/");
    private readonly HttpClient _http;
    private readonly Uri _api;

    public GitHubPrivateReleases(HttpMessageHandler? handler = null, Uri? apiBase = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VmpLauncher", "1.0"));
        _api = apiBase ?? DefaultApi;
    }

    public async Task<AccessResult> CheckAccessAsync(string repo, string token, CancellationToken ct)
    {
        if (!InviteCode.IsValidRepo(repo))
        {
            return new AccessResult(AccessState.NoAccess, $"'{repo}' is not a valid repository name.");
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var req = Api(HttpMethod.Get, $"repos/{repo}", token);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)))
                    {
                        var isPrivate = doc.RootElement.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True;
                        return isPrivate
                            ? new AccessResult(AccessState.Ok, $"Connected to {repo}.")
                            : new AccessResult(AccessState.NotPrivate, $"{repo} is public. Private mods must live in a private repository.");
                    }
                case HttpStatusCode.Unauthorized:
                    return new AccessResult(AccessState.BadToken, "The access token was rejected (expired or revoked). Ask the host for a new invite code.");
                case HttpStatusCode.NotFound:
                    return new AccessResult(AccessState.NoAccess, $"The token cannot see {repo}. Ask the host for a new invite code.");
                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                    return new AccessResult(AccessState.Unreachable, "GitHub is rate limiting requests. Try again in a few minutes.");
                default:
                    return new AccessResult(AccessState.Unreachable, $"GitHub answered {(int)resp.StatusCode}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new AccessResult(AccessState.Unreachable, "Could not reach GitHub. Check your internet connection.");
        }
    }

    /// <summary>
    /// Downloads the asset named by <paramref name="source"/> into <paramref name="destination"/> and proves it is
    /// <paramref name="expected"/> (size and SHA-256) before the file appears there.
    /// </summary>
    public async Task DownloadAsync(PrivateSourceRef source, ArtifactRef expected, string token, string destination, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (source.Kind != "github-release" || !InviteCode.IsValidRepo(source.Repo))
        {
            throw new LauncherException(ErrorCode.InvalidLock, $"Unsupported private source {source.Kind}:{source.Repo}.");
        }
        var assetUrl = await FindAssetAsync(source, token, ct).ConfigureAwait(false);

        // 1. Ask the API for the asset bytes; GitHub answers with a short-lived redirect to its storage host.
        using var req = Api(HttpMethod.Get, assetUrl, token);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var first = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        HttpResponseMessage response = first;
        HttpResponseMessage? redirected = null;
        try
        {
            if (first.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.MovedPermanently or HttpStatusCode.SeeOther)
            {
                var location = first.Headers.Location ?? throw Unavailable(source, "redirect without a location");
                if (!location.IsAbsoluteUri)
                {
                    location = new Uri(new Uri(_api, assetUrl), location);
                }
                if (location.Scheme != Uri.UriSchemeHttps && !_api.IsLoopback)
                {
                    throw Unavailable(source, "redirect to a non-HTTPS location");
                }
                // 2. Follow it WITHOUT the token: the storage URL is pre-signed and must never see the credential.
                using var follow = new HttpRequestMessage(HttpMethod.Get, location);
                redirected = await _http.SendAsync(follow, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response = redirected;
            }
            ThrowForStatus(response, source);
            await SaveVerifiedAsync(response, expected, destination, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            redirected?.Dispose();
        }
    }

    private async Task<string> FindAssetAsync(PrivateSourceRef source, string token, CancellationToken ct)
    {
        using var req = Api(HttpMethod.Get, $"repos/{source.Repo}/releases/tags/{Uri.EscapeDataString(source.Tag)}", token);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        ThrowForStatus(resp, source);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var n) && string.Equals(n.GetString(), source.Asset, StringComparison.Ordinal)
                    && a.TryGetProperty("id", out var id) && id.TryGetInt64(out var assetId))
                {
                    return $"repos/{source.Repo}/releases/assets/{assetId}";
                }
            }
        }
        throw Unavailable(source, $"release {source.Tag} has no asset named {source.Asset}");
    }

    private static async Task SaveVerifiedAsync(HttpResponseMessage response, ArtifactRef expected, string destination, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var tmp = destination + ".part";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > expected.Size)
                    {
                        throw new LauncherException(ErrorCode.ArtifactCorrupt, $"{expected.FileName} is larger than the pack says ({expected.Size:N0} bytes).", RecoveryHint.AskHost);
                    }
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((done, expected.Size));
                }
                var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (done != expected.Size || !Sha256Util.HexEquals(actual, expected.Sha256))
                {
                    throw new LauncherException(ErrorCode.ArtifactCorrupt, $"{expected.FileName} from the private repository does not match the pack (the host may need to republish it).", RecoveryHint.AskHost,
                        details: new[] { $"expected {expected.Size} bytes sha256 {expected.Sha256}", $"got {done} bytes sha256 {actual}" });
                }
            }
            File.Move(tmp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private HttpRequestMessage Api(HttpMethod method, string relative, string token)
    {
        var req = new HttpRequestMessage(method, new Uri(_api, relative));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private static void ThrowForStatus(HttpResponseMessage resp, PrivateSourceRef source)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }
        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new LauncherException(ErrorCode.PrivateArtifactNeeded, "Your private-mods access expired or was revoked. Ask the host for a new invite code.", RecoveryHint.AskHost),
            HttpStatusCode.NotFound => new LauncherException(ErrorCode.PrivateArtifactNeeded, $"{source.Asset} is not in {source.Repo} (or your invite cannot see it). Ask the host to publish it or send a new invite.", RecoveryHint.AskHost),
            _ => Unavailable(source, $"GitHub answered {(int)resp.StatusCode}"),
        };
    }

    private static LauncherException Unavailable(PrivateSourceRef source, string why)
        => new(ErrorCode.ArtifactUnavailable, $"Could not download {source.Asset} from the private repository: {why}.", RecoveryHint.Retry);

    public void Dispose() => _http.Dispose();
}
