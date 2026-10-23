using System.Text.RegularExpressions;

namespace VmpLauncher.Net.Doctor;

/// <summary>Scrubs secrets and personal paths from text that may be shared (Doctor report, logs).</summary>
public static partial class ReportRedactor
{
    public const string Redacted = "[redacted]";
    private const string ProfileToken = "%USERPROFILE%";

    public static string Redact(string? text, IEnumerable<string?>? secrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }
        foreach (var secret in (secrets ?? []).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s!.Length))
        {
            text = text.Replace(secret!, Redacted, StringComparison.Ordinal);
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && profile.Length > 3)
        {
            foreach (var form in ProfileForms(profile))
            {
                text = text.Replace(form, ProfileToken, StringComparison.OrdinalIgnoreCase);
            }
        }
        // Any other C:\Users\<name> (other drives/accounts, forward slashes, escaped JSON backslashes).
        return UserDirRegex().Replace(text, ProfileToken);
    }

    /// <summary>The profile path as it appears in plain text, with forward slashes, and JSON/C#-escaped (doubled backslashes); longest first.</summary>
    private static IEnumerable<string> ProfileForms(string profile)
    {
        var trimmed = profile.TrimEnd('\\', '/');
        var forms = new[]
        {
            trimmed.Replace("\\", "\\\\", StringComparison.Ordinal),
            trimmed,
            trimmed.Replace('\\', '/'),
        };
        return forms.Where(f => f.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // C:\Users\<name> where <name> may contain spaces ("John Smith"): it runs lazily up to the next path separator, quote
    // or other character that cannot be part of a folder name, or to the end of the line. Trailing spaces stay outside.
    [GeneratedRegex(@"[A-Za-z]:(?:\\\\|\\|/)Users(?:\\\\|\\|/)[^\\/\r\n""'<>|:*?]{1,255}?(?=\s*(?:[\\/""'<>|:*?]|\r|\n|\z))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserDirRegex();
}
