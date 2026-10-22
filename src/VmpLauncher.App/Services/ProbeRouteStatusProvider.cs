using VmpLauncher.App.ViewModels;
using VmpLauncher.Net.Probing;

namespace VmpLauncher.App.Services;

/// <summary>Live status dots on the Play page: an L1 reachability probe (wrong-password hail, no server side effects).</summary>
public sealed class ProbeRouteStatusProvider : IRouteStatusProvider
{
    private readonly VmpServerProbe _probe = new();

    public async Task<(string Status, string Kind, string Rtt)> CheckAsync(RouteCardViewModel route, CancellationToken ct)
    {
        if (route.Kind == "lan" && !LooksLikeLocalSubnet(route.Host))
        {
            return ("Home network only", "neutral", string.Empty);
        }
        var result = await _probe.ProbeAsync(new ProbeRequest(route.Host, route.Port, ProbeLevel.Reachability) { Timeout = TimeSpan.FromSeconds(6) }, ct).ConfigureAwait(false);
        var rtt = result.RoundTrip is { } r ? $"{(int)r.TotalMilliseconds} ms" : string.Empty;
        return result.Outcome switch
        {
            ProbeOutcome.Online => ("Online", "ok", rtt),
            ProbeOutcome.OnlineNoPassword => ("Online (no password)", "ok", rtt),
            ProbeOutcome.Unreachable => route.Kind == "zerotier" ? ("Unreachable (ZeroTier joined?)", "error", string.Empty) : ("Unreachable", "error", string.Empty),
            ProbeOutcome.AddressNotFound => ("Address not found", "warn", string.Empty),
            _ => (result.Outcome.ToString(), "warn", rtt),
        };
    }

    private static bool LooksLikeLocalSubnet(string host)
    {
        if (!System.Net.IPAddress.TryParse(host, out var ip))
        {
            return true;
        }
        var b = ip.GetAddressBytes();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    continue;
                }
                var a = ua.Address.GetAddressBytes();
                if (a[0] == b[0] && a[1] == b[1] && a[2] == b[2])
                {
                    return true;
                }
            }
        }
        return false;
    }
}
