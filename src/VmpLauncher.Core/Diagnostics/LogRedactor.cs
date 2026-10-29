using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace VmpLauncher.Core.Diagnostics;

/// <summary>
/// Scrubs one line (or a block) of log text before it goes into a bundle for the host. The Windows user name becomes
/// <c>&lt;user&gt;</c> in every <c>C:\Users\name</c> path (backslashes, forward slashes and JSON-escaped double
/// backslashes, any case, any drive), and also wherever the current user's own name stands as a whole word; secrets
/// (the server password, private-repo tokens) become <c>[redacted]</c>. Nothing else is touched, so a name that is only
/// part of another word (<c>Bobsled</c> for the user <c>bob</c>) survives.
/// </summary>
/// <remarks>Instances are cheap and not thread safe: make one per bundle. <see cref="Replacements"/> counts what was changed.</remarks>
public sealed partial class LogRedactor
{
    public const string UserPlaceholder = "<user>";
    public const string SecretPlaceholder = "[redacted]";

    // Loose whole-word replacement of the user's own name only runs for names this long (a 1-2 letter name would mangle
    // ordinary words); paths are always redacted whatever the name.
    private const int MinLooseNameLength = 3;
    private const int MinSecretLength = 4;

    private static readonly SearchValues<string> TokenMarkers =
        SearchValues.Create(["VMPINVITE", "github_pat_", "ghp_", "gho_", "ghu_", "ghs_", "ghr_"], StringComparison.Ordinal);

    private readonly string[] _userNames;
    private readonly string[] _secrets;

    public LogRedactor(IEnumerable<string?>? userNames = null, IEnumerable<string?>? secrets = null)
    {
        _userNames = (userNames ?? [])
            .Select(n => n?.Trim())
            .Where(n => !string.IsNullOrEmpty(n) && n.Length >= MinLooseNameLength && n.IndexOfAny(['\\', '/', '<', '>']) < 0)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToArray();
        _secrets = (secrets ?? [])
            .Where(s => !string.IsNullOrEmpty(s) && s.Length >= MinSecretLength)
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    /// <summary>How many user names (path or whole word) and secrets this instance has replaced so far.</summary>
    public long UserNameReplacements { get; private set; }

    public long SecretReplacements { get; private set; }

    public long Replacements => UserNameReplacements + SecretReplacements;

    /// <summary>The names of the Windows account running this process: the user name and the profile folder's name (they can differ).</summary>
    public static IReadOnlyList<string> CurrentUserNames()
    {
        var names = new List<string>();
        try
        {
            names.Add(Environment.UserName);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                names.Add(Path.GetFileName(profile.TrimEnd('\\', '/')));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // No user name to hide: the C:\Users\<name> rule still applies.
        }
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The pure function: <paramref name="text"/> with user-profile names (and the optional extra names and secrets) replaced.</summary>
    public static string Redact(string? text, IEnumerable<string?>? userNames = null, IEnumerable<string?>? secrets = null)
        => new LogRedactor(userNames, secrets).Redact(text);

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }
        foreach (var secret in _secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
            {
                text = ReplaceAll(text, secret, SecretPlaceholder, wholeWord: false, out var n, StringComparison.Ordinal);
                SecretReplacements += n;
            }
        }
        if (text.AsSpan().ContainsAny(TokenMarkers))
        {
            // Shapes of the things the launcher itself holds (invite codes, GitHub tokens), in case one was ever logged.
            text = TokenShapeRegex().Replace(text, _ =>
            {
                SecretReplacements++;
                return SecretPlaceholder;
            });
        }
        foreach (var name in _userNames)
        {
            // Names can contain spaces ("John Smith"), so these run before the path rule below, which stops at a space.
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                text = ReplaceAll(text, name, UserPlaceholder, wholeWord: true, out var n, StringComparison.OrdinalIgnoreCase);
                UserNameReplacements += n;
            }
        }
        if (text.Contains("Users", StringComparison.OrdinalIgnoreCase))
        {
            text = UserDirRegex().Replace(text, m =>
            {
                UserNameReplacements++;
                return $"{m.Groups["head"].Value}{UserPlaceholder}";
            });
        }
        return text;
    }

    private static string ReplaceAll(string text, string find, string replacement, bool wholeWord, out int count, StringComparison comparison)
    {
        count = 0;
        var idx = text.IndexOf(find, comparison);
        if (idx < 0)
        {
            return text;
        }
        StringBuilder? sb = null;
        var last = 0;
        while (idx >= 0)
        {
            var end = idx + find.Length;
            var accept = !wholeWord || (IsBoundary(text, idx - 1) && IsBoundary(text, end) && !IsAlreadyRedacted(text, idx, end));
            if (accept)
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, last, idx - last).Append(replacement);
                last = end;
                count++;
            }
            idx = end >= text.Length ? -1 : text.IndexOf(find, end, comparison);
        }
        if (sb is null)
        {
            return text;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    private static bool IsBoundary(string text, int index) => index < 0 || index >= text.Length || !IsWordChar(text[index]);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The name sits inside an earlier <c>&lt;user&gt;</c> (a user called "user"): leave it, so redaction is idempotent.</summary>
    private static bool IsAlreadyRedacted(string text, int start, int end)
        => start > 0 && text[start - 1] == '<' && end < text.Length && text[end] == '>';

    // <drive>:\Users\<name>, also with forward slashes and JSON/C#-escaped double backslashes. The name runs to the next
    // separator, quote, bracket or space; a name that contains spaces is covered by the whole-word rule for the current user.
    [GeneratedRegex(@"(?<head>[A-Za-z]:(?:\\\\|\\|/)+Users(?:\\\\|\\|/)+)(?<name>[^\\/\r\n""'<>|:*?\s,;()\[\]{}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserDirRegex();

    [GeneratedRegex(@"VMPINVITE\d*-[A-Za-z0-9_\-+/=.]{8,}|github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenShapeRegex();
}
