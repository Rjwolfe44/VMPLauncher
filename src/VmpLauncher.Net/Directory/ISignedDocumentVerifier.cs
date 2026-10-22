namespace VmpLauncher.Net.Directory;

/// <summary>
/// Verifies a detached signature over the exact document bytes. The production implementation (trusted pack keys)
/// lives in Core and is injected; Net only depends on this contract.
/// </summary>
public interface ISignedDocumentVerifier
{
    /// <param name="document">Exact bytes as downloaded (no re-serialization).</param>
    /// <param name="signatureJson">Contents of the detached <c>.sig</c> file.</param>
    /// <param name="keyId">Id of the trusted key that produced the signature, when valid.</param>
    /// <returns>True only when the signature is valid and made by a trusted key.</returns>
    bool Verify(ReadOnlySpan<byte> document, string signatureJson, out string? keyId);
}
