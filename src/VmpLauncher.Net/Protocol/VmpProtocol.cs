using System.Text.RegularExpressions;

namespace VmpLauncher.Net.Protocol;

/// <summary>Wire constants shared with the VMP client/server (LunaMultiplayer fork, see VMPMod\LmpCommon).</summary>
public static partial class VmpProtocol
{
    /// <summary>Lidgren application identifier; peers with a different identifier are silently ignored.</summary>
    public const string AppIdentifier = "VMP";

    /// <summary>Fixed MTU used by VMP (AutoExpandMTU is off on both ends).</summary>
    public const int Mtu = 1200;

    public const int DefaultPort = 8800;

    /// <summary><c>SessionAdmission.LocalProtocolForkId</c>.</summary>
    public const string ProtocolForkId = "VMP.VladMultiplayer";

    /// <summary>Deny reason sent by <c>LidgrenServer</c> when the hail (password) does not match.</summary>
    public const string InvalidPasswordReason = "Invalid password";

    /// <summary>Build string the Doctor sends so the server always rejects it before authentication.</summary>
    public const string DoctorClientBuild = "vmp-launcher-doctor";

    public const string DoctorPlayerName = "launcher";

    /// <summary>Hail prefix for reachability probes. Deliberately never a valid password.</summary>
    public const string StatusProbeHailPrefix = "vmp-launcher-status-probe/";

    /// <summary>Disconnect reason Lidgren itself sends when every connection slot is taken (before the password check).</summary>
    public const string ServerFullReason = "Server full";

    /// <summary>
    /// <c>SessionAdmission</c>: "Exact build mismatch: server requires 'X', client sent 'Y'." Anchored on the whole
    /// exact-build message form: the fork rejection ("Protocol/fork mismatch: server requires 'VMP.VladMultiplayer', ...")
    /// also says "server requires" but carries the fork id, not a build.
    /// </summary>
    [GeneratedRegex(@"Exact build mismatch: server requires '(?<b>[^']+)'", RegexOptions.CultureInvariant)]
    public static partial Regex ServerRequiresRegex();

    public static string? TryParseServerBuild(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return null;
        }
        var m = ServerRequiresRegex().Match(reason);
        return m.Success ? m.Groups["b"].Value : null;
    }

    public static bool IsInvalidPassword(string? reason)
        => reason is not null && reason.Contains(InvalidPasswordReason, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the "Server full" disconnect reason (Lidgren's slot check, and VMP's own handshake check).</summary>
    public static bool IsServerFull(string? reason)
        => reason is not null && reason.Trim().Equals(ServerFullReason, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mirrors <c>SessionAdmission.AreExactBuildsCompatible</c>: trimmed ordinal comparison where a trailing
    /// "-compiled" stamp (dedicated server builds) is ignored.
    /// </summary>
    public static bool AreBuildsCompatible(string? a, string? b)
    {
        static string Norm(string? s)
        {
            s = s?.Trim() ?? string.Empty;
            return s.EndsWith("-compiled", StringComparison.OrdinalIgnoreCase) ? s[..^"-compiled".Length] : s;
        }
        return Norm(a).Length > 0 && string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);
    }
}
