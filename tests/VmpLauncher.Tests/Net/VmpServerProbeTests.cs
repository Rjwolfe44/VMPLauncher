using System.IO;
using System.Net;
using System.Net.Sockets;
using Lidgren.Network;
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
    [InlineData(ProbeLevel.Build, "[VMP] Incompatible client version. Exact build mismatch: server requires '1.2.3', client sent 'x'.", true, true, ProbeOutcome.BuildReported)]
    [InlineData(ProbeLevel.Build, "Protocol/fork mismatch: server requires 'VMP.VladMultiplayer', client sent 'x'.", true, true, ProbeOutcome.Rejected)]
    [InlineData(ProbeLevel.Reachability, "Server full", false, false, ProbeOutcome.ServerFull)]
    [InlineData(ProbeLevel.Password, "Server full", false, false, ProbeOutcome.ServerFull)]
    [InlineData(ProbeLevel.Build, "Server full", false, false, ProbeOutcome.ServerFull)]
    [InlineData(ProbeLevel.Build, "Connection timed out", true, true, ProbeOutcome.Rejected)]
    [InlineData(ProbeLevel.Reachability, "Something else entirely", false, false, ProbeOutcome.Rejected)]
    public void MapDisconnect_table(ProbeLevel level, string reason, bool connected, bool handshake, ProbeOutcome expected)
        => Assert.Equal(expected, VmpServerProbe.MapDisconnect(level, reason, connected, handshake, TimeSpan.Zero).Outcome);

    [Fact]
    public void ServerFull_counts_as_a_server_answering_and_ProbeOutcome_values_were_not_renumbered()
    {
        Assert.True(new ProbeResult(ProbeOutcome.ServerFull).ServerAnswered);
        Assert.Equal(8, (int)ProbeOutcome.Error);
        Assert.Equal(9, (int)ProbeOutcome.ServerFull);
    }

    [Theory]
    [InlineData(ProbeLevel.Reachability, null)]
    [InlineData(ProbeLevel.Password, Password)]
    [InlineData(ProbeLevel.Build, Password)]
    public async Task Full_server_is_ServerFull_at_every_level_not_Rejected(ProbeLevel level, string? password)
    {
        using var server = new FakeVmpServer(Password, maximumConnections: 0);
        var r = await Probe.ProbeAsync(Req(server.Port, level, password));

        Assert.Equal(ProbeOutcome.ServerFull, r.Outcome);
        Assert.Equal("Server full", r.Reason);
        Assert.True(r.ServerAnswered);
        Assert.Empty(server.Hails); // refused before the password was even looked at
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fork_mismatch_rejection_is_not_mistaken_for_a_server_build(bool replyMessage)
    {
        // SessionAdmission's fork rejection also says "server requires '...'", but it names the fork id.
        using var server = new FakeVmpServer(Password, sendReplyMessage: replyMessage,
            reject: hs => (4, $"Protocol/fork mismatch: server requires 'VMP.SomeOtherFork', client sent '{hs.ForkId}'."));
        var r = await Probe.ProbeAsync(Req(server.Port, ProbeLevel.Build, Password));

        Assert.Equal(ProbeOutcome.Rejected, r.Outcome);
        Assert.Null(r.ServerBuild);
        Assert.Contains("VMP.SomeOtherFork", r.Reason);
    }

    [Fact]
    public async Task Server_full_handshake_reply_maps_to_ServerFull()
    {
        using var server = new FakeVmpServer(Password, reject: _ => (2, "Server full"));
        var r = await Probe.ProbeAsync(Req(server.Port, ProbeLevel.Build, Password));
        Assert.Equal(ProbeOutcome.ServerFull, r.Outcome);
    }

    [Fact]
    public async Task Failed_start_closes_the_socket_and_reports_a_local_error()
    {
        NetClient? created = null;
        var probe = new VmpServerProbe(null,
            req =>
            {
                var config = VmpServerProbe.CreateConfiguration(req);
                config.DualStack = false;
                config.LocalAddress = IPAddress.Parse("192.0.2.123"); // TEST-NET-1: not an address of this machine, so Bind() throws
                return config;
            },
            c => created = c);

        var r = await probe.ProbeAsync(Req(1234, ProbeLevel.Reachability));

        Assert.Equal(ProbeOutcome.Error, r.Outcome);
        Assert.NotNull(created);
        Assert.NotNull(created!.Socket); // Lidgren had already created it when Bind() failed
        Assert.Throws<ObjectDisposedException>(() => created.Socket!.Available);
    }

    [Fact]
    public void Server_requires_regex_only_matches_the_exact_build_message()
    {
        Assert.Equal("1.2.3", VmpProtocol.TryParseServerBuild("Exact build mismatch: server requires '1.2.3', client sent 'x'."));
        Assert.Null(VmpProtocol.TryParseServerBuild("Protocol/fork mismatch: server requires 'VMP.VladMultiplayer', client sent 'x'."));
        Assert.Null(VmpProtocol.TryParseServerBuild("the server requires 'abc'"));
        Assert.Null(VmpProtocol.TryParseServerBuild(null));
    }
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
