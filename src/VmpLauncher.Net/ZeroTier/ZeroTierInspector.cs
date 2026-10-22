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

    /// <summary>GET http://127.0.0.1:9993{path} with X-ZT1-Auth; null when the API is unreachable.</summary>
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
        var token = _host.ReadAuthToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            var status = await _host.GetLocalApiAsync("/status", token, cancellationToken).ConfigureAwait(false);
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

/// <summary>Real machine access: Service Control Manager, NIC enumeration, authtoken.secret, local API on :9993.</summary>
public sealed class WindowsZeroTierHost : IZeroTierHost
{
    public const string ServiceName = "ZeroTierOneService";
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(3) };

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

    public string? ReadAuthToken()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroTier", "authtoken.secret"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZeroTier", "One", "authtoken.secret"),
        };
        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    var token = File.ReadAllText(path).Trim();
                    if (token.Length > 0)
                    {
                        return token;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The ProgramData copy is admin-only by default; skip quietly.
            }
        }
        return null;
    }

    public async Task<(HttpStatusCode Status, string Body)?> GetLocalApiAsync(string path, string authToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1:9993" + path));
            request.Headers.TryAddWithoutValidation("X-ZT1-Auth", authToken);
            using var response = await s_http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
