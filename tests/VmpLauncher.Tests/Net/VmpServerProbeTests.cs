using System.IO;
using System.Net;
using System.Net.Sockets;
using VmpLauncher.Net.Probing;
using VmpLauncher.Net.Protocol;
using Xunit;

namespace VmpLauncher.Tests.Net;

public class VmpServerProbeTests
{
    private const string Password = "correct horse";
    private static readonly VmpServerProbe Probe = new();

    private static ProbeRequest Req(int port, ProbeLevel level, string? password = null) => new("127.0.0.1", port, level)
    {
        Password = password,
        Timeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task L1_wrong_password_deny_means_online_and_never_sends_the_real_password()
    {
        using var server = new FakeVmpServer(Password);
        var r = await Probe.ProbeAsync(Req(server.Port, ProbeLevel.Reachability, Password));

        Assert.Equal(ProbeOutcome.Online, r.Outcome);
        Assert.NotNull(r.RoundTrip);
        Assert.Contains("Invalid password", r.Reason);
        var hail = Assert.Single(server.Hails);
        Assert.StartsWith(VmpProtocol.StatusProbeHailPrefix, hail);
        Assert.DoesNotContain(Password, hail);
        Assert.Equal(0, server.ApprovedCount);
    }

    [Fact]
    public async Task L1_against_passwordless_server_is_OnlineNoPassword()
    {
        using var server = new FakeVmpServer(password: null);
        var r = await Probe.ProbeAsync(Req(server.Port, ProbeLevel.Reachability));
        Assert.Equal(ProbeOutcome.OnlineNoPassword, r.Outcome);
        Assert.Empty(server.Handshakes);
    }

    [Fact]
    public async Task L2_correct_password_is_PasswordOk_without_handshake()
    {
        using var server = new FakeVmpServer(Password);
        var r = await Probe.ProbeAsync(Req(server.Port, ProbeLevel.Password, Password));
        Assert.Equal(ProbeOutcome.PasswordOk, r.Outcome);
        Assert.Equal(Password, Assert.Single(server.Hails));
        Assert.Empty(server.Handshakes);
    }

    [Fact]
    public async Task L2_wrong_password_is_WrongPassword()
    {
        using var server = new FakeVmpServer(Password);
        var r = await Probe.ProbeAsync(Req(server.Port, ProbeLevel.Password, "nope"));
        Assert.Equal(ProbeOutcome.WrongPassword, r.Outcome);
    }

    [Fact]
    public async Task L2_without_password_is_a_local_error()
    {
        var r = await Probe.ProbeAsync(Req(1, ProbeLevel.Password));
        Assert.Equal(ProbeOutcome.Error, r.Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task L3_reports_server_build_from_reply_or_disconnect_reason(bool replyMessage)
    {
        using var server = new FakeVmpServer(Password, "0.2.0-train.20261021.1", replyMessage);
        var r = await Probe.ProbeAsync(Req(server.Port, ProbeLevel.Build, Password));

        Assert.Equal(ProbeOutcome.BuildReported, r.Outcome);
        Assert.Equal("0.2.0-train.20261021.1", r.ServerBuild);
        var hs = Assert.Single(server.Handshakes);
        Assert.Equal(("launcher", "VMP.VladMultiplayer", "vmp-launcher-doctor"), (hs.PlayerName, hs.ForkId, hs.Build));
        Assert.True(Guid.TryParse(hs.UniqueIdentifier, out _));
    }

    [Fact]
    public async Task L3_wrong_password_is_WrongPassword()
    {
        using var server = new FakeVmpServer(Password);
        var r = await Probe.ProbeAsync(Req(server.Port, ProbeLevel.Build, "nope"));
        Assert.Equal(ProbeOutcome.WrongPassword, r.Outcome);
        Assert.Empty(server.Handshakes);
    }

    [Fact]
    public async Task Silent_host_hits_overall_timeout_as_Unreachable()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var r = await Probe.ProbeAsync(Req(port, ProbeLevel.Reachability) with { Timeout = TimeSpan.FromMilliseconds(700) });
        Assert.Equal(ProbeOutcome.Unreachable, r.Outcome);
        Assert.Null(r.RoundTrip);
    }

    [Fact]
    public async Task Lidgren_giving_up_on_handshake_maps_to_Unreachable()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var r = await Probe.ProbeAsync(Req(port, ProbeLevel.Reachability) with
        {
            MaximumHandshakeAttempts = 2,
            ResendHandshakeIntervalSeconds = 0.15f,
        });
        Assert.Equal(ProbeOutcome.Unreachable, r.Outcome);
        Assert.Contains("no response", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dns_failure_is_AddressNotFound()
    {
        var probe = new VmpServerProbe((_, _) => throw new SocketException((int)SocketError.HostNotFound));
        var r = await probe.ProbeAsync(new ProbeRequest("no-such-host.invalid", 8800));
        Assert.Equal(ProbeOutcome.AddressNotFound, r.Outcome);
    }

    [Fact]
    public async Task Resolution_prefers_IPv4()
    {
        using var server = new FakeVmpServer(Password);
        var probe = new VmpServerProbe((_, _) => Task.FromResult(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback }));
        var r = await probe.ProbeAsync(new ProbeRequest("vmp.example", server.Port) { Timeout = TimeSpan.FromSeconds(5) });
        Assert.Equal(ProbeOutcome.Online, r.Outcome);
        Assert.StartsWith("127.0.0.1:", r.RemoteAddress);
    }

    [Fact]
    public void Request_ToString_never_prints_password()
    {
        var text = new ProbeRequest("h", 1, ProbeLevel.Password) { Password = "s3cr3t" }.ToString();
        Assert.DoesNotContain("s3cr3t", text);
    }

    [Theory]
    [InlineData(ProbeLevel.Reachability, "Invalid password", false, false, ProbeOutcome.Online)]
    [InlineData(ProbeLevel.Password, "Invalid password", false, false, ProbeOutcome.WrongPassword)]
    [InlineData(ProbeLevel.Reachability, "Failed to establish connection - no response from remote host", false, false, ProbeOutcome.Unreachable)]
    [InlineData(ProbeLevel.Build, "Exact build mismatch: server requires '1.2.3', client sent 'x'.", true, true, ProbeOutcome.BuildReported)]
    [InlineData(ProbeLevel.Reachability, "Server full", false, false, ProbeOutcome.Rejected)]
    [InlineData(ProbeLevel.Build, "Connection timed out", true, true, ProbeOutcome.Rejected)]
    public void MapDisconnect_table(ProbeLevel level, string reason, bool connected, bool handshake, ProbeOutcome expected)
        => Assert.Equal(expected, VmpServerProbe.MapDisconnect(level, reason, connected, handshake, TimeSpan.Zero).Outcome);
}

public class VmpHandshakeEncoderTests
{
    // Golden bytes were produced by a throwaway console that compiled VMPMod's REAL LmpCommon message classes
    // (HandshakeCliMsg + HandshakeRequestMsgData/HandshakeSrvMsg + HandshakeReplyMsgData) and Lidgren sources
    // read-only, serialized them with these exact field values through MessageBase.Serialize, and hex-dumped
    // NetOutgoingMessage.Data[0..LengthBytes].
    private static byte[] Golden(string name) => Convert.FromHexString(File.ReadAllText(FakeVmpServer.Fixture(name)).Trim());

    [Fact]
    public void Request_encoder_matches_real_LmpCommon_bytes()
    {
        var bytes = VmpHandshakeEncoder.EncodeRequest(new HandshakeRequest(
            "launcher", "00000000-0000-0000-0000-000000000001", "VMP.VladMultiplayer", "vmp-launcher-doctor")
        {
            SentTime = 638000000000000000L,
            MajorVersion = 0,
            MinorVersion = 2,
            BuildVersion = 0,
        });
        Assert.Equal(Convert.ToHexStringLower(Golden("handshake-request.hex")), Convert.ToHexStringLower(bytes));
    }

    [Fact]
    public void Reply_decoder_reads_real_LmpCommon_bytes()
    {
        var reply = VmpHandshakeEncoder.TryDecodeReply(Golden("handshake-reply-build-mismatch.hex"));
        Assert.NotNull(reply);
        Assert.Equal(HandshakeReplyCode.ProtocolBuildMismatch, reply!.Value.Response);
        Assert.Equal("0.2.0-train.20261021.1", VmpProtocol.TryParseServerBuild(reply.Value.Reason));
    }

    [Fact]
    public void Reply_decoder_rejects_other_or_truncated_messages()
    {
        var golden = Golden("handshake-reply-build-mismatch.hex");
        Assert.Null(VmpHandshakeEncoder.TryDecodeReply(golden.AsSpan(0, 30)));
        var other = (byte[])golden.Clone();
        other[0] = 7;
        Assert.Null(VmpHandshakeEncoder.TryDecodeReply(other));
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0-compiled", true)]
    [InlineData("0.2.0-train.1", "0.2.0-train.1", true)]
    [InlineData("0.2.0-train.1", "0.2.0-train.2", false)]
    [InlineData("", "", false)]
    public void Build_compatibility_mirrors_SessionAdmission(string a, string b, bool expected)
        => Assert.Equal(expected, VmpProtocol.AreBuildsCompatible(a, b));
}
