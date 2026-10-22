using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using VmpLauncher.Net.Directory;
using VmpLauncher.Net.Probing;
using VmpLauncher.Net.Protocol;
using VmpLauncher.Net.ZeroTier;

namespace VmpLauncher.Net.Doctor;

/// <summary>Inputs for <see cref="ConnectionDoctor"/>. Only <see cref="LoadServers"/> is required.</summary>
public sealed class ConnectionDoctorOptions
{
    /// <summary>Loads (and verifies) the server list. Failures become a failed step, not an exception.</summary>
    public required Func<CancellationToken, Task<ServerDirectoryResult>> LoadServers { get; init; }

    public IServerProbe Probe { get; init; } = new VmpServerProbe();

    /// <summary>Null skips the ZeroTier step even when a zerotier route exists.</summary>
    public IZeroTierInspector? ZeroTier { get; init; } = new ZeroTierInspector();

    /// <summary>Returns the saved server password, or null. Only called once, and only if a route answered L1.</summary>
    public Func<CancellationToken, ValueTask<string?>>? PasswordProvider { get; init; }

    /// <summary>Exact build of the VMP client installed in the active profile, or null if unknown.</summary>
    public Func<string?>? InstalledBuild { get; init; }

    public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = (h, ct) => Dns.GetHostAddressesAsync(h, ct);

    /// <summary>Opens a local UDP socket; returns the bound port. Throws <see cref="SocketException"/> on failure.</summary>
    public Func<int> BindUdp { get; init; } = DefaultBindUdp;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Server lists older than this are flagged.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromDays(1);

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public string DnsCanaryHost { get; init; } = "raw.githubusercontent.com";

    private static int DefaultBindUdp()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
}

/// <summary>
/// Step-by-step connection diagnosis for players: DNS, local UDP, server list freshness, ZeroTier, then per route
/// L1 reachability and (with a saved password) L2 password + L3 server build, and finally installed vs server build.
/// Every emitted step is already redacted; <see cref="BuildReport"/> renders a shareable Markdown report.
/// </summary>
public sealed class ConnectionDoctor
{
    private readonly ConnectionDoctorOptions _o;
    private readonly List<DoctorStep> _final = [];
    private readonly object _gate = new();
    private DateTimeOffset _startedAt;
    private ServerDirectoryResult? _servers;
    private bool _passwordRequested;
    private string? _password;

    public ConnectionDoctor(ConnectionDoctorOptions options)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Final (non-Running) steps of the last run, in order.</summary>
    public IReadOnlyList<DoctorStep> Results
    {
        get
        {
            lock (_gate)
            {
                return _final.ToArray();
            }
        }
    }

