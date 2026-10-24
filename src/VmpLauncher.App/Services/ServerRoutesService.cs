using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VmpLauncher.Core;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Net.Directory;

namespace VmpLauncher.App.Services;

/// <summary>Bridges Core's pinned-key signature verifier to the Net layer's document verifier.</summary>
public sealed class CoreSignedDocumentVerifier : ISignedDocumentVerifier
{
    private readonly SignatureVerifier _verifier = SignatureVerifier.Default();

    public bool Verify(ReadOnlySpan<byte> document, string signatureJson, out string? keyId)
    {
        SignatureEnvelope? env;
        try
        {
            env = JsonSerializer.Deserialize<SignatureEnvelope>(signatureJson, LauncherJson.Pretty);
        }
        catch (JsonException)
        {
            keyId = null;
            return false;
        }
        var level = _verifier.Verify(document, env, out keyId, out _);
        return level == TrustLevel.VerifiedPublisher;
    }
}

/// <summary>Where a route on the Play page comes from.</summary>
public static class RouteOrigin
{
    /// <summary>The host's published list (or the presets frozen in the pack).</summary>
    public const string Pack = "pack";

    /// <summary>A preset the host keeps off the published list (exportable: false), shown on the host's own PC only.</summary>
    public const string Host = "host";

    /// <summary>Added by the player on this PC.</summary>
    public const string Mine = "mine";
}

public sealed record RouteInfo(string Id, string Name, string EntryName, string Host, int Port, string Kind, string? Notes, string? RequiresSoftware, string? NetworkId, string Origin = RouteOrigin.Pack);

/// <summary>Adds the routes that never travel with the pack (the host's private presets, the player's own servers) to the published ones.</summary>
public static class RouteMerger
{
    public const string HostOnlyNote = "Only on this PC: kept off the published server list.";

    /// <summary>
    /// Published routes first, then the host-only presets, then the player's servers. A route whose name or address is
    /// already listed is skipped (the published one works already), and ids stay unique.
    /// </summary>
    public static IReadOnlyList<RouteInfo> Merge(IReadOnlyList<RouteInfo> published, IEnumerable<ServerPreset> hostOnly, IEnumerable<MyServer> mine)
    {
        var all = new List<RouteInfo>(published);
        foreach (var p in hostOnly.Where(p => !p.Exportable))
        {
            Add(all, "host-", p.Name, p.Address, p.Port, HostOnlyNote, RouteOrigin.Host);
        }
        foreach (var m in mine)
        {
            Add(all, "mine-", m.Name, m.Host, m.Port, null, RouteOrigin.Mine);
        }
        return all;
    }

    private static void Add(List<RouteInfo> all, string idPrefix, string name, string host, int port, string? notes, string origin)
    {
        if (all.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
                      || (string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase) && r.Port == port)))
        {
            return;
        }
        var baseId = idPrefix + Slug(name);
        var id = baseId;
        for (var i = 2; all.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)); i++)
        {
            id = baseId + "-" + i;
        }
        all.Add(new RouteInfo(id, name, name, host, port, MyServers.Kind(host), notes, null, null, origin));
    }

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = System.Text.RegularExpressions.Regex.Replace(new string(chars), "-+", "-").Trim('-');
        return slug.Length == 0 ? "server" : slug;
    }
}

public sealed record RoutesResult(IReadOnlyList<RouteInfo> Routes, string Source, string? RequiredBuild, string? Motd, string? Maintenance, string? Warning);

/// <summary>
/// Where the Play page gets its routes: the signed live <c>servers.json</c> when the pack points at one and it
/// verifies, else the cached copy, else the presets frozen in the lock.
/// </summary>
public sealed class ServerRoutesService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly LauncherPaths _paths;
    private readonly ILogger<ServerRoutesService> _log;

    public ServerRoutesService(LauncherPaths paths, ILogger<ServerRoutesService> log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<RoutesResult> GetRoutesAsync(ResolvedPack pack, CancellationToken ct)
    {
        var url = pack.Lock.Multiplayer.ServerDirectoryUrl;
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            var client = new ServerDirectoryClient(_paths, pack.Lock.Pack.Id, Http, new CoreSignedDocumentVerifier());
            try
            {
                var result = await client.FetchAsync(uri, new Uri(url + ".sig"), ct).ConfigureAwait(false);
                return FromDirectory(result, result.IsFromCache ? $"saved server list ({Describe(result.Age)} old)" : "live server list");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Server directory unavailable; using lock presets");
                var cached = client.LoadCached();
                if (cached is not null)
                {
                    return FromDirectory(cached, $"saved server list ({Describe(cached.Age)} old)") with { Warning = "Could not refresh the server list; using the saved copy." };
                }
            }
        }
        var fromLock = pack.Lock.Multiplayer.Servers
            .Select(s => new RouteInfo(s.Name.ToLowerInvariant().Replace(' ', '-'), s.Name, s.Name, s.Address, s.Port, GuessKind(s.Address), null, GuessKind(s.Address) == "zerotier" ? "ZeroTier" : null, null))
            .ToList();
        return new RoutesResult(fromLock, "pack presets", pack.Lock.Multiplayer.RequiredClientVersion, null, null, null);
    }

    private static RoutesResult FromDirectory(ServerDirectoryResult r, string source)
    {
        var d = r.Directory;
        var routes = d.RoutesByPriority
            .Select(x => new RouteInfo(x.Id, x.Name, x.VmpEntryName ?? x.Name, x.Host, x.Port, x.Kind.ToString().ToLowerInvariant(), x.Notes, x.RequiresSoftware?.Name, x.RequiresSoftware?.NetworkId))
            .ToList();
        return new RoutesResult(routes, source, d.Server.RequiredBuild, d.Motd, d.Maintenance?.Message, r.Warning);
    }

    private static string GuessKind(string host)
        => host.EndsWith(".ply.gg", StringComparison.OrdinalIgnoreCase) ? "playit"
         : host.StartsWith("10.", StringComparison.Ordinal) ? "zerotier"
         : host.StartsWith("192.168.", StringComparison.Ordinal) ? "lan" : "direct";

    private static string Describe(TimeSpan age)
        => age.TotalDays >= 1 ? $"{(int)age.TotalDays} d" : age.TotalHours >= 1 ? $"{(int)age.TotalHours} h" : $"{Math.Max(1, (int)age.TotalMinutes)} min";
}
