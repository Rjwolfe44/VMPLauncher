using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace VmpLauncher.Net.Directory;

/// <summary>
/// A way to reach the pack's server that the player added on their own PC (a home-network address, another tunnel).
/// It lives in the launcher's settings, is never published, and uses the password saved on the Play page.
/// </summary>
public sealed record MyServer(string Name, string Host, int Port);

/// <summary>The outcome of checking what the player typed into the Add server dialog.</summary>
/// <param name="Server">The server to save, or null.</param>
/// <param name="Error">What is wrong, or null when it is fine or merely not filled in yet.</param>
public sealed record MyServerCheck(MyServer? Server, string? Error);

/// <summary>An address that is already in the list: a name, a host and a port.</summary>
public sealed record KnownRoute(string Name, string Host, int Port);

public static partial class MyServers
{
    public const int DefaultPort = 8800;
    public const int MaxNameLength = 40;

    /// <summary>
    /// Checks a name and an address ("host" or "host:port"; port 8800 when left out) against the routes already known.
    /// A blank field gives no error (the dialog just keeps its Save button off), so the player is not scolded before typing.
    /// </summary>
    public static MyServerCheck Check(string? name, string? address, IEnumerable<KnownRoute> known)
    {
        var routes = known.ToList();
        var n = (name ?? string.Empty).Trim();
        var a = (address ?? string.Empty).Trim();

        string? error = null;
        if (n.Length > MaxNameLength)
        {
            error = $"Keep the name to {MaxNameLength} characters or fewer.";
        }
        else if (n.Any(c => char.IsControl(c) || c == '"'))
        {
            error = "The name can't contain quotes or control characters.";
        }
        else if (n.Length > 0 && routes.Any(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase)))
        {
            error = "There is already a server with that name.";
        }

        string host = string.Empty;
        var port = DefaultPort;
        if (error is null && a.Length > 0 && !TryParseAddress(a, out host, out port, out error))
        {
            return new MyServerCheck(null, error);
        }
        if (error is null && a.Length > 0 && routes.FirstOrDefault(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase) && r.Port == port) is { } same)
        {
            error = $"That address is already in the list as \"{same.Name}\".";
        }
        if (error is not null || n.Length == 0 || a.Length == 0)
        {
            return new MyServerCheck(null, error);
        }
        return new MyServerCheck(new MyServer(n, host, port), null);
    }

    /// <summary>Splits "host", "host:port" or "[v6]:port" and checks that the host looks like an address or a host name.</summary>
    public static bool TryParseAddress(string address, out string host, out int port, out string? error)
    {
        host = string.Empty;
        port = DefaultPort;
        error = null;
        var text = address.Trim();
        if (text.Contains("://", StringComparison.Ordinal) || text.Contains('/') || text.Any(char.IsWhiteSpace))
        {
            error = "Enter just the address, like 192.168.1.20 (add :port if it isn't 8800).";
            return false;
        }

        string portText = string.Empty;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0)
            {
                error = "That address is missing its closing bracket.";
                return false;
            }
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':'))
                {
                    error = "After the closing bracket only :port is allowed.";
                    return false;
                }
                portText = rest[1..];
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':');
            host = text[..colon];
            portText = text[(colon + 1)..];
        }
        else
        {
            host = text;
        }

        if (text.Contains(':') && !text.StartsWith('[') && text.Count(c => c == ':') > 1 && !IPAddress.TryParse(host, out _))
        {
            error = "That doesn't look like an address.";
            return false;
        }
        if (portText.Length > 0 && (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            error = "The port must be a number from 1 to 65535.";
            return false;
        }
        if (portText.Length == 0)
        {
            port = DefaultPort;
        }
        if (host.Length == 0 || host.Length > 253 || (!IPAddress.TryParse(host, out _) && !HostName().IsMatch(host)))
        {
            error = "That doesn't look like an address or a host name.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// How the Play page labels a route: Playit tunnels, private home-network ranges, everything else direct.
    /// (10.x is left direct: it could be a home network or ZeroTier.)
    /// </summary>
    public static string Kind(string host)
    {
        if (host.EndsWith(".ply.gg", StringComparison.OrdinalIgnoreCase))
        {
            return "playit";
        }
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if ((b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 169 && b[1] == 254))
                {
                    return "lan";
                }
            }
            return "direct";
        }
        return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || !host.Contains('.')
            ? "lan"
            : "direct";
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();
}
