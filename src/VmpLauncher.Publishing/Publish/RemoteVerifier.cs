using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Net.Directory;
using VmpLauncher.Net.Probing;

namespace VmpLauncher.Publishing.Publish;

/// <summary>
/// Checks what friends will actually see: catalog → channel → lock → inventory/policy/servers.json, all fetched fresh
/// over HTTPS, signatures verified against the launcher's built-in keys, hashes compared, routes probed.
/// </summary>
public sealed class RemoteVerifier
{
    public sealed record Result(string? PublishedVersion, IReadOnlyList<string> Lines, IReadOnlyList<string> Problems)
    {
        public bool Ok => Problems.Count == 0;
    }

    private readonly HttpClient _http;
    private readonly IServerProbe? _probe;
    private readonly SignatureVerifier _verifier;

    public RemoteVerifier(HttpClient? http = null, IServerProbe? probe = null, SignatureVerifier? verifier = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _probe = probe;
        _verifier = verifier ?? SignatureVerifier.Default();
    }

    public async Task<Result> VerifyAsync(string catalogUrl, string packId, string? expectedVersion, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var problems = new List<string>();
        string? version = null;
        try
        {
            var catalog = JsonNode.Parse(await GetAsync(catalogUrl, ct).ConfigureAwait(false)) as JsonArray;
            var entry = catalog?.FirstOrDefault(e => e?["id"]?.GetValue<string>() == packId)
                ?? throw new InvalidOperationException($"catalog has no pack '{packId}'");
            var baseUrl = catalogUrl[..(catalogUrl.LastIndexOf('/') + 1)] + entry["path"]!.GetValue<string>().TrimEnd('/') + "/";
            lines.Add($"catalog: {packId} -> {baseUrl}");

            var channelBytes = await GetAsync(baseUrl + "channel.json", ct).ConfigureAwait(false);
            CheckSignature("channel.json", channelBytes, await TryGetAsync(baseUrl + "channel.json.sig", ct).ConfigureAwait(false), lines, problems);
            var channel = JsonNode.Parse(channelBytes)!;
            var stable = channel["channels"]!["stable"]!;
            version = stable["version"]!.GetValue<string>();
            var lockRel = stable["lock"]!.GetValue<string>();
            var lockSha = stable["sha256"]!.GetValue<string>();
            lines.Add($"channel: stable = {version} ({lockRel})");
            if (expectedVersion is not null && version != expectedVersion)
            {
                problems.Add($"channel points at {version}, expected {expectedVersion} (CDN cache? wait a few minutes)");
            }

            var lockBytes = await GetAsync(baseUrl + lockRel, ct).ConfigureAwait(false);
            if (!Sha256Util.HexEquals(Sha256Util.HashBytes(lockBytes), lockSha))
            {
                problems.Add("lock bytes do not match the sha256 in channel.json");
            }
            CheckSignature("lock", lockBytes, await TryGetAsync(baseUrl + lockRel + ".sig", ct).ConfigureAwait(false), lines, problems);
            var lk = JsonSerializer.Deserialize<PackLock>(lockBytes, LauncherJson.Pretty)!;
            lines.Add($"lock: {lk.Pack.Id} {lk.Pack.Version}, {lk.Ckan.Modules.Count} modules, client {lk.Multiplayer.RequiredClientVersion}");
            if (lk.Pack.Version != version)
            {
                problems.Add($"lock says version {lk.Pack.Version} but the channel says {version}");
            }

            var versionBase = baseUrl + lockRel[..(lockRel.LastIndexOf('/') + 1)];
            var inv = await GetAsync(versionBase + lk.Inventory.File, ct).ConfigureAwait(false);
            if (!Sha256Util.HexEquals(Sha256Util.HashBytes(inv), lk.Inventory.Sha256))
            {
                problems.Add("inventory.json.gz does not match the lock");
            }
            else
            {
                lines.Add($"inventory: {inv.Length:N0} bytes, hash OK");
            }

            if (lk.Multiplayer.Policy is { } pol)
            {
                var policy = await TryGetAsync(pol.Url, ct).ConfigureAwait(false);
                if (policy is null)
                {
                    problems.Add($"policy not reachable at {pol.Url}");
                }
                else if (pol.Sha256AtLockTime is not null && !Sha256Util.HexEquals(Sha256Util.HashBytes(policy), pol.Sha256AtLockTime))
                {
                    problems.Add("published KSPModControl.xml differs from the one the lock was built against");
                }
                else
                {
                    lines.Add("policy: hash matches the lock");
                }
            }

            foreach (var m in lk.Ckan.Modules.Where(m => m.Origin == ModuleOrigin.Custom && m.Artifact is { Distribution: "download" }))
            {
                foreach (var url in m.Artifact!.Urls)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Head, url);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        lines.Add($"asset: {m.Identifier} reachable ({resp.Content.Headers.ContentLength?.ToString("N0") ?? "?"} bytes)");
                    }
                    else
                    {
                        problems.Add($"asset for {m.Identifier} not reachable: {url} ({(int)resp.StatusCode})");
                    }
                }
            }

            if (lk.Multiplayer.ServerDirectoryUrl is { } dirUrl)
            {
                var dirBytes = await TryGetAsync(dirUrl, ct).ConfigureAwait(false);
                if (dirBytes is null)
                {
                    problems.Add($"servers.json not reachable at {dirUrl}");
                }
                else
                {
                    CheckSignature("servers.json", dirBytes, await TryGetAsync(dirUrl + ".sig", ct).ConfigureAwait(false), lines, problems);
                    var dir = ServerDirectory.Parse(dirBytes);
                    lines.Add($"servers.json: sequence {dir.Sequence}, requires build {dir.Server.RequiredBuild}, {dir.Routes.Count} route(s)");
                    if (lk.Multiplayer.RequiredClientVersion is not null && dir.Server.RequiredBuild != lk.Multiplayer.RequiredClientVersion)
                    {
                        problems.Add($"servers.json requires build {dir.Server.RequiredBuild} but the pack installs {lk.Multiplayer.RequiredClientVersion}");
                    }
                    if (_probe is not null)
                    {
                        foreach (var route in dir.RoutesByPriority.Where(r => r.Kind is RouteKind.Playit or RouteKind.Direct))
                        {
                            var pr = await _probe.ProbeAsync(new ProbeRequest(route.Host, route.Port), ct).ConfigureAwait(false);
                            var line = $"route {route.Name}: {pr.Outcome}{(pr.RoundTrip is { } rtt ? $" {rtt.TotalMilliseconds:F0} ms" : "")}";
                            lines.Add(line);
                            if (!pr.ServerAnswered)
                            {
                                problems.Add($"route {route.Name} did not answer ({pr.Outcome}{(pr.Error is null ? "" : ": " + pr.Error)})");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            problems.Add("remote check failed: " + ex.Message);
        }
        return new Result(version, lines, problems);
    }

    private void CheckSignature(string what, byte[] document, byte[]? sig, List<string> lines, List<string> problems)
    {
        if (sig is null)
        {
            problems.Add($"{what}: no .sig next to it");
            return;
        }
        SignatureEnvelope? env;
        try
        {
            env = JsonSerializer.Deserialize<SignatureEnvelope>(sig, LauncherJson.Pretty);
        }
        catch (JsonException)
        {
            problems.Add($"{what}: .sig is not a signature envelope");
            return;
        }
        var trust = _verifier.Verify(document, env, out var keyId, out var reason);
        if (trust == TrustLevel.VerifiedPublisher)
        {
            lines.Add($"{what}: {SignatureVerifier.DescribeTrust(trust)} ({keyId})");
        }
        else
        {
            problems.Add($"{what}: {SignatureVerifier.DescribeTrust(trust)}{(reason is null ? "" : " - " + reason)}");
        }
    }

    private async Task<byte[]> GetAsync(string url, CancellationToken ct)
    {
        // raw.githubusercontent.com caches for a few minutes; a unique query string makes the CDN fetch the current file.
        var busted = url + (url.Contains('?') ? "&" : "?") + "vmpl=" + DateTimeOffset.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var req = new HttpRequestMessage(HttpMethod.Get, busted);
        req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
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
