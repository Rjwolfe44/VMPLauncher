using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace VmpLauncher.Core.Security;

/// <summary>
/// Keeps the publisher key's passphrase encrypted with DPAPI (current Windows user) next to the key file, so the
/// host does not have to remember or re-type it. Anyone who can read it already has the user's Windows session.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PassphraseStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VmpLauncher.publisher-passphrase.v1");

    public static string PathFor(string keyDir, string keyId) => Path.Combine(keyDir, keyId + ".passphrase.dpapi");

    public static string GenerateRandom() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');

    public static void Save(string keyDir, string keyId, string passphrase)
    {
        Directory.CreateDirectory(keyDir);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(passphrase), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(keyDir, keyId), cipher);
    }

    public static string? TryLoad(string keyDir, string keyId)
    {
        var path = PathFor(keyDir, keyId);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
