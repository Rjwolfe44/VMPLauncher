using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lidgren.Network;
using VmpLauncher.Net.Protocol;

namespace VmpLauncher.Net.Probing;

/// <summary>
/// Talks to a VMP server over Lidgren/UDP exactly like the game client does (app id "VMP", MTU 1200, hail = password),
/// one <see cref="NetClient"/> per probe. L1 never sends the real password; L2/L3 never complete authentication, so
/// none of them trigger the server's "last player left" backup.
/// </summary>
public sealed class VmpServerProbe : IServerProbe
{
    private static readonly object PeerStartGate = new();
    private static readonly TimeSpan HandshakeResendInterval = TimeSpan.FromSeconds(1);
    private const int MaxHandshakeSends = 5;

    /// <summary>
    /// After the L3 reply arrives we wait (briefly) for the server's own disconnect instead of sending a bye: the
    /// server still holds its reliable reply, and our ack + bye landing together would hit the same Lidgren
    /// reset-then-ack race on the server's network thread.
    /// </summary>
    private static readonly TimeSpan ServerDisconnectGrace = TimeSpan.FromSeconds(2);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<ProbeRequest, NetPeerConfiguration> _configure;
    private readonly Action<NetClient>? _clientCreated;

    public VmpServerProbe()
        : this(null)
    {
    }

    /// <param name="resolver">DNS override for tests; defaults to <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</param>
    public VmpServerProbe(Func<string, CancellationToken, Task<IPAddress[]>>? resolver)
        : this(resolver, null, null)
    {
    }

    /// <param name="resolver">See the public constructor.</param>
    /// <param name="configure">Test seam: builds the Lidgren configuration (e.g. an unbindable local address).</param>
    /// <param name="clientCreated">Test seam: receives each <see cref="NetClient"/> right after it is created.</param>
    internal VmpServerProbe(Func<string, CancellationToken, Task<IPAddress[]>>? resolver, Func<ProbeRequest, NetPeerConfiguration>? configure, Action<NetClient>? clientCreated)
    {
        _resolve = resolver ?? ((h, ct) => Dns.GetHostAddressesAsync(h, ct));
        _configure = configure ?? CreateConfiguration;
        _clientCreated = clientCreated;
    }

