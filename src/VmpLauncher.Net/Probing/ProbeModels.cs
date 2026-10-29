namespace VmpLauncher.Net.Probing;

/// <summary>How far a probe goes. Only <see cref="Reachability"/> is safe for background status polling.</summary>
public enum ProbeLevel
{
    /// <summary>L1: connect with a deliberately wrong password; an "Invalid password" deny proves VMP is answering.</summary>
    Reachability = 1,

    /// <summary>L2: connect with the saved password, then disconnect before any handshake (Doctor only).</summary>
    Password = 2,

    /// <summary>L3: saved password + a handshake with an impossible client build so the server reveals its build
    /// and rejects us before authentication (Doctor only).</summary>
    Build = 3,
}

public enum ProbeOutcome
{
    /// <summary>L1: the VMP server answered (denied our dummy password).</summary>
    Online,

    /// <summary>L1: the server accepted a random password, so it has no password set. We disconnected at once.</summary>
    OnlineNoPassword,

    /// <summary>No answer from the host in time (server/tunnel offline, wrong port, firewall).</summary>
    Unreachable,

    /// <summary>The host name could not be resolved.</summary>
    AddressNotFound,

    /// <summary>L2: the saved password was accepted.</summary>
    PasswordOk,

    /// <summary>L2/L3: the server answered "Invalid password".</summary>
    WrongPassword,

    /// <summary>L3: the server reported its exact build in the rejection (see <see cref="ProbeResult.ServerBuild"/>).</summary>
    BuildReported,

    /// <summary>The server answered but refused for another reason (see <see cref="ProbeResult.Reason"/>).</summary>
    Rejected,

    /// <summary>Local failure (socket, bad input) or a connected server that never answered the handshake.</summary>
    Error,

    /// <summary>
    /// The VMP server is up but refused with "Server full" (all player slots taken). Lidgren sends this before it
    /// looks at the password, so the password and build are unknown. Appended last so existing values keep their numbers.
    /// </summary>
    ServerFull,
}

/// <summary>Probe parameters. <see cref="Password"/> is never printed by <see cref="ToString"/>.</summary>
public sealed record ProbeRequest(string Host, int Port, ProbeLevel Level = ProbeLevel.Reachability)
{
    /// <summary>Saved server password, required for L2/L3 and ignored for L1.</summary>
    public string? Password { get; init; }

    /// <summary>Overall budget including DNS.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public float ConnectionTimeoutSeconds { get; init; } = 8f;
    public int MaximumHandshakeAttempts { get; init; } = 4;
    public float ResendHandshakeIntervalSeconds { get; init; } = 1.5f;

    public override string ToString() => $"{Host}:{Port} L{(int)Level}{(Password is null ? "" : " (password set)")}";
}

/// <param name="Outcome">What happened.</param>
/// <param name="RoundTrip">Time from the first connect packet to the server's first answer (includes resends).</param>
/// <param name="ServerBuild">Exact server build (L3 only).</param>
/// <param name="Reason">Raw disconnect/deny reason from the server or Lidgren.</param>
/// <param name="Error">Local error text.</param>
public sealed record ProbeResult(ProbeOutcome Outcome, TimeSpan? RoundTrip = null, string? ServerBuild = null, string? Reason = null, string? Error = null)
{
    /// <summary>Address actually contacted (after DNS), for diagnostics.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>True when a VMP server answered at all.</summary>
    public bool ServerAnswered => Outcome is ProbeOutcome.Online or ProbeOutcome.OnlineNoPassword or ProbeOutcome.PasswordOk
        or ProbeOutcome.WrongPassword or ProbeOutcome.BuildReported or ProbeOutcome.Rejected or ProbeOutcome.ServerFull;
}

public interface IServerProbe
{
    Task<ProbeResult> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken = default);
}
