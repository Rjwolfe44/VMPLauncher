using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VmpLauncher.Core.Private;

/// <summary>
/// The host's copy of the read-only access token that invite codes carry, one per private repository, encrypted with
/// DPAPI for the current Windows user next to the publisher key (<c>%APPDATA%\VMPLauncher\publisher</c>).
/// This is never the host's own GitHub login: it must be a fine-grained token limited to Contents: read on the
/// private mods repository, because every friend gets a copy.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HostAccessStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VmpLauncher.private-access.v1");

    public static string PathFor(string keyDir) => Path.Combine(keyDir, "private-access.dpapi");

    public static IReadOnlyDictionary<string, string> LoadAll(string keyDir)
    {
        var path = PathFor(keyDir);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new Dictionary<string, string>();
            return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string? TryGet(string keyDir, string repo) => LoadAll(keyDir).TryGetValue(repo, out var t) ? t : null;

    public static void Set(string keyDir, string repo, string? token)
    {
        if (!InviteCode.IsValidRepo(repo))
        {
            throw new ArgumentException($"'{repo}' is not an owner/name repository.", nameof(repo));
        }
        var map = new Dictionary<string, string>(LoadAll(keyDir), StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(token))
        {
            map.Remove(repo);
        }
        else
        {
            map[repo] = token.Trim();
        }
        Directory.CreateDirectory(keyDir);
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(map), Entropy, DataProtectionScope.CurrentUser);
        var path = PathFor(keyDir);
        File.WriteAllBytes(path + ".tmp", cipher);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// Rejects tokens that are obviously the host's own login rather than a scoped read-only token:
    /// classic PATs (ghp_) and OAuth tokens (gho_, e.g. from <c>gh auth token</c>) can see every repository.
    /// </summary>
    public static string? WhyUnsafe(string token)
    {
        var t = token.Trim();
        if (t.StartsWith("gho_", StringComparison.Ordinal) || t.StartsWith("ghu_", StringComparison.Ordinal))
        {
            return "That is a GitHub login token (like the one `gh auth token` prints). It can see all your repositories. Create a fine-grained token limited to the private mods repository instead.";
        }
        if (t.StartsWith("ghp_", StringComparison.Ordinal))
        {
            return "That is a classic personal access token, which cannot be limited to one repository. Create a fine-grained token (it starts with github_pat_) instead.";
        }
        if (!t.StartsWith("github_pat_", StringComparison.Ordinal))
        {
            return "That does not look like a fine-grained GitHub token (they start with github_pat_).";
        }
        return null;
    }
}
