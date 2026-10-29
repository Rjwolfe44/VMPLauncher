using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Private;

/// <summary>
/// What a host hands a friend in one paste: read access to the private mods repository and, optionally, the server
/// password. It is a bearer secret (anyone holding it can download the private mods), so it travels only in a private
/// message and is stored on the friend's PC with DPAPI. It is not encrypted: there is nothing to encrypt it against.
/// </summary>
public sealed record Invite
{
    public int V { get; init; } = 1;
    /// <summary>Who issued it (display only).</summary>
    public string? From { get; init; }
    public string? PackId { get; init; }
    /// <summary>owner/name of the private mods repository.</summary>
    public string? Repo { get; init; }
    /// <summary>Fine-grained GitHub token with read-only Contents access to <see cref="Repo"/> only.</summary>
    public string? Token { get; init; }
    public string? ServerPassword { get; init; }
}

public static partial class InviteCode
{
    public const string Prefix = "VMPINVITE1-";

    [GeneratedRegex(@"VMPINVITE1-([A-Za-z0-9_\-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9\-]{0,38})/[A-Za-z0-9._\-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoPattern();

    public static bool IsValidRepo(string? repo) => repo is not null && RepoPattern().IsMatch(repo) && !repo.Contains("..", StringComparison.Ordinal);

    public static string Encode(Invite invite)
    {
        if (invite.Repo is not null && !IsValidRepo(invite.Repo))
        {
            throw new ArgumentException($"'{invite.Repo}' is not an owner/name repository.", nameof(invite));
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(invite, LauncherJson.Compact);
        return Prefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>True when <paramref name="text"/> contains something that looks like an invite code.</summary>
    public static bool LooksLikeInvite(string? text) => text is not null && Pattern().IsMatch(text);

    /// <summary>
    /// Finds and decodes an invite inside pasted text (a whole chat message is fine). Throws a user-facing error for
    /// anything malformed or truncated.
    /// </summary>
    public static Invite Decode(string text)
    {
        var m = Pattern().Match(text ?? string.Empty);
        if (!m.Success)
        {
            throw new LauncherException(ErrorCode.InvalidPack, "That is not an invite code. It starts with VMPINVITE1-.", RecoveryHint.AskHost);
        }
        Invite? invite;
        try
        {
            var b64 = m.Groups[1].Value.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
            invite = JsonSerializer.Deserialize<Invite>(Convert.FromBase64String(b64), LauncherJson.Pretty);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            throw new LauncherException(ErrorCode.InvalidPack, "The invite code is damaged or cut off. Copy the whole code again.", RecoveryHint.AskHost, ex);
        }
        if (invite is null || invite.V != 1)
        {
            throw new LauncherException(ErrorCode.InvalidPack, "This invite code is from a newer launcher. Update the launcher first.", RecoveryHint.AskHost);
        }
        if (invite.Repo is not null && !IsValidRepo(invite.Repo))
        {
            throw new LauncherException(ErrorCode.InvalidPack, "The invite code names an invalid repository.", RecoveryHint.AskHost);
        }
        if ((invite.Repo is null) != (invite.Token is null))
        {
            throw new LauncherException(ErrorCode.InvalidPack, "The invite code is incomplete (repository without token or the reverse).", RecoveryHint.AskHost);
        }
        return invite;
    }
}
