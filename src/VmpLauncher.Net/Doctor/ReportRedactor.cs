using System.Text.RegularExpressions;

namespace VmpLauncher.Net.Doctor;

/// <summary>Scrubs secrets and personal paths from text that may be shared (Doctor report, logs).</summary>
public static partial class ReportRedactor
{
    public const string Redacted = "[redacted]";

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
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        // Any other C:\Users\<name> (other drives/accounts, forward slashes, escaped JSON backslashes).
        return UserDirRegex().Replace(text, "%USERPROFILE%");
    }

    [GeneratedRegex(@"[A-Za-z]:(\\\\|\\|/)Users(\\\\|\\|/)[^\\/\s""'<>|:*?]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserDirRegex();
}
