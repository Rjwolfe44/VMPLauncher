using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VmpLauncher.Net.Directory;
using VmpLauncher.Net.Doctor;
using VmpLauncher.Net.Probing;
using VmpLauncher.Net.ZeroTier;
using Xunit;

namespace VmpLauncher.Tests.Net;

internal sealed class ScriptedProbe : IServerProbe
{
    public Func<ProbeRequest, ProbeResult> Script { get; set; } = _ => new ProbeResult(ProbeOutcome.Unreachable);
    public List<ProbeRequest> Calls { get; } = [];

    public Task<ProbeResult> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request);
        return Task.FromResult(Script(request));
    }
}

internal sealed class FixedZeroTier(ZeroTierStatus status) : IZeroTierInspector
{
    public Task<ZeroTierStatus> InspectAsync(string? networkId, Ipv4Cidr? subnet, CancellationToken cancellationToken = default) => Task.FromResult(status);
}

public class ConnectionDoctorTests
{
    private const string Secret = "hunter2-Very-Secret";

    private static ServerDirectoryResult Servers(string? json = null)
        => new(ServerDirectory.Parse(Encoding.UTF8.GetBytes(json ?? ServerDirectoryTests.Json(4))), false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), "test-key");

    private static ConnectionDoctorOptions Options(ScriptedProbe probe, string? password = Secret, string? installed = "0.2.0-train.20261021.1", ZeroTierState zt = ZeroTierState.Joined) => new()
    {
        LoadServers = _ => Task.FromResult(Servers()),
        Probe = probe,
        ZeroTier = new FixedZeroTier(new ZeroTierStatus(zt, zt == ZeroTierState.Joined ? IPAddress.Parse("10.37.239.7") : null, "fake")),
        PasswordProvider = _ => ValueTask.FromResult(password),
        InstalledBuild = () => installed,
        Resolve = (h, _) => Task.FromResult(new[] { IPAddress.Parse("147.185.221.216") }),
        BindUdp = () => 50000,
    };

    private static async Task<List<DoctorStep>> Run(ConnectionDoctor d)
    {
        var all = new List<DoctorStep>();
        await foreach (var s in d.RunAsync())
        {
            all.Add(s);
        }
        return all;
    }

    [Fact]
    public async Task Healthy_run_passes_every_step_and_probes_L1_before_L2_L3()
    {
        var probe = new ScriptedProbe
        {
            Script = r => r.Level switch
            {
                ProbeLevel.Reachability => new ProbeResult(ProbeOutcome.Online, TimeSpan.FromMilliseconds(42), Reason: "Invalid password"),
                ProbeLevel.Password => new ProbeResult(ProbeOutcome.PasswordOk, TimeSpan.FromMilliseconds(40)),
                _ => new ProbeResult(ProbeOutcome.BuildReported, TimeSpan.FromMilliseconds(41), "0.2.0-train.20261021.1", "Exact build mismatch: server requires '0.2.0-train.20261021.1', client sent 'vmp-launcher-doctor'."),
            },
        };
        var d = new ConnectionDoctor(Options(probe));
        var all = await Run(d);

        Assert.All(all.Where(s => s.Status == DoctorStatus.Running), s => Assert.Contains(d.Results, f => f.Id == s.Id));
        Assert.All(d.Results, s => Assert.Equal(DoctorStatus.Pass, s.Status));
        Assert.Contains(d.Results, s => s.Id == "zerotier");
        Assert.Contains(d.Results, s => s.Id == "route.playit.build");
        Assert.Equal("build.compare", d.Results[^1].Id);
        // Priority order: playit (10) before zerotier (20); L1 always first; the real password only on L2/L3.
        Assert.Equal(("signaling-karma.tun.ply.gg", ProbeLevel.Reachability), (probe.Calls[0].Host, probe.Calls[0].Level));
        Assert.All(probe.Calls.Where(c => c.Level == ProbeLevel.Reachability), c => Assert.Null(c.Password));
        Assert.All(probe.Calls.Where(c => c.Level != ProbeLevel.Reachability), c => Assert.Equal(Secret, c.Password));
    }

    [Fact]
    public async Task Playit_timeout_uses_plain_language_and_skips_password_checks()
    {
        var probe = new ScriptedProbe { Script = _ => new ProbeResult(ProbeOutcome.Unreachable, Error: "No answer within 10 s.") };
        var d = new ConnectionDoctor(Options(probe, zt: ZeroTierState.WaitingForAuthorization));
        await Run(d);

        var playit = d.Results.Single(s => s.Id == "route.playit.reach");
        Assert.Equal(DoctorStatus.Fail, playit.Status);
        Assert.Equal("The server or its Playit tunnel is offline. Ask the host to check that VMP and Playit are running.", playit.Summary);
        Assert.Equal(DoctorFix.AskHostAuthorize, d.Results.Single(s => s.Id == "route.zerotier.reach").Fix);
        Assert.Equal(DoctorFix.AskHostAuthorize, d.Results.Single(s => s.Id == "zerotier").Fix);
        Assert.DoesNotContain(probe.Calls, c => c.Level != ProbeLevel.Reachability);
        // No L3 possible, so the comparison falls back to the server list's requiredBuild.
        var compare = d.Results.Single(s => s.Id == "build.compare");
        Assert.Equal(DoctorStatus.Pass, compare.Status);
        Assert.Contains("from the server list", compare.Summary);
    }

    [Fact]
    public async Task Wrong_password_and_old_client_suggest_fixes()
    {
        var probe = new ScriptedProbe
        {
            Script = r => r.Level == ProbeLevel.Reachability ? new ProbeResult(ProbeOutcome.Online, TimeSpan.FromMilliseconds(30)) : new ProbeResult(ProbeOutcome.WrongPassword, Reason: "Invalid password"),
        };
        var d = new ConnectionDoctor(Options(probe, installed: "0.1.9"));
        await Run(d);
        Assert.Equal(DoctorFix.EnterPassword, d.Results.First(s => s.Id.EndsWith(".password", StringComparison.Ordinal)).Fix);
        var compare = d.Results.Single(s => s.Id == "build.compare");
        Assert.Equal(DoctorStatus.Fail, compare.Status);
        Assert.Equal(DoctorFix.UpdatePack, compare.Fix);
    }

    [Fact]
    public async Task No_password_skips_L2_L3_with_EnterPassword_hint()
    {
        var probe = new ScriptedProbe { Script = _ => new ProbeResult(ProbeOutcome.Online, TimeSpan.FromMilliseconds(30)) };
        var d = new ConnectionDoctor(Options(probe, password: null));
        await Run(d);
        Assert.All(probe.Calls, c => Assert.Equal(ProbeLevel.Reachability, c.Level));
        Assert.Contains(d.Results, s => s.Fix == DoctorFix.EnterPassword && s.Status == DoctorStatus.Warn);
    }

    [Fact]
    public async Task Failures_in_dns_udp_and_server_list_are_steps_not_exceptions()
    {
        var o = Options(new ScriptedProbe());
        var d = new ConnectionDoctor(new ConnectionDoctorOptions
        {
            LoadServers = _ => throw new HttpRequestExceptionLike("offline"),
            Probe = o.Probe,
            Resolve = (_, _) => throw new SocketException((int)SocketError.HostNotFound),
            BindUdp = () => throw new SocketException((int)SocketError.AccessDenied),
        });
        await Run(d);
        Assert.Equal(DoctorStatus.Fail, d.Results.Single(s => s.Id == "dns.internet").Status);
        Assert.Equal(DoctorFix.CheckFirewall, d.Results.Single(s => s.Id == "udp.bind").Fix);
        Assert.Equal(DoctorFix.RefreshServers, d.Results.Single(s => s.Id == "servers.list").Fix);
        Assert.Equal(DoctorStatus.Skipped, d.Results[^1].Status);
    }

    [Fact]
    public async Task Report_redacts_password_and_user_profile()
    {
        var probe = new ScriptedProbe
        {
            Script = r => r.Level == ProbeLevel.Reachability
                ? new ProbeResult(ProbeOutcome.Online, TimeSpan.FromMilliseconds(30), Error: @"log at C:\Users\Chevy\AppData\Local\VmpLauncher\logs\x.log")
                : new ProbeResult(ProbeOutcome.Rejected, Reason: $"server echoed '{Secret}' from D:/Users/vlad/stuff"),
        };
        var d = new ConnectionDoctor(Options(probe));
        await Run(d);
        var report = d.BuildReport();

        Assert.StartsWith("# VMP Connection Doctor report", report);
        Assert.DoesNotContain(Secret, report);
        Assert.DoesNotContain(@"Users\Chevy", report);
        Assert.DoesNotContain("Users/vlad", report);
        Assert.DoesNotContain(Environment.UserName + @"\AppData", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\AppData\Local\VmpLauncher", report);
        Assert.Contains(ReportRedactor.Redacted, report);
        Assert.All(d.Results, s => Assert.DoesNotContain(Secret, (s.Summary ?? "") + s.Details));
    }

    [Fact]
    public void Redactor_handles_json_escaped_paths_and_the_real_profile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = ReportRedactor.Redact($"a {profile}\\x b \"C:\\\\Users\\\\Someone\\\\y\" pw=abc", ["abc"]);
        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Someone", text);
        Assert.Contains("pw=" + ReportRedactor.Redacted, text);
    }

    [Fact]
    public async Task End_to_end_against_fake_lidgren_server()
    {
        using var server = new FakeVmpServer("pw-123");
        var json = ServerDirectoryTests.Json(1)
            .Replace("\"host\": \"signaling-karma.tun.ply.gg\", \"port\": 18113", $"\"host\": \"127.0.0.1\", \"port\": {server.Port}", StringComparison.Ordinal)
            .Replace("\"kind\": \"zerotier\"", "\"kind\": \"lan\"", StringComparison.Ordinal)
            .Replace("\"host\": \"10.37.239.191\"", "\"host\": \"127.0.0.1\"", StringComparison.Ordinal)
            .Replace("\"port\": 8800", $"\"port\": {server.Port}", StringComparison.Ordinal);
        var d = new ConnectionDoctor(new ConnectionDoctorOptions
        {
            LoadServers = _ => Task.FromResult(Servers(json)),
            PasswordProvider = _ => ValueTask.FromResult<string?>("pw-123"),
            InstalledBuild = () => FakeVmpServer.DefaultBuild + "-compiled",
            Resolve = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
            ProbeTimeout = TimeSpan.FromSeconds(5),
        });
        await Run(d);
        Assert.All(d.Results, s => Assert.True(s.Status == DoctorStatus.Pass, $"{s.Id}: {s.Status} {s.Summary} {s.Details}"));
        Assert.Equal(2, server.Handshakes.Count);
        Assert.DoesNotContain("pw-123", d.BuildReport());
    }

    private sealed class HttpRequestExceptionLike(string message) : IOException(message);
}
