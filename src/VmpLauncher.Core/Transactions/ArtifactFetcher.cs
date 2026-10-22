using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;

namespace VmpLauncher.Core.Transactions;

/// <summary>
/// Downloads small non-CKAN artifacts (override bundles) into <c>&lt;cache&gt;\vmpl-artifacts\&lt;sha256&gt;.zip</c>,
/// verifying the hash. HTTPS only; hosts are checked against the allow-list from the lock's trust level.
/// </summary>
public sealed class ArtifactFetcher
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "VmpLauncher" } },
    };

    private readonly string _dir;
    private readonly IReadOnlyCollection<string> _allowedHosts;

    public ArtifactFetcher(string cacheDir, IReadOnlyCollection<string>? allowedHosts = null)
    {
        _dir = Path.Combine(cacheDir, "vmpl-artifacts");
        _allowedHosts = allowedHosts ?? new[] { "github.com", "objects.githubusercontent.com", "raw.githubusercontent.com", "release-assets.githubusercontent.com" };
    }

    public string PathFor(ArtifactRef artifact) => Path.Combine(_dir, artifact.Sha256.ToLowerInvariant() + ".zip");

    public bool IsCached(ArtifactRef artifact)
    {
        var p = PathFor(artifact);
        return File.Exists(p) && new FileInfo(p).Length == artifact.Size && Sha256Util.HexEquals(Sha256Util.HashFile(p), artifact.Sha256);
    }

    public string Fetch(ArtifactRef artifact, CancellationToken ct = default)
    {
        var path = PathFor(artifact);
        if (IsCached(artifact))
        {
            return path;
        }
        Directory.CreateDirectory(_dir);
        if (artifact.Urls.Count == 0)
        {
            throw new LauncherException(ErrorCode.PrivateArtifactNeeded, $"Artifact {artifact.FileName ?? artifact.Sha256[..12]} has no download URL.", RecoveryHint.ProvideFile);
        }
        var errors = new List<string>();
        foreach (var url in artifact.Urls)
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"{url}: not https");
                continue;
            }
            if (!_allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{url}: host not allowed");
                continue;
            }
            var tmp = path + ".part";
            try
            {
                using var response = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using (var src = response.Content.ReadAsStream(ct))
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1024 * 1024];
                    long total = 0;
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > artifact.Size)
                        {
                            throw new InvalidDataException("download larger than declared size");
                        }
                        dst.Write(buffer, 0, read);
                    }
                }
                var sha = Sha256Util.HashFile(tmp, ct);
                if (!Sha256Util.HexEquals(sha, artifact.Sha256))
                {
                    File.Delete(tmp);
                    errors.Add($"{url}: hash mismatch ({sha[..12]})");
                    continue;
                }
                File.Move(tmp, path, overwrite: true);
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add($"{url}: {ex.Message}");
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch (IOException) { }
                }
            }
        }
        throw new LauncherException(ErrorCode.ArtifactUnavailable, $"Could not download {artifact.FileName ?? "an override bundle"}.", RecoveryHint.Retry, details: errors);
    }

    public void Store(ArtifactRef artifact, string sourceFile)
    {
        var sha = Sha256Util.HashFile(sourceFile);
        if (!Sha256Util.HexEquals(sha, artifact.Sha256))
        {
            throw new LauncherException(ErrorCode.ArtifactCorrupt, $"{Path.GetFileName(sourceFile)} does not match the expected hash.", RecoveryHint.ProvideFile);
        }
        Directory.CreateDirectory(_dir);
        File.Copy(sourceFile, PathFor(artifact), overwrite: true);
    }
}
