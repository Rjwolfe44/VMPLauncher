using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.App.Services;

/// <summary>What the Home page shows: the resolved lock of the pack's current channel plus its trust level.</summary>
public sealed record ResolvedPack(PackLock Lock, string LockPath, string InventoryPath, string LockSha256, TrustLevel Trust, string? TrustReason, string Source);

public sealed record PackCatalogEntry(string Id, string Name, string Path, string? BannerUrl, string? MinLauncherVersion);

public sealed record ChannelFile(string Schema, string PackId, Dictionary<string, ChannelPointer> Channels);
public sealed record ChannelPointer(string Version, string Lock, string Sha256);

/// <summary>
/// Resolves a pack from the online catalog (catalog.json → channel.json → lock + inventory, each cached under
/// <c>cache\packs</c>) or from a local export folder. A mutable channel pointer must always resolve to an
/// immutable lock whose sha256 matches, and unsigned content is labelled, never trusted silently.
/// </summary>
public sealed class PackCatalogService
{
    public const string DefaultCatalogUrl = "https://raw.githubusercontent.com/Rjwolfe44/VMP-Packs/main/catalog.json";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };

    private readonly LauncherPaths _paths;
    private readonly AppSettingsStore _settings;
    private readonly ILogger<PackCatalogService> _log;
    private readonly SignatureVerifier _verifier = SignatureVerifier.Default();

    public PackCatalogService(LauncherPaths paths, AppSettingsStore settings, ILogger<PackCatalogService> log)
    {
        _paths = paths;
        _settings = settings;
        _log = log;
    }

    private string PackCacheDir => Path.Combine(_paths.CacheDir, "packs");

    /// <summary>Returns the pack to show. Prefers a local export folder when configured (host/dev), else the online catalog, else the last cached copy.</summary>
    public async Task<ResolvedPack?> ResolveAsync(string packId, CancellationToken ct)
    {
        var local = _settings.Current.LocalPackDir;
        if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
        {
            return ResolveLocal(local, "local folder");
        }
        try
        {
            return await ResolveOnlineAsync(packId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or IOException or JsonException) && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Catalog fetch failed; trying cached copy");
            var cachedDir = Path.Combine(PackCacheDir, packId);
            return Directory.Exists(cachedDir) ? ResolveLocal(cachedDir, "cached copy (offline)") : null;
        }
        catch (LauncherException ex) when (ex.Code == ErrorCode.Tampered && !ct.IsCancellationRequested && Directory.Exists(Path.Combine(PackCacheDir, packId)))
        {
            // Right after a publish GitHub's file cache can serve the new channel.json with the old signature for up to
            // five minutes. Keep the pack already verified and installed (nothing unverified is used); the next check retries.
            _log.LogWarning(ex, "New pack files did not verify; keeping the cached copy");
            return ResolveLocal(Path.Combine(PackCacheDir, packId), "cached copy (the newest pack files did not verify yet; GitHub can take up to 5 minutes after a publish)");
        }
    }

    public ResolvedPack ResolveLocal(string dir, string source)
    {
        var lockPath = Directory.EnumerateFiles(dir, "*.vmpack.lock.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                       ?? throw new LauncherException(ErrorCode.InvalidLock, $"No lock found in {dir}.");
        var lockDoc = LauncherJson.ReadFile<PackLock>(lockPath);
        var invPath = Path.Combine(dir, lockDoc.Inventory.File);
        _ = InventoryFile.Read(invPath, lockDoc.Inventory.Sha256); // validates hash and schema early
        var trust = _verifier.VerifyFile(lockPath, out _, out var reason);
        return new ResolvedPack(lockDoc, lockPath, invPath, Sha256Util.HashFile(lockPath), trust, reason, source);
    }

    private async Task<ResolvedPack> ResolveOnlineAsync(string packId, CancellationToken ct)
    {
        var catalogUrl = _settings.Current.CatalogUrl ?? DefaultCatalogUrl;
        var baseUri = new Uri(catalogUrl);
        var catalogJson = await GetStringAsync(baseUri, ct).ConfigureAwait(false);
        var entries = JsonSerializer.Deserialize<List<PackCatalogEntry>>(catalogJson, LauncherJson.Pretty) ?? new List<PackCatalogEntry>();
        var entry = entries.FirstOrDefault(e => string.Equals(e.Id, packId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new LauncherException(ErrorCode.InvalidPack, $"The catalog has no pack named '{packId}'.");
        var packBase = new Uri(baseUri, entry.Path.TrimEnd('/') + "/");
        var channelUri = new Uri(packBase, "channel.json");
        var channelBytes = await GetBytesAsync(channelUri, ct).ConfigureAwait(false);
        var channelSig = await TryGetStringAsync(new Uri(packBase, "channel.json.sig"), ct).ConfigureAwait(false);
        var channelTrust = _verifier.Verify(channelBytes, channelSig is null ? null : JsonSerializer.Deserialize<SignatureEnvelope>(channelSig, LauncherJson.Pretty), out _, out var channelReason);
        if (channelTrust == TrustLevel.Tampered)
        {
            throw new LauncherException(ErrorCode.Tampered, "The pack's channel file failed signature verification.", RecoveryHint.AskHost, details: new[] { channelReason ?? "" });
        }
        var channel = JsonSerializer.Deserialize<ChannelFile>(channelBytes, LauncherJson.Pretty) ?? throw new LauncherException(ErrorCode.InvalidPack, "Empty channel file.");
        var stable = channel.Channels.TryGetValue("stable", out var p) ? p : channel.Channels.Values.First();

        var dir = Path.Combine(PackCacheDir, packId);
        Directory.CreateDirectory(dir);
        var lockUri = new Uri(packBase, stable.Lock);
        var lockPath = Path.Combine(dir, $"{packId}.vmpack.lock.json");
        // Unchanged pack: the three small files above are the whole check (about 1.7 MB less per check).
        if (TryReuseCached(lockPath, stable.Sha256, catalogUrl) is { } unchanged)
        {
            return unchanged;
        }
        var lockBytes = await GetBytesAsync(lockUri, ct).ConfigureAwait(false);
        if (!Sha256Util.HexEquals(Sha256Util.HashBytes(lockBytes), stable.Sha256))
        {
            throw new LauncherException(ErrorCode.Tampered, "The downloaded lock does not match the hash in the channel file.", RecoveryHint.Retry);
        }
        await File.WriteAllBytesAsync(lockPath, lockBytes, ct).ConfigureAwait(false);
        var (lockSig, sigMissing) = await TryGetStringWithStatusAsync(new Uri(lockUri + ".sig"), ct).ConfigureAwait(false);
        if (lockSig is not null)
        {
            await File.WriteAllTextAsync(lockPath + ".sig", lockSig, ct).ConfigureAwait(false);
        }
        else if (!sigMissing)
        {
            throw new HttpRequestException("Could not download the lock signature.");
        }
        else if (File.Exists(lockPath + ".sig"))
        {
            File.Delete(lockPath + ".sig");
        }
        var lockDoc = LauncherJson.ReadFile<PackLock>(lockPath);
        var invUri = new Uri(lockUri, lockDoc.Inventory.File);
        var invPath = Path.Combine(dir, lockDoc.Inventory.File);
        var invBytes = await GetBytesAsync(invUri, ct).ConfigureAwait(false);
        if (!Sha256Util.HexEquals(Sha256Util.HashBytes(invBytes), lockDoc.Inventory.Sha256))
        {
            throw new LauncherException(ErrorCode.Tampered, "The downloaded inventory does not match the lock.", RecoveryHint.Retry);
        }
        await File.WriteAllBytesAsync(invPath, invBytes, ct).ConfigureAwait(false);
        // The changelog is display-only (unsigned); a failure to fetch it never blocks anything.
        var changelog = await TryGetStringAsync(new Uri(packBase, "CHANGELOG.md"), ct).ConfigureAwait(false);
        if (changelog is not null && changelog.Length < 512 * 1024)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "CHANGELOG.md"), changelog, ct).ConfigureAwait(false);
        }
        var trust = _verifier.VerifyFile(lockPath, out _, out var reason);
        if (trust == TrustLevel.Tampered)
        {
            throw new LauncherException(ErrorCode.Tampered, "The pack lock failed signature verification.", RecoveryHint.AskHost, details: new[] { reason ?? "" });
        }
        return new ResolvedPack(lockDoc, lockPath, invPath, Sha256Util.HashBytes(lockBytes), trust, reason, catalogUrl);
    }

    /// <summary>
    /// The cached lock and inventory when the channel still points at them: same lock hash, inventory present and
    /// matching, signature trusted. Anything less (missing, damaged, unsigned) means a normal download.
    /// </summary>
    private ResolvedPack? TryReuseCached(string lockPath, string expectedSha256, string source)
    {
        try
        {
            if (!File.Exists(lockPath) || !Sha256Util.HexEquals(Sha256Util.HashFile(lockPath), expectedSha256))
            {
                return null;
            }
            var lockDoc = LauncherJson.ReadFile<PackLock>(lockPath);
            var invPath = Path.Combine(Path.GetDirectoryName(lockPath)!, lockDoc.Inventory.File);
            if (!File.Exists(invPath))
            {
                return null;
            }
            _ = InventoryFile.Read(invPath, lockDoc.Inventory.Sha256);
            var trust = _verifier.VerifyFile(lockPath, out _, out var reason);
            return trust == TrustLevel.VerifiedPublisher ? new ResolvedPack(lockDoc, lockPath, invPath, expectedSha256.ToLowerInvariant(), trust, reason, source) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or LauncherException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string> GetStringAsync(Uri uri, CancellationToken ct)
        => System.Text.Encoding.UTF8.GetString(await GetBytesAsync(uri, ct).ConfigureAwait(false));

    private static async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"Refusing non-HTTPS pack source {uri}.");
        }
        using var response = await Http.GetAsync(uri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > LauncherJson.MaxLockBytes)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"{uri} is too large.");
        }
        return bytes;
    }

    private static async Task<string?> TryGetStringAsync(Uri uri, CancellationToken ct)
        => (await TryGetStringWithStatusAsync(uri, ct).ConfigureAwait(false)).Text;

    /// <summary>Text, or null with <c>Missing</c> = true for a definite 404 and false for a transient failure.</summary>
    private static async Task<(string? Text, bool Missing)> TryGetStringWithStatusAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(uri, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), false);
            }
            return (null, response.StatusCode == System.Net.HttpStatusCode.NotFound);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return (null, false);
        }
    }

    /// <summary>
    /// The changelog section for <paramref name="version"/> ("## version (date)" up to the next "## "), from the copy
    /// cached next to the lock (online) or the pack folder above a local export. Null when there is none.
    /// </summary>
    public static string? WhatsNew(ResolvedPack pack)
    {
        try
        {
            var dir = Path.GetDirectoryName(pack.LockPath)!;
            var candidates = new[] { Path.Combine(dir, "CHANGELOG.md"), Path.Combine(Path.GetDirectoryName(dir) ?? dir, "CHANGELOG.md") };
            var file = candidates.FirstOrDefault(File.Exists);
            if (file is null)
            {
                return null;
            }
            var lines = File.ReadAllLines(file);
            var header = "## " + pack.Lock.Pack.Version;
            var start = Array.FindIndex(lines, l => l.StartsWith(header, StringComparison.Ordinal));
            if (start < 0)
            {
                return null;
            }
            var body = lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal)).Where(l => l.Trim().Length > 0)
                .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? "\u2022 " + l[2..] : l).Take(14).ToList();
            return body.Count == 0 ? null : string.Join("\n", body);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
