using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace VmpLauncher.Net.ZeroTier;

/// <summary>An IPv4 network in CIDR form (e.g. <c>10.37.239.0/24</c>). Host bits in the input are ignored.</summary>
public readonly record struct Ipv4Cidr
{
    private Ipv4Cidr(uint network, int prefixLength)
    {
        PrefixLength = prefixLength;
        Mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        NetworkValue = network & Mask;
    }

    public int PrefixLength { get; }
    private uint Mask { get; }
    private uint NetworkValue { get; }

    public IPAddress Network
    {
        get
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, NetworkValue);
            return new IPAddress(b);
        }
    }

    public static Ipv4Cidr Parse(string text)
        => TryParse(text, out var c) ? c : throw new FormatException($"'{text}' is not an IPv4 CIDR such as 10.37.239.0/24.");

    public static bool TryParse([NotNullWhen(true)] string? text, out Ipv4Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Trim().Split('/');
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }
        var prefix = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix is < 0 or > 32))
        {
            return false;
        }
        // IPAddress.TryParse accepts shorthand like "10" or "10.1"; require four dotted octets.
        if (parts[0].Count(ch => ch == '.') != 3 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        cidr = new Ipv4Cidr(ToUInt32(ip), prefix);
        return true;
    }

    /// <summary>True when <paramref name="address"/> (IPv4 or IPv4-mapped IPv6) is inside this network.</summary>
    public bool Contains(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return address.AddressFamily == AddressFamily.InterNetwork && (ToUInt32(address) & Mask) == NetworkValue;
    }

    public override string ToString() => $"{Network}/{PrefixLength}";

    private static uint ToUInt32(IPAddress ip)
    {
        Span<byte> b = stackalloc byte[4];
        ip.TryWriteBytes(b, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }
}