    public async Task<ProbeResult> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Host) || request.Port is <= 0 or > 65535)
        {
            return new ProbeResult(ProbeOutcome.Error, Error: "A host name and a port between 1 and 65535 are required.");
        }
        if (request.Level != ProbeLevel.Reachability && string.IsNullOrEmpty(request.Password))
        {
            return new ProbeResult(ProbeOutcome.Error, Error: "This check needs the server password.");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(request.Timeout);

        IPAddress address;
        try
        {
            address = await ResolveAsync(request.Host.Trim(), budget.Token).ConfigureAwait(false);
        }
        catch (SocketException e)
        {
            return new ProbeResult(ProbeOutcome.AddressNotFound, Error: $"Could not look up '{request.Host}' ({e.SocketErrorCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(ProbeOutcome.AddressNotFound, Error: $"Looking up '{request.Host}' timed out.");
        }

        var endpoint = new IPEndPoint(address, request.Port);
        try
        {
            var result = await RunAsync(request, endpoint, budget.Token, cancellationToken).ConfigureAwait(false);
            return result with { RemoteAddress = endpoint.ToString() };
        }
        catch (Exception e) when (e is SocketException or NetException or InvalidOperationException)
        {
            return new ProbeResult(ProbeOutcome.Error, Error: e.Message) { RemoteAddress = endpoint.ToString() };
        }
    }

    private async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            return literal;
        }
        var all = await _resolve(host, ct).ConfigureAwait(false);
        // Prefer IPv4: Playit/ZeroTier/LAN routes are IPv4 and VMP servers commonly bind 0.0.0.0.
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6)
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    internal static NetPeerConfiguration CreateConfiguration(ProbeRequest request)
    {
        var dual = Socket.OSSupportsIPv6;
        var ping = Math.Min(1f, request.ConnectionTimeoutSeconds);
        return new NetPeerConfiguration(VmpProtocol.AppIdentifier)
        {
            LocalAddress = dual ? IPAddress.IPv6Any : IPAddress.Any,
            DualStack = dual,
            Port = 0,
            MaximumTransmissionUnit = VmpProtocol.Mtu,
            AutoExpandMTU = false,
            PingInterval = ping,
            ConnectionTimeout = Math.Max(ping, request.ConnectionTimeoutSeconds),
            MaximumHandshakeAttempts = Math.Max(1, request.MaximumHandshakeAttempts),
            ResendHandshakeInterval = Math.Max(0.05f, request.ResendHandshakeIntervalSeconds),
        };
    }

    private async Task<ProbeResult> RunAsync(ProbeRequest request, IPEndPoint endpoint, CancellationToken budget, CancellationToken caller)
    {
        var client = new NetClient(_configure(request));
        _clientCreated?.Invoke(client);
        var started = false;
        var connected = false;
        var handshakesSent = 0;
        TimeSpan? firstAnswer = null;
        TimeSpan lastHandshake = default;
        ProbeResult? pending = null;
        TimeSpan pendingSince = default;
        var clock = new Stopwatch();
        try
        {
            // Lidgren's NetUtility.ComputeSHAHash uses one static SHA256 instance; concurrent peer start-ups throw
            // "Concurrent operations from multiple threads". Starting peers is cheap, so serialize just that step.
            lock (PeerStartGate)
            {
                client.Start();
            }
            started = true;
            var hail = client.CreateMessage();
            // L1 must never reveal (or even need) the real password: a random, clearly-labelled dummy hail.
            hail.Write(request.Level == ProbeLevel.Reachability
                ? VmpProtocol.StatusProbeHailPrefix + Guid.NewGuid().ToString("N")
                : request.Password);
            clock.Start();
            client.Connect(endpoint, hail);

            while (true)
            {
                if (pending is not null && clock.Elapsed - pendingSince > ServerDisconnectGrace)
                {
                    return pending;
                }
                if (pending is null && handshakesSent is > 0 and < MaxHandshakeSends && clock.Elapsed - lastHandshake > HandshakeResendInterval)
                {
                    SendDoctorHandshake(client);
                    handshakesSent++;
                    lastHandshake = clock.Elapsed;
                }

                var msg = client.ReadMessage();
                if (msg is null)
                {
                    await Task.Delay(5, budget).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                    {
                        var status = (NetConnectionStatus)msg.ReadByte();
                        var reason = msg.ReadString();
                        if (status == NetConnectionStatus.Connected)
                        {
                            connected = true;
                            firstAnswer ??= clock.Elapsed;
                            switch (request.Level)
                            {
                                case ProbeLevel.Reachability:
                                    return new ProbeResult(ProbeOutcome.OnlineNoPassword, firstAnswer, Reason: "The server accepted a random password.");
                                case ProbeLevel.Password:
                                    return new ProbeResult(ProbeOutcome.PasswordOk, firstAnswer);
                                default:
                                    SendDoctorHandshake(client);
                                    handshakesSent = 1;
                                    lastHandshake = clock.Elapsed;
                                    continue;
                            }
                        }
                        if (status == NetConnectionStatus.Disconnected)
                        {
                            return pending ?? MapDisconnect(request.Level, reason, connected, handshakesSent > 0, firstAnswer ?? clock.Elapsed);
                        }
                    }
                    else if (msg.MessageType == NetIncomingMessageType.Data && handshakesSent > 0 && pending is null)
                    {
                        if (VmpHandshakeEncoder.TryDecodeReply(msg.Data.AsSpan(0, msg.LengthBytes)) is { } r)
                        {
                            // HandshookSuccessfully is impossible with our build string; we leave either way. A build is only
                            // trusted from the exact-build rejection (reply code AND message form): the fork-mismatch
                            // rejection also says "server requires '...'" but names the fork id, not a build.
                            pending = r.Response == HandshakeReplyCode.ProtocolBuildMismatch && VmpProtocol.TryParseServerBuild(r.Reason) is { } build
                                ? new ProbeResult(ProbeOutcome.BuildReported, firstAnswer, build, r.Reason)
                                : r.Response == HandshakeReplyCode.ServerFull
                                    ? new ProbeResult(ProbeOutcome.ServerFull, firstAnswer, Reason: r.Reason)
                                    : new ProbeResult(ProbeOutcome.Rejected, firstAnswer, Reason: $"{r.Response}: {r.Reason}");
                            pendingSince = clock.Elapsed;
                        }
                    }
                }
                finally
                {
                    client.Recycle(msg);
                }
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return pending ?? (connected
                ? new ProbeResult(ProbeOutcome.Error, firstAnswer, Error: $"Connected, but the server did not answer the handshake within {request.Timeout.TotalSeconds:0.#} s.")
                : new ProbeResult(ProbeOutcome.Unreachable, Error: $"No answer within {request.Timeout.TotalSeconds:0.#} s."));
        }
        finally
        {
            if (started)
            {
                client.Shutdown("vmp-launcher probe complete");
                // Give the network thread a moment to send the bye so the server frees the slot immediately.
                for (var i = 0; i < 40 && client.Status != NetPeerStatus.NotRunning; i++)
                {
                    await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                }
            }
            else
            {
                // Start() failed (e.g. while binding). No network thread exists, so Shutdown() would only set a flag
                // nobody reads and the socket Lidgren already created would stay open until finalization.
                CloseUnstartedSocket(client);
            }
        }
    }

    private static void CloseUnstartedSocket(NetClient client)
    {
        try
        {
            client.Socket?.Close();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Already gone; nothing else to release.
        }
    }

    internal static ProbeResult MapDisconnect(ProbeLevel level, string? reason, bool wasConnected, bool handshakeSent, TimeSpan elapsed)
    {
        if (VmpProtocol.IsServerFull(reason))
        {
            // Lidgren refuses on slot count before it looks at the password: the server is up and VMP, just full.
            return new ProbeResult(ProbeOutcome.ServerFull, elapsed, Reason: reason);
        }
        if (VmpProtocol.IsInvalidPassword(reason))
        {
            return level == ProbeLevel.Reachability
                ? new ProbeResult(ProbeOutcome.Online, elapsed, Reason: reason)
                : new ProbeResult(ProbeOutcome.WrongPassword, elapsed, Reason: reason);
        }
        if (handshakeSent && VmpProtocol.TryParseServerBuild(reason) is { } build)
        {
            return new ProbeResult(ProbeOutcome.BuildReported, elapsed, build, reason);
        }
        if (!wasConnected && reason is not null &&
            (reason.Contains("no response", StringComparison.OrdinalIgnoreCase) || reason.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
        {
            return new ProbeResult(ProbeOutcome.Unreachable, Reason: reason);
        }
        return new ProbeResult(ProbeOutcome.Rejected, elapsed, Reason: reason);
    }

    /// <summary>
    /// The doctor handshake goes out <see cref="NetDeliveryMethod.Unreliable"/> (resent until answered) rather than
    /// ReliableOrdered like the game: Lidgren 0.29 (as vendored) NREs on its network thread when an ack for a stored
    /// reliable message is processed after the channel was reset by a disconnect/shutdown, which kills the process.
    /// With no reliable messages of ours in flight that path cannot be hit. The payload bytes are identical and the
    /// server does not check the delivery method.
    /// </summary>
    private static void SendDoctorHandshake(NetClient client)
    {
        var bytes = VmpHandshakeEncoder.EncodeRequest(new HandshakeRequest(
            VmpProtocol.DoctorPlayerName,
            Guid.NewGuid().ToString(),
            VmpProtocol.ProtocolForkId,
            VmpProtocol.DoctorClientBuild)
        { SentTime = DateTime.UtcNow.Ticks });
        var msg = client.CreateMessage(bytes.Length);
        msg.Write(bytes);
        client.SendMessage(msg, NetDeliveryMethod.Unreliable);
        client.FlushSendQueue();
    }
}
