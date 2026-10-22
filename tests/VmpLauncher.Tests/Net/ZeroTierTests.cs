using System.IO;
using System.Net;
using VmpLauncher.Net.ZeroTier;
using Xunit;

namespace VmpLauncher.Tests.Net;

public class Ipv4CidrTests
{
    [Theory]
    [InlineData("10.37.239.0/24", "10.37.239.191", true)]
    [InlineData("10.37.239.0/24", "10.37.240.1", false)]
    [InlineData("10.37.239.191/24", "10.37.239.5", true)] // host bits ignored
    [InlineData("192.168.50.115/32", "192.168.50.115", true)]
    [InlineData("192.168.50.115/32", "192.168.50.116", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("10.0.0.0/8", "::ffff:10.1.2.3", true)] // IPv4-mapped IPv6
    [InlineData("10.0.0.0/8", "fe80::1", false)]
    [InlineData("10.37.239.0/23", "10.37.238.1", true)]
    [InlineData("10.37.239.0/23", "10.37.240.1", false)]
    public void Contains(string cidr, string ip, bool expected)
        => Assert.Equal(expected, Ipv4Cidr.Parse(cidr).Contains(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("10.37.239.191/24", "10.37.239.0/24")]
    [InlineData("10.1.2.3", "10.1.2.3/32")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, Ipv4Cidr.Parse(input).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0/8")]
    [InlineData("fe80::/10")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("banana/24")]
    public void Rejects_invalid(string? text) => Assert.False(Ipv4Cidr.TryParse(text, out _));
}

internal sealed class FakeZeroTierHost : IZeroTierHost
{
    public ZeroTierServiceState Service { get; set; } = ZeroTierServiceState.Running;
    public List<ZeroTierAdapter> Adapters { get; } = [];
    public string? Token { get; set; }
    public Dictionary<string, (HttpStatusCode, string)> Api { get; } = new();

    public ZeroTierServiceState GetServiceState() => Service;
    public IReadOnlyList<ZeroTierAdapter> GetZeroTierAdapters() => Adapters;
    public string? ReadAuthToken() => Token;

    public Task<(HttpStatusCode Status, string Body)?> GetLocalApiAsync(string path, string authToken, CancellationToken cancellationToken)
        => Task.FromResult<(HttpStatusCode, string)?>(Api.TryGetValue(path, out var v) ? v : null);
}

public class ZeroTierInspectorTests
{
    private const string Net = "633e31d8a237e3f9";
    private static readonly Ipv4Cidr Subnet = Ipv4Cidr.Parse("10.37.239.0/24");

    private static Task<ZeroTierStatus> Inspect(FakeZeroTierHost host) => new ZeroTierInspector(host).InspectAsync(Net, Subnet);

    [Fact]
    public async Task Not_installed_and_not_running()
    {
        Assert.Equal(ZeroTierState.NotInstalled, (await Inspect(new FakeZeroTierHost { Service = ZeroTierServiceState.NotInstalled })).State);
        Assert.Equal(ZeroTierState.NotRunning, (await Inspect(new FakeZeroTierHost { Service = ZeroTierServiceState.Stopped })).State);
    }

    [Fact]
    public async Task Adapter_in_subnet_is_Joined()
    {
        var host = new FakeZeroTierHost();
        host.Adapters.Add(new("ZeroTier One [8056c2e21c000001]", "ZeroTier Virtual Port", true, [IPAddress.Parse("10.99.0.4")]));
        host.Adapters.Add(new("ZeroTier One [633e31d8a237e3f9]", "ZeroTier Virtual Port", true, [IPAddress.Parse("10.37.239.42")]));
        var s = await Inspect(host);
        Assert.Equal(ZeroTierState.Joined, s.State);
        Assert.Equal(IPAddress.Parse("10.37.239.42"), s.Address);
    }

    [Fact]
    public async Task Api_distinguishes_not_joined_and_waiting()
    {
        var host = new FakeZeroTierHost { Token = "tok" };
        host.Api["/status"] = (HttpStatusCode.OK, """{"address":"abcdef0123","online":true}""");
        host.Api["/network/" + Net] = (HttpStatusCode.NotFound, "{}");
        Assert.Equal(ZeroTierState.NotJoined, (await Inspect(host)).State);

        host.Api["/network/" + Net] = (HttpStatusCode.OK, """{"status":"ACCESS_DENIED","assignedAddresses":[]}""");
        var waiting = await Inspect(host);
        Assert.Equal(ZeroTierState.WaitingForAuthorization, waiting.State);
        Assert.Equal("abcdef0123", waiting.NodeId);
        Assert.Contains("abcdef0123", waiting.Detail);
    }

    [Fact]
    public async Task Without_api_or_adapters_is_NotJoined_and_with_foreign_adapter_is_Unknown()
    {
        var host = new FakeZeroTierHost();
        Assert.Equal(ZeroTierState.NotJoined, (await Inspect(host)).State);
        host.Adapters.Add(new("ZeroTier One [x]", "ZeroTier Virtual Port", true, [IPAddress.Parse("169.254.10.1")]));
        Assert.Equal(ZeroTierState.Unknown, (await Inspect(host)).State);
    }
}