    public async IAsyncEnumerable<DoctorStep> RunAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _final.Clear();
        }
        _startedAt = _o.Time.GetUtcNow();
        _servers = null;
        _passwordRequested = false;
        _password = null;
        try
        {
            // 1. Internet name lookup.
            yield return Running("dns.internet", "Internet name lookup");
            yield return Final(await DnsStepAsync("dns.internet", "Internet name lookup", _o.DnsCanaryHost, null, cancellationToken).ConfigureAwait(false));

            // 2. Local UDP.
            yield return Running("udp.bind", "Local network (UDP)");
            yield return Final(UdpStep());

            // 3. Server list.
            yield return Running("servers.list", "Server list");
            yield return Final(await ServerListStepAsync(cancellationToken).ConfigureAwait(false));
            var directory = _servers?.Directory;
            if (directory is null)
            {
                yield return Final(new DoctorStep("routes", "Server routes", DoctorStatus.Skipped, "Skipped because the server list could not be loaded.", Fix: DoctorFix.RefreshServers));
                yield break;
            }
            var routes = directory.RoutesByPriority.ToList();

            foreach (var route in routes.Where(r => !IPAddress.TryParse(r.Host, out _)))
            {
                var id = $"dns.route.{route.Id}";
                var title = $"{RouteLabel(route)}: address lookup";
                yield return Running(id, title);
                yield return Final(await DnsStepAsync(id, title, route.Host, route, cancellationToken).ConfigureAwait(false));
            }

            // 4. ZeroTier.
            ZeroTierStatus? zt = null;
            var ztRoute = routes.FirstOrDefault(r => r.Kind == RouteKind.Zerotier);
            if (ztRoute is not null)
            {
                yield return Running("zerotier", "ZeroTier");
                DoctorStep step;
                if (_o.ZeroTier is null)
                {
                    step = new DoctorStep("zerotier", "ZeroTier", DoctorStatus.Skipped, "ZeroTier check is not available here.");
                }
                else
                {
                    zt = await _o.ZeroTier.InspectAsync(ztRoute.RequiresSoftware?.NetworkId, ParseSubnet(ztRoute), cancellationToken).ConfigureAwait(false);
                    step = ZeroTierStep(zt, ztRoute, hasOtherRoutes: routes.Any(r => r.Kind != RouteKind.Zerotier));
                }
                yield return Final(step);
            }

            // 5. Routes: L1, then L2/L3 when a password is available.
            string? serverBuild = null;
            foreach (var route in routes)
            {
                var label = RouteLabel(route);
                var reachId = $"route.{route.Id}.reach";
                yield return Running(reachId, $"{label}: server reachable");
                var l1 = await ProbeAsync(route, ProbeLevel.Reachability, null, cancellationToken).ConfigureAwait(false);
                yield return Final(ReachStep(reachId, $"{label}: server reachable", route, l1, zt));
                if (l1.Outcome is not (ProbeOutcome.Online or ProbeOutcome.OnlineNoPassword))
                {
                    continue;
                }

                var password = await GetPasswordAsync(cancellationToken).ConfigureAwait(false);
                var pwId = $"route.{route.Id}.password";
                var buildId = $"route.{route.Id}.build";
                if (string.IsNullOrEmpty(password))
                {
                    // Warn once; later routes just note the skip.
                    var required = directory.Server.PasswordRequired && l1.Outcome != ProbeOutcome.OnlineNoPassword
                        && !Results.Any(s => s.Fix == DoctorFix.EnterPassword);
                    yield return Final(new DoctorStep(pwId, $"{label}: password", required ? DoctorStatus.Warn : DoctorStatus.Skipped,
                        required ? "No saved password, so the password and version checks were skipped. Enter the password from the host." : "No saved password; skipped.",
                        Fix: required ? DoctorFix.EnterPassword : null));
                    continue;
                }

                yield return Running(pwId, $"{label}: password");
                var l2 = await ProbeAsync(route, ProbeLevel.Password, password, cancellationToken).ConfigureAwait(false);
                yield return Final(PasswordStep(pwId, $"{label}: password", l2));
                if (l2.Outcome != ProbeOutcome.PasswordOk)
                {
                    continue;
                }

                yield return Running(buildId, $"{label}: server version");
                var l3 = await ProbeAsync(route, ProbeLevel.Build, password, cancellationToken).ConfigureAwait(false);
                if (l3.Outcome == ProbeOutcome.BuildReported)
                {
                    serverBuild ??= l3.ServerBuild;
                }
                yield return Final(BuildProbeStep(buildId, $"{label}: server version", l3));
            }

            // 6. Installed client vs server.
            yield return Running("build.compare", "Your VMP version");
            yield return Final(CompareStep(serverBuild, directory));
        }
        finally
        {
            _password = null;
        }
    }

    /// <summary>Shareable Markdown summary of the last run. Never contains the password or the Windows user name.</summary>
    public string BuildReport()
    {
        var steps = Results;
        var sb = new StringBuilder();
        sb.AppendLine("# VMP Connection Doctor report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Generated: {_o.Time.GetUtcNow():yyyy-MM-dd HH:mm:ss} UTC (run started {_startedAt:HH:mm:ss} UTC)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Launcher: {typeof(ConnectionDoctor).Assembly.GetName().Version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- OS: {Environment.OSVersion.VersionString}");
        if (_servers is { } s)
        {
            var d = s.Directory;
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Server: {d.Server.DisplayName} ({d.Server.Id}), pack {d.PackId}, list sequence {d.Sequence} issued {d.IssuedAt:yyyy-MM-dd HH:mm} UTC{(s.IsFromCache ? ", from cache" : string.Empty)}");
        }
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Result: {steps.Count(x => x.Status == DoctorStatus.Pass)} passed, {steps.Count(x => x.Status == DoctorStatus.Warn)} warnings, {steps.Count(x => x.Status == DoctorStatus.Fail)} failed, {steps.Count(x => x.Status == DoctorStatus.Skipped)} skipped.");
        sb.AppendLine();
        sb.AppendLine("| Result | Check | Summary |");
        sb.AppendLine("|---|---|---|");
        foreach (var step in steps)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {Tag(step.Status)} | {Cell(step.Title)} | {Cell(step.Summary)} |");
        }
        var detailed = steps.Where(x => !string.IsNullOrWhiteSpace(x.Details) || x.Fix is not null).ToList();
        if (detailed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Details");
            foreach (var step in detailed)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"### {Tag(step.Status)} {step.Title} (`{step.Id}`)");
                if (!string.IsNullOrWhiteSpace(step.Details))
                {
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(step.Details.Replace("```", "'''", StringComparison.Ordinal));
                    sb.AppendLine("```");
                }
                if (step.Fix is { } fix)
                {
                    sb.AppendLine();
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Suggested fix: {fix}");
                }
            }
        }
        return ReportRedactor.Redact(sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), [_password]);

        static string Tag(DoctorStatus st) => st switch
        {
            DoctorStatus.Pass => "PASS",
            DoctorStatus.Warn => "WARN",
            DoctorStatus.Fail => "FAIL",
            DoctorStatus.Skipped => "SKIP",
            _ => "....",
        };
        static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    private DoctorStep Running(string id, string title) => new(id, title, DoctorStatus.Running, "Checking...");

    private DoctorStep Final(DoctorStep step)
    {
        var clean = step with
        {
            Summary = ReportRedactor.Redact(step.Summary, [_password]),
            Details = step.Details is null ? null : ReportRedactor.Redact(step.Details, [_password]),
        };
        lock (_gate)
        {
            _final.Add(clean);
        }
        return clean;
    }

    private async ValueTask<string?> GetPasswordAsync(CancellationToken ct)
    {
        if (!_passwordRequested)
        {
            _passwordRequested = true;
            _password = _o.PasswordProvider is null ? null : await _o.PasswordProvider(ct).ConfigureAwait(false);
        }
        return _password;
    }

    private Task<ProbeResult> ProbeAsync(ServerRoute route, ProbeLevel level, string? password, CancellationToken ct)
        => _o.Probe.ProbeAsync(new ProbeRequest(route.Host, route.Port, level) { Password = password, Timeout = _o.ProbeTimeout }, ct);

    private async Task<DoctorStep> DnsStepAsync(string id, string title, string host, ServerRoute? route, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var addresses = await _o.Resolve(host, cts.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }
            return new DoctorStep(id, title, DoctorStatus.Pass, $"Found {host}.", $"{host} -> {string.Join(", ", addresses.Select(a => a.ToString()))}");
        }
        catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var detail = $"{host}: {(e is SocketException se ? se.SocketErrorCode.ToString() : "lookup timed out")}";
            return route is null
                ? new DoctorStep(id, title, DoctorStatus.Fail, "Your PC can't look up internet addresses. Check that you're connected to the internet.", detail, DoctorFix.CheckFirewall)
                : new DoctorStep(id, title, DoctorStatus.Fail,
                    route.Kind == RouteKind.Playit
                        ? $"The Playit address {host} doesn't exist right now. The host may have changed the tunnel; refresh the server list."
                        : $"Couldn't find the address {host}. The server list may be out of date.",
                    detail, DoctorFix.RefreshServers);
        }
    }

    private DoctorStep UdpStep()
    {
        try
        {
            var port = _o.BindUdp();
            return new DoctorStep("udp.bind", "Local network (UDP)", DoctorStatus.Pass, "Your PC can open a UDP connection.", $"Bound UDP port {port}.");
        }
        catch (SocketException e)
        {
            return new DoctorStep("udp.bind", "Local network (UDP)", DoctorStatus.Fail,
                "Your PC couldn't open a UDP connection, which the game needs. Security software may be blocking it.", $"{e.SocketErrorCode}: {e.Message}", DoctorFix.CheckFirewall);
        }
    }

    private async Task<DoctorStep> ServerListStepAsync(CancellationToken ct)
    {
        const string id = "servers.list", title = "Server list";
        try
        {
            var s = await _o.LoadServers(ct).ConfigureAwait(false);
            _servers = s;
            var d = s.Directory;
            var details = $"pack={d.PackId} sequence={d.Sequence} issuedAt={d.IssuedAt:O} age={FormatAge(s.Age)} fromCache={s.IsFromCache} key={s.KeyId ?? "-"} routes={string.Join(", ", d.Routes.Select(r => $"{r.Id}({r.Kind} {r.Host}:{r.Port})"))}"
                + (s.Warning is null ? string.Empty : "\n" + s.Warning);
            if (d.Maintenance is { } m && (m.Until is null || m.Until > _o.Time.GetUtcNow()))
            {
                return new DoctorStep(id, title, DoctorStatus.Warn,
                    $"The host has announced maintenance: {m.Message}" + (m.Until is { } u ? $" (until {u:yyyy-MM-dd HH:mm} UTC)." : string.Empty), details);
            }
            if (s.IsFromCache)
            {
                return new DoctorStep(id, title, DoctorStatus.Warn, $"Couldn't download the latest server list, so a saved copy from {FormatAge(s.Age)} ago is being used.", details, DoctorFix.RefreshServers);
            }
            if (s.Age > _o.StaleAfter)
            {
                return new DoctorStep(id, title, DoctorStatus.Warn, $"The server list is {FormatAge(s.Age)} old.", details, DoctorFix.RefreshServers);
            }
            return new DoctorStep(id, title, DoctorStatus.Pass, $"Server list is up to date ({d.Routes.Count} way{(d.Routes.Count == 1 ? "" : "s")} to connect).", details);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new DoctorStep(id, title, DoctorStatus.Fail, "Couldn't load the server list. Check your internet connection and try again.", e.Message, DoctorFix.RefreshServers);
        }
    }

    private static DoctorStep ZeroTierStep(ZeroTierStatus zt, ServerRoute route, bool hasOtherRoutes)
    {
        const string id = "zerotier", title = "ZeroTier";
        var bad = hasOtherRoutes ? DoctorStatus.Warn : DoctorStatus.Fail;
        var details = $"state={zt.State} network={route.RequiresSoftware?.NetworkId ?? "-"} subnet={route.RequiresSoftware?.Subnet ?? "-"} node={zt.NodeId ?? "-"} api={zt.LocalApiAvailable} status={zt.NetworkStatus ?? "-"}\n{zt.Detail}";
        var alt = hasOtherRoutes ? " (Only needed for the ZeroTier route.)" : string.Empty;
        return zt.State switch
        {
            ZeroTierState.Joined => new DoctorStep(id, title, DoctorStatus.Pass, $"ZeroTier is connected (your address is {zt.Address}).", details),
            ZeroTierState.NotInstalled => new DoctorStep(id, title, bad, "ZeroTier isn't installed." + alt, details, DoctorFix.InstallZeroTier),
            ZeroTierState.NotRunning => new DoctorStep(id, title, bad, "ZeroTier is installed but not running. Start ZeroTier One (or reinstall it)." + alt, details, DoctorFix.InstallZeroTier),
            ZeroTierState.NotJoined => new DoctorStep(id, title, bad, $"This PC hasn't joined the server's ZeroTier network ({route.RequiresSoftware?.NetworkId})." + alt, details, DoctorFix.JoinZeroTier),
            ZeroTierState.WaitingForAuthorization => new DoctorStep(id, title, bad,
                "You've joined the ZeroTier network, but the host still needs to approve this PC." + (zt.NodeId is null ? string.Empty : $" Send them your node id {zt.NodeId}."), details, DoctorFix.AskHostAuthorize),
            _ => new DoctorStep(id, title, DoctorStatus.Warn, "ZeroTier is running, but this PC doesn't have a server network address yet. The host may need to approve you.", details, DoctorFix.AskHostAuthorize),
        };
    }

    private static DoctorStep ReachStep(string id, string title, ServerRoute route, ProbeResult r, ZeroTierStatus? zt)
    {
        var details = ProbeDetails(route, r);
        switch (r.Outcome)
        {
            case ProbeOutcome.Online:
                return new DoctorStep(id, title, DoctorStatus.Pass, $"The server is online ({Ms(r.RoundTrip)} response).", details);
            case ProbeOutcome.OnlineNoPassword:
                return new DoctorStep(id, title, DoctorStatus.Warn, "The server is online but accepts any password. Let the host know.", details);
            case ProbeOutcome.AddressNotFound:
                return new DoctorStep(id, title, DoctorStatus.Fail, $"Couldn't find the address {route.Host}. The server list may be out of date.", details, DoctorFix.RefreshServers);
            case ProbeOutcome.Unreachable:
                return route.Kind switch
                {
                    RouteKind.Playit => new DoctorStep(id, title, DoctorStatus.Fail,
                        "The server or its Playit tunnel is offline. Ask the host to check that VMP and Playit are running.", details),
                    RouteKind.Zerotier when zt is { State: not ZeroTierState.Joined } => new DoctorStep(id, title, DoctorStatus.Fail,
                        "Can't reach the server over ZeroTier because ZeroTier isn't connected on this PC (see the ZeroTier check).", details,
                        zt.State switch
                        {
                            ZeroTierState.NotInstalled or ZeroTierState.NotRunning => DoctorFix.InstallZeroTier,
                            ZeroTierState.NotJoined => DoctorFix.JoinZeroTier,
                            _ => DoctorFix.AskHostAuthorize,
                        }),
                    RouteKind.Zerotier => new DoctorStep(id, title, DoctorStatus.Fail,
                        "ZeroTier is connected, but the server didn't answer. The server may be offline, or the host's PC isn't online in ZeroTier.", details),
                    RouteKind.Lan => new DoctorStep(id, title, DoctorStatus.Fail,
                        "The server didn't answer on the local network. This route only works on the host's home network.", details),
                    _ => new DoctorStep(id, title, DoctorStatus.Fail,
                        $"The server didn't answer. It may be offline, or a firewall is blocking UDP port {route.Port}.", details, DoctorFix.CheckFirewall),
                };
            case ProbeOutcome.Rejected:
                return new DoctorStep(id, title, DoctorStatus.Warn, "Something answered, but it didn't behave like the VMP server.", details);
            default:
                return new DoctorStep(id, title, DoctorStatus.Fail, "The check couldn't run on this PC.", details, DoctorFix.CheckFirewall);
        }
    }

    private static DoctorStep PasswordStep(string id, string title, ProbeResult r) => r.Outcome switch
    {
        ProbeOutcome.PasswordOk => new DoctorStep(id, title, DoctorStatus.Pass, "Your saved password is correct.", ProbeDetails(null, r)),
        ProbeOutcome.WrongPassword => new DoctorStep(id, title, DoctorStatus.Fail, "The server rejected your saved password. Enter the current password from the host.", ProbeDetails(null, r), DoctorFix.EnterPassword),
        ProbeOutcome.Unreachable => new DoctorStep(id, title, DoctorStatus.Warn, "The server stopped answering during the password check. Try again in a minute.", ProbeDetails(null, r)),
        _ => new DoctorStep(id, title, DoctorStatus.Warn, "The password check didn't give a clear answer.", ProbeDetails(null, r)),
    };

    private static DoctorStep BuildProbeStep(string id, string title, ProbeResult r) => r.Outcome == ProbeOutcome.BuildReported
        ? new DoctorStep(id, title, DoctorStatus.Pass, $"The server is running VMP {r.ServerBuild}.", ProbeDetails(null, r))
        : new DoctorStep(id, title, DoctorStatus.Warn, "Couldn't read the server's VMP version.", ProbeDetails(null, r));

    private DoctorStep CompareStep(string? serverBuild, ServerDirectory directory)
    {
        const string id = "build.compare", title = "Your VMP version";
        var installed = _o.InstalledBuild?.Invoke();
        var expected = serverBuild ?? (string.IsNullOrWhiteSpace(directory.Server.RequiredBuild) ? null : directory.Server.RequiredBuild);
        var source = serverBuild is not null ? "reported by the server" : "from the server list";
        var details = $"installed={installed ?? "-"} server={serverBuild ?? "-"} serverList={directory.Server.RequiredBuild}";
        if (serverBuild is not null && !string.IsNullOrWhiteSpace(directory.Server.RequiredBuild) && !VmpProtocol.AreBuildsCompatible(serverBuild, directory.Server.RequiredBuild))
        {
            details += "\nThe server list's requiredBuild differs from what the server reports; the host should republish the server list.";
        }
        if (expected is null)
        {
            return new DoctorStep(id, title, DoctorStatus.Skipped, "The server's version is unknown, so it couldn't be compared.", details);
        }
        if (_o.InstalledBuild is null)
        {
            return new DoctorStep(id, title, DoctorStatus.Skipped, $"No game folder was selected, so your VMP version wasn't checked. The server needs {expected}.", details);
        }
        if (installed is null)
        {
            return new DoctorStep(id, title, DoctorStatus.Warn, $"Couldn't find the VMP client in your game folder. The server needs {expected}.", details, DoctorFix.UpdatePack);
        }
        return VmpProtocol.AreBuildsCompatible(installed, expected)
            ? new DoctorStep(id, title, DoctorStatus.Pass, $"Your VMP version {installed} matches the server ({source}).", details)
            : new DoctorStep(id, title, DoctorStatus.Fail, $"You have VMP {installed}, but the server needs {expected}. Update the pack.", details, DoctorFix.UpdatePack);
    }

    private static string ProbeDetails(ServerRoute? route, ProbeResult r)
    {
        var sb = new StringBuilder();
        if (route is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"route={route.Id} kind={route.Kind} target={route.Host}:{route.Port} ");
        }
        sb.Append(CultureInfo.InvariantCulture, $"outcome={r.Outcome} rtt={Ms(r.RoundTrip)} address={r.RemoteAddress ?? "-"}");
        if (r.ServerBuild is not null) sb.Append(CultureInfo.InvariantCulture, $" serverBuild={r.ServerBuild}");
        if (r.Reason is not null) sb.Append(CultureInfo.InvariantCulture, $"\nreason: {r.Reason}");
        if (r.Error is not null) sb.Append(CultureInfo.InvariantCulture, $"\nerror: {r.Error}");
        return sb.ToString();
    }

    private static Ipv4Cidr? ParseSubnet(ServerRoute route)
        => Ipv4Cidr.TryParse(route.RequiresSoftware?.Subnet, out var c) ? c : null;

    private static string RouteLabel(ServerRoute route) => string.IsNullOrWhiteSpace(route.Name) ? route.Id : route.Name;

    private static string Ms(TimeSpan? t) => t is { } v ? $"{v.TotalMilliseconds:0} ms" : "-";

    private static string FormatAge(TimeSpan age) => age.TotalMinutes < 1 ? "under a minute"
        : age.TotalHours < 1 ? $"{age.TotalMinutes:0} min"
        : age.TotalDays < 2 ? $"{age.TotalHours:0} h"
        : $"{age.TotalDays:0} days";
}
