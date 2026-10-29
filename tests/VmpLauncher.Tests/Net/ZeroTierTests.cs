using System.IO;
using System.Net;
using System.Net.Http;
using VmpLauncher.Net.ZeroTier;
using Xunit;

namespace VmpLauncher.Tests.Net;

public class Ipv4CidrTests
{
    [Theory]
    [InlineData("10.37.239.0/24", "10.37.239.191", true)]
    [InlineData("10.37.239.0/24", "10.37.240.1", false)]
    [InlineData("10.37.239.191/24", "10.37.239.5", true)] // host bits ignored
    [InlineData("192.168.1.20/32", "192.168.1.20", true)]
    [InlineData("192.168.1.20/32", "192.168.1.21", false)]
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

    /// <summary>When non-empty these are returned by ReadAuthTokens (in order) instead of <see cref="Token"/>.</summary>
    public List<string> Tokens { get; } = [];

    /// <summary>When set, any other token gets 401 Unauthorized, like ZeroTier does.</summary>
    public string? ValidToken { get; set; }

    public List<(string Path, string Token)> Calls { get; } = [];
    public Dictionary<string, (HttpStatusCode, string)> Api { get; } = new();

    public ZeroTierServiceState GetServiceState() => Service;
    public IReadOnlyList<ZeroTierAdapter> GetZeroTierAdapters() => Adapters;
    public string? ReadAuthToken() => Token ?? (Tokens.Count > 0 ? Tokens[0] : null);
    public IReadOnlyList<string> ReadAuthTokens() => Tokens.Count > 0 ? Tokens : Token is null ? [] : [Token];

    public Task<(HttpStatusCode Status, string Body)?> GetLocalApiAsync(string path, string authToken, CancellationToken cancellationToken)
    {
        Calls.Add((path, authToken));
        if (ValidToken is not null && authToken != ValidToken)
        {
            return Task.FromResult<(HttpStatusCode, string)?>((HttpStatusCode.Unauthorized, string.Empty));
        }
        return Task.FromResult<(HttpStatusCode, string)?>(Api.TryGetValue(path, out var v) ? v : null);
    }
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

    [Fact]
    public async Task A_401_on_the_first_token_moves_on_to_the_next_token()
    {
        var host = new FakeZeroTierHost { ValidToken = "good" };
        host.Tokens.AddRange(["stale", "good"]);
        host.Api["/status"] = (HttpStatusCode.OK, """{"address":"abcdef0123","online":true}""");
        host.Api["/network/" + Net] = (HttpStatusCode.OK, """{"status":"ACCESS_DENIED","assignedAddresses":[]}""");

        var s = await Inspect(host);

        Assert.True(s.LocalApiAvailable);
        Assert.Equal(ZeroTierState.WaitingForAuthorization, s.State);
        Assert.Equal("abcdef0123", s.NodeId);
        Assert.Equal([("/status", "stale"), ("/status", "good"), ("/network/" + Net, "good")], host.Calls);
    }

    [Fact]
    public async Task Stops_at_the_first_token_the_api_accepts()
    {
        var host = new FakeZeroTierHost { ValidToken = "first" };
        host.Tokens.AddRange(["first", "second"]);
        host.Api["/status"] = (HttpStatusCode.OK, """{"address":"abcdef0123"}""");
        host.Api["/network/" + Net] = (HttpStatusCode.NotFound, "{}");

        Assert.Equal(ZeroTierState.NotJoined, (await Inspect(host)).State);
        Assert.DoesNotContain(host.Calls, c => c.Token == "second");
    }

    [Fact]
    public async Task When_every_token_is_refused_the_api_counts_as_unavailable()
    {
        var host = new FakeZeroTierHost { ValidToken = "none-of-these" };
        host.Tokens.AddRange(["a", "b"]);
        host.Api["/status"] = (HttpStatusCode.OK, """{"address":"abcdef0123"}""");

        var s = await Inspect(host);

        Assert.False(s.LocalApiAvailable);
        Assert.Null(s.NodeId);
        Assert.Equal(["a", "b"], host.Calls.Select(c => c.Token).ToArray());
        Assert.DoesNotContain(host.Calls, c => c.Path.StartsWith("/network/", StringComparison.Ordinal));
    }
}

public sealed class WindowsZeroTierHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-zt-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string DataDir(string name, string? token, string? port)
    {
        var dir = Path.Combine(_root, name);
        System.IO.Directory.CreateDirectory(dir);
        if (token is not null) File.WriteAllText(Path.Combine(dir, WindowsZeroTierHost.TokenFileName), token + "\r\n");
        if (port is not null) File.WriteAllText(Path.Combine(dir, WindowsZeroTierHost.PortFileName), port);
        return dir;
    }

    [Fact]
    public void Reads_every_distinct_token_in_order_and_skips_missing_or_empty_ones()
    {
        var host = new WindowsZeroTierHost([DataDir("user", "tok-a", null), DataDir("empty", "  ", null), Path.Combine(_root, "absent"), DataDir("svc", "tok-b", null), DataDir("dup", "tok-a", null)], null);
        Assert.Equal(["tok-a", "tok-b"], host.ReadAuthTokens().ToArray());
        Assert.Equal("tok-a", host.ReadAuthToken());
        Assert.Null(new WindowsZeroTierHost([Path.Combine(_root, "absent")], null).ReadAuthToken());
    }

    [Fact]
    public void Api_port_comes_from_the_port_file_next_to_the_token()
    {
        var host = new WindowsZeroTierHost([DataDir("user", "tok-a", "9994\n"), DataDir("svc", "tok-b", "40123")], null);
        Assert.Equal(9994, host.ResolveApiPort("tok-a"));
        Assert.Equal(40123, host.ResolveApiPort("tok-b"));
    }

    [Fact]
    public void Api_port_falls_back_to_any_port_file_then_to_9993()
    {
        var host = new WindowsZeroTierHost([DataDir("user", "tok-a", null), DataDir("svc", "tok-b", "40123")], null);
        Assert.Equal(40123, host.ResolveApiPort("tok-a")); // the token's own dir has no port file; ProgramData's is readable
        Assert.Equal(40123, host.ResolveApiPort("unknown-token"));

        var none = new WindowsZeroTierHost([DataDir("only", "tok", null)], null);
        Assert.Equal(WindowsZeroTierHost.DefaultApiPort, none.ResolveApiPort("tok"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-5")]
    public void Garbage_port_files_are_ignored(string content)
    {
        var host = new WindowsZeroTierHost([DataDir("d", "tok", content)], null);
        Assert.Equal(9993, host.ResolveApiPort("tok"));
    }

    [Fact]
    public async Task Local_api_requests_go_to_the_recorded_port_with_the_token_header()
    {
        var http = new FakeHttp();
        http.Routes["http://127.0.0.1:40123/status"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"address":"abcdef0123"}""") };
        var host = new WindowsZeroTierHost([DataDir("d", "tok-a", "40123")], http);

        var reply = await host.GetLocalApiAsync("/status", "tok-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, reply!.Value.Status);
        var request = Assert.Single(http.Requests);
        Assert.Equal(40123, request.RequestUri!.Port);
        Assert.Equal("/status", request.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task Local_api_uses_9993_when_there_is_no_port_file_and_reports_401()
    {
        var http = new FakeHttp();
        http.Routes["http://127.0.0.1:9993/status"] = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var host = new WindowsZeroTierHost([DataDir("d", "tok-a", null)], http);

        var reply = await host.GetLocalApiAsync("/status", "tok-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, reply!.Value.Status);
    }
}
