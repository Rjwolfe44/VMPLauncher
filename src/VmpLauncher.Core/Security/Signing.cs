using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Security;

/// <summary>Detached signature file (<c>*.sig</c>) over the exact bytes of a document.</summary>
public sealed record SignatureEnvelope
{
    public string Schema { get; init; } = "vmpack-sig/1";
    public string Alg { get; init; } = "ecdsa-p256-sha256";
    public required string KeyId { get; init; }
    /// <summary>SHA-256 of the signed bytes, for quick mismatch diagnostics (the signature itself is over the bytes).</summary>
    public required string SignedSha256 { get; init; }
    /// <summary>Base64 IEEE P1363 (r||s) signature.</summary>
    public required string Sig { get; init; }
    public DateTimeOffset Utc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TrustedKey
{
    public required string KeyId { get; init; }
    public string Alg { get; init; } = "ecdsa-p256-sha256";
    /// <summary>Base64 SubjectPublicKeyInfo.</summary>
    public required string PublicKeySpki { get; init; }
    public string? Owner { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public bool Revoked { get; init; }
}

public enum TrustLevel
{
    /// <summary>Signed by a key the launcher ships with (or one introduced by a signed rotation).</summary>
    VerifiedPublisher,
    /// <summary>No signature; content hashes still protect integrity but not origin.</summary>
    Unverified,
    /// <summary>A signature is present but invalid or from an unknown/revoked key.</summary>
    Tampered,
}

/// <summary>
/// ECDSA P-256 / SHA-256 from the BCL: no native dependency, Velopack-friendly. The private key is stored as
/// encrypted PKCS#8 (passphrase) and never leaves the host machine.
/// </summary>
public sealed class PublisherKey : IDisposable
{
    private readonly ECDsa _key;

    private PublisherKey(ECDsa key, string keyId)
    {
        _key = key;
        KeyId = keyId;
    }

    public string KeyId { get; }

    public static PublisherKey Generate(string keyId) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), keyId);

    public TrustedKey PublicInfo(string? owner = null) => new()
    {
        KeyId = KeyId,
        PublicKeySpki = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        Owner = owner,
        NotBefore = DateTimeOffset.UtcNow,
    };

    public void SaveEncrypted(string path, string passphrase)
    {
        var pkcs8 = _key.ExportEncryptedPkcs8PrivateKey(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        var doc = new { keyId = KeyId, alg = "ecdsa-p256-sha256", encryptedPkcs8 = Convert.ToBase64String(pkcs8) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        LauncherJson.WriteFile(path, doc);
    }

    public static PublisherKey LoadEncrypted(string path, string passphrase)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var keyId = doc.RootElement.GetProperty("keyId").GetString()!;
        var blob = Convert.FromBase64String(doc.RootElement.GetProperty("encryptedPkcs8").GetString()!);
        var key = ECDsa.Create();
        try
        {
            key.ImportEncryptedPkcs8PrivateKey(passphrase, blob, out _);
        }
        catch (CryptographicException ex)
        {
            key.Dispose();
            throw new LauncherException(ErrorCode.Unknown, "Wrong passphrase for the publisher key.", RecoveryHint.Retry, ex);
        }
        return new PublisherKey(key, keyId);
    }

    public SignatureEnvelope Sign(ReadOnlySpan<byte> document)
    {
        var sig = _key.SignData(document, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignatureEnvelope { KeyId = KeyId, SignedSha256 = Sha256Util.HashBytes(document), Sig = Convert.ToBase64String(sig) };
    }

    public void SignFile(string documentPath)
    {
        var env = Sign(File.ReadAllBytes(documentPath));
        LauncherJson.WriteFile(documentPath + ".sig", env);
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Verifies signatures against a pinned set of publisher keys.</summary>
public sealed class SignatureVerifier
{
    private readonly Dictionary<string, TrustedKey> _keys;

    public SignatureVerifier(IEnumerable<TrustedKey> trustedKeys)
    {
        _keys = trustedKeys.ToDictionary(k => k.KeyId, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<TrustedKey> Keys => _keys.Values;

    /// <summary>Keys compiled into the launcher (<c>trusted-keys.json</c> embedded resource) plus any extra file.</summary>
    public static SignatureVerifier Default(string? extraKeysFile = null)
    {
        var keys = new List<TrustedKey>();
        using (var s = typeof(SignatureVerifier).Assembly.GetManifestResourceStream("VmpLauncher.Core.Security.trusted-keys.json"))
        {
            if (s is not null)
            {
                keys.AddRange(JsonSerializer.Deserialize<List<TrustedKey>>(s, LauncherJson.Pretty) ?? new List<TrustedKey>());
            }
        }
        if (extraKeysFile is not null && File.Exists(extraKeysFile))
        {
            keys.AddRange(LauncherJson.ReadFile<List<TrustedKey>>(extraKeysFile));
        }
        return new SignatureVerifier(keys);
    }

    public TrustLevel Verify(ReadOnlySpan<byte> document, SignatureEnvelope? envelope, out string? keyId, out string? reason)
    {
        keyId = envelope?.KeyId;
        reason = null;
        if (envelope is null)
        {
            reason = "no signature";
            return TrustLevel.Unverified;
        }
        if (envelope.Alg != "ecdsa-p256-sha256")
        {
            reason = $"unsupported algorithm {envelope.Alg}";
            return TrustLevel.Tampered;
        }
        if (!_keys.TryGetValue(envelope.KeyId, out var key))
        {
            reason = $"unknown publisher key {envelope.KeyId}";
            return TrustLevel.Tampered;
        }
        if (key.Revoked)
        {
            reason = $"publisher key {envelope.KeyId} is revoked";
            return TrustLevel.Tampered;
        }
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKeySpki), out _);
            var ok = ecdsa.VerifyData(document, Convert.FromBase64String(envelope.Sig), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!ok)
            {
                reason = "signature does not match the document";
                return TrustLevel.Tampered;
            }
            return TrustLevel.VerifiedPublisher;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            reason = $"malformed signature: {ex.Message}";
            return TrustLevel.Tampered;
        }
    }

    public TrustLevel VerifyFile(string documentPath, out string? keyId, out string? reason)
    {
        var sigPath = documentPath + ".sig";
        SignatureEnvelope? env = null;
        if (File.Exists(sigPath))
        {
            try
            {
                env = LauncherJson.ReadFile<SignatureEnvelope>(sigPath, 64 * 1024);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                keyId = null;
                reason = "unreadable signature file";
                return TrustLevel.Tampered;
            }
        }
        return Verify(File.ReadAllBytes(documentPath), env, out keyId, out reason);
    }

    public static string DescribeTrust(TrustLevel level) => level switch
    {
        TrustLevel.VerifiedPublisher => "Verified publisher",
        TrustLevel.Unverified => "Unverified pack",
        _ => "Tampered or untrusted",
    };
}
