using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text.Json;

namespace VmpLauncher.Net.ZeroTier;

public enum ZeroTierState
{
    NotInstalled,
    NotRunning,
    NotJoined,
    WaitingForAuthorization,
    Joined,
    Unknown,
}

/// <param name="State">Overall verdict.</param>
/// <param name="Address">This PC's ZeroTier IPv4 inside the requested subnet (Joined only).</param>
/// <param name="Detail">Plain-language explanation of how the verdict was reached.</param>
public sealed record ZeroTierStatus(ZeroTierState State, IPAddress? Address = null, string? Detail = null)
{
    /// <summary>10-hex-digit node id from the local API, when readable.</summary>
    public string? NodeId { get; init; }

    /// <summary>Raw network status from the local API (OK, REQUESTING_CONFIGURATION, ACCESS_DENIED, NOT_FOUND...).</summary>
    public string? NetworkStatus { get; init; }

    public bool LocalApiAvailable { get; init; }
}

public enum ZeroTierServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Unknown,
}

public sealed record ZeroTierAdapter(string Name, string Description, bool IsUp, IReadOnlyList<IPAddress> IPv4Addresses);

/// <summary>Everything the inspector reads from the machine; swapped out in tests.</summary>
public interface IZeroTierHost
{
    ZeroTierServiceState GetServiceState();
    IReadOnlyList<ZeroTierAdapter> GetZeroTierAdapters();

    /// <summary>Contents of authtoken.secret, or null if missing/unreadable (it is admin-only by default).</summary>
    string? ReadAuthToken();

    /// <summary>
    /// Every distinct non-empty auth token found, best first (a per-user copy and the service's own can differ and only
    /// one of them is accepted). The inspector tries them in order until the API stops answering 401.
    /// </summary>
    IReadOnlyList<string> ReadAuthTokens() => ReadAuthToken() is { Length: > 0 } token ? [token] : [];

    /// <summary>GET http://127.0.0.1:{api port}{path} with X-ZT1-Auth; null when the API is unreachable. The port is ZeroTier's own (default 9993).</summary>
    Task<(HttpStatusCode Status, string Body)?> GetLocalApiAsync(string path, string authToken, CancellationToken cancellationToken);
}

public interface IZeroTierInspector
{
    Task<ZeroTierStatus> InspectAsync(string? networkId, Ipv4Cidr? subnet, CancellationToken cancellationToken = default);
}

/// <summary>Checks the ZeroTier One service, its virtual adapters and (when the auth token is readable) the local API.</summary>
public sealed class ZeroTierInspector : IZeroTierInspector
{
    private readonly IZeroTierHost _host;

    public ZeroTierInspector()
        : this(new WindowsZeroTierHost())
    {
    }

    public ZeroTierInspector(IZeroTierHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task<ZeroTierStatus> InspectAsync(string? networkId, Ipv4Cidr? subnet, CancellationToken cancellationToken = default)
    {
        var service = _host.GetServiceState();
        if (service == ZeroTierServiceState.NotInstalled)
        {
            return new ZeroTierStatus(ZeroTierState.NotInstalled, Detail: "ZeroTier One is not installed on this PC.");
        }
        if (service == ZeroTierServiceState.Stopped)
        {
            return new ZeroTierStatus(ZeroTierState.NotRunning, Detail: "ZeroTier One is installed but its Windows service (ZeroTierOneService) is not running.");
        }

        var adapters = _host.GetZeroTierAdapters();
        var match = adapters.Where(a => a.IsUp).SelectMany(a => a.IPv4Addresses).FirstOrDefault(ip => InSubnet(ip, subnet));

        // Optional local API: tells "not joined" apart from "waiting for the host to authorize".
        string? nodeId = null, netStatus = null;
        IPAddress? apiAddress = null;
        var apiAvailable = false;
        var apiNotJoined = false;
        string? token = null;
        (HttpStatusCode Status, string Body)? status = null;
        foreach (var candidate in _host.ReadAuthTokens().Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            // A 401 means this particular token is stale or belongs to another install; try the next one.
            token = candidate;
            status = await _host.GetLocalApiAsync("/status", candidate, cancellationToken).ConfigureAwait(false);
            if (status is not { Status: HttpStatusCode.Unauthorized })
            {
                break;
            }
        }
        if (token is not null)
        {
            if (status is { Status: HttpStatusCode.OK } s)
            {
                apiAvailable = true;
                nodeId = TryGetString(s.Body, "address");
            }
            if (apiAvailable && IsValidNetworkId(networkId))
            {
                var net = await _host.GetLocalApiAsync("/network/" + networkId!.ToLowerInvariant(), token, cancellationToken).ConfigureAwait(false);
                if (net is { Status: HttpStatusCode.NotFound })
                {
                    apiNotJoined = true;
                }
                else if (net is { Status: HttpStatusCode.OK } n)
                {
                    netStatus = TryGetString(n.Body, "status");
                    apiAddress = TryGetAssigned(n.Body, subnet);
                }
            }
        }

        ZeroTierStatus Make(ZeroTierState state, IPAddress? ip, string detail)
            => new(state, ip, detail) { NodeId = nodeId, NetworkStatus = netStatus, LocalApiAvailable = apiAvailable };

        var where = subnet is { } sn ? $" in {sn}" : string.Empty;
        if (match is not null)
        {
            return Make(ZeroTierState.Joined, match, $"This PC has ZeroTier address {match}.");
        }
        if (apiNotJoined)
        {
            return Make(ZeroTierState.NotJoined, null, $"This PC has not joined ZeroTier network {networkId}.");
        }
        if (netStatus is "ACCESS_DENIED" or "REQUESTING_CONFIGURATION")
        {
            return Make(ZeroTierState.WaitingForAuthorization, null,
                $"This PC joined network {networkId}, but the host has not authorized it yet (status {netStatus})" + (nodeId is null ? "." : $". Your node id is {nodeId}."));
        }
        if (netStatus == "NOT_FOUND")
        {
            return Make(ZeroTierState.NotJoined, null, $"ZeroTier says network {networkId} does not exist. The server list may have the wrong network id.");
        }
        if (netStatus == "OK" && apiAddress is not null)
        {
            return Make(ZeroTierState.Joined, apiAddress, $"ZeroTier assigned {apiAddress}, but the adapter is not up yet. Wait a few seconds.");
        }
        if (netStatus is not null)
        {
            return Make(ZeroTierState.Unknown, null, $"ZeroTier network status is {netStatus} and this PC has no address{where}.");
        }
        if (adapters.Count == 0)
        {
            return Make(ZeroTierState.NotJoined, null, "ZeroTier is running but has no network adapters, so this PC has not joined any network.");
        }
        return Make(ZeroTierState.Unknown, null,
            $"ZeroTier is running but this PC has no address{where}. If you just joined, the host may still need to authorize you.");
    }

    private static bool InSubnet(IPAddress ip, Ipv4Cidr? subnet)
        => subnet is { } s ? s.Contains(ip) : !IsLinkLocal(ip);

    private static bool IsLinkLocal(IPAddress ip)
    {
        var b = ip.MapToIPv4().GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    internal static bool IsValidNetworkId(string? id)
        => id is { Length: 16 } && id.All(Uri.IsHexDigit);

    private static string? TryGetString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IPAddress? TryGetAssigned(string json, Ipv4Cidr? subnet)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("assignedAddresses", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var item in arr.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                var slash = text?.IndexOf('/', StringComparison.Ordinal) ?? -1;
                if (text is not null && IPAddress.TryParse(slash >= 0 ? text[..slash] : text, out var ip)
                    && ip.AddressFamily == AddressFamily.InterNetwork && InSubnet(ip, subnet))
                {
                    return ip;
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}

/// <summary>
/// Real machine access: Service Control Manager, NIC enumeration, authtoken.secret, and the local API on the port ZeroTier
/// records in <c>zerotier-one.port</c> (9993 unless it was changed).
/// </summary>
public sealed class WindowsZeroTierHost : IZeroTierHost
{
    public const string ServiceName = "ZeroTierOneService";
    public const int DefaultApiPort = 9993;
    public const string TokenFileName = "authtoken.secret";
    public const string PortFileName = "zerotier-one.port";
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly IReadOnlyList<string> _dataDirs;
    private readonly HttpClient _client;

    public WindowsZeroTierHost()
        : this(DefaultDataDirs(), null)
    {
    }

    /// <param name="dataDirs">Directories that may hold <c>authtoken.secret</c> (and <c>zerotier-one.port</c>), best first.</param>
    /// <param name="handler">Test seam for the local API HTTP traffic; null uses the shared client.</param>
    internal WindowsZeroTierHost(IReadOnlyList<string> dataDirs, HttpMessageHandler? handler)
    {
        _dataDirs = dataDirs ?? throw new ArgumentNullException(nameof(dataDirs));
        _client = handler is null ? s_http : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    private static string[] DefaultDataDirs() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroTier"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZeroTier", "One"),
    ];

    public ZeroTierServiceState GetServiceState()
    {
        return OperatingSystem.IsWindows() ? QueryService() : ZeroTierServiceState.Unknown;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ZeroTierServiceState QueryService()
    {
        try
        {
            var svc = ServiceController.GetServices().FirstOrDefault(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
            if (svc is null)
            {
                return ZeroTierServiceState.NotInstalled;
            }
            using (svc)
            {
                return svc.Status == ServiceControllerStatus.Running ? ZeroTierServiceState.Running : ZeroTierServiceState.Stopped;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ZeroTierServiceState.Unknown;
        }
    }

    public IReadOnlyList<ZeroTierAdapter> GetZeroTierAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase))
                .Select(n => new ZeroTierAdapter(n.Name, n.Description, n.OperationalStatus == OperationalStatus.Up,
                    n.GetIPProperties().UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList()))
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    public string? ReadAuthToken() => ReadAuthTokens() is [var first, ..] ? first : null;

    public IReadOnlyList<string> ReadAuthTokens()
    {
        var tokens = new List<string>();
        foreach (var dir in _dataDirs)
        {
            // The ProgramData copy is admin-only by default; unreadable ones are skipped quietly.
            if (ReadTokenFile(dir) is { } token && !tokens.Contains(token, StringComparer.Ordinal))
            {
                tokens.Add(token);
            }
        }
        return tokens;
    }

    private static string? ReadTokenFile(string dir) => ReadTrimmed(Path.Combine(dir, TokenFileName)) is { Length: > 0 } token ? token : null;

    /// <summary>The API port from <c>zerotier-one.port</c> in <paramref name="dir"/>, or null if absent or not a valid port.</summary>
    internal static int? ReadPortFile(string dir)
        => int.TryParse(ReadTrimmed(Path.Combine(dir, PortFileName)), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    private static string? ReadTrimmed(string path)
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

    /// <summary>
    /// ZeroTier writes the port its API really listens on next to <c>authtoken.secret</c>. Prefer the data dir the token
    /// came from, then any dir that has a port file, then the default 9993.
    /// </summary>
    internal int ResolveApiPort(string authToken)
    {
        foreach (var dir in _dataDirs)
        {
            if (string.Equals(ReadTokenFile(dir), authToken, StringComparison.Ordinal) && ReadPortFile(dir) is { } own)
            {
                return own;
            }
        }
        foreach (var dir in _dataDirs)
        {
            if (ReadPortFile(dir) is { } any)
            {
                return any;
            }
        }
        return DefaultApiPort;
    }

    public async Task<(HttpStatusCode Status, string Body)?> GetLocalApiAsync(string path, string authToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(FormattableString.Invariant($"http://127.0.0.1:{ResolveApiPort(authToken)}{path}")));
            request.Headers.TryAddWithoutValidation("X-ZT1-Auth", authToken);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
