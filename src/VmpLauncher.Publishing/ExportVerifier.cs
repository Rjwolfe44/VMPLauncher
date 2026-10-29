using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Security;

namespace VmpLauncher.Publishing;

/// <summary>Validates an export (or staged) version folder: lock/inventory consistency, signature, asset hashes and sizes, secret patterns.</summary>
public static class ExportVerifier
{
    public sealed record Result(PackLock Lock, TrustLevel Trust, string? KeyId, IReadOnlyList<string> Problems)
    {
        public bool Ok => Problems.Count == 0;
    }

    public static Result Verify(string versionDir, string? extraKeysFile = null, IEnumerable<string>? assetSearchDirs = null)
    {
        var lockPath = Directory.EnumerateFiles(versionDir, "*.vmpack.lock.json").FirstOrDefault()
            ?? throw new FileNotFoundException("No *.vmpack.lock.json in " + versionDir);
        var problems = new List<string>();
        var (lockDoc, _, _) = ProfileOperations.LoadAndValidate(new ProfileOperations.LockSource(lockPath, Path.Combine(versionDir, "inventory.json.gz")));
        var verifier = SignatureVerifier.Default(extraKeysFile);
        var trust = verifier.VerifyFile(lockPath, out var keyId, out var reason);
        if (trust != TrustLevel.VerifiedPublisher)
        {
            problems.Add($"lock signature: {SignatureVerifier.DescribeTrust(trust)}{(reason is null ? "" : " - " + reason)}");
        }
        var searchDirs = new List<string> { versionDir };
        searchDirs.AddRange(assetSearchDirs ?? Array.Empty<string>());
        foreach (var m in lockDoc.Ckan.Modules.Where(m => m.Origin == ModuleOrigin.Custom))
        {
            var file = searchDirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, m.Artifact!.FileName ?? "*", SearchOption.AllDirectories)).FirstOrDefault();
            if (file is null)
            {
                problems.Add($"{m.Identifier}: asset {m.Artifact!.FileName} missing");
                continue;
            }
            if (!Sha256Util.HexEquals(Sha256Util.HashFile(file), m.Artifact!.Sha256))
            {
                problems.Add($"{m.Identifier}: asset hash mismatch");
            }
            if (new FileInfo(file).Length >= 2L * 1024 * 1024 * 1024)
            {
                problems.Add($"{m.Identifier}: asset is 2 GiB or larger; GitHub Releases rejects it");
            }
        }
        var policy = Path.Combine(versionDir, "KSPModControl.xml");
        if (lockDoc.Multiplayer.Policy?.Sha256AtLockTime is { } policySha && File.Exists(policy) && !Sha256Util.HexEquals(Sha256Util.HashFile(policy), policySha))
        {
            problems.Add("KSPModControl.xml does not match the hash recorded in the lock");
        }
        var findings = new SecretScanner().ScanDirectory(versionDir);
        problems.AddRange(findings.Select(f => $"secret pattern: {Path.GetRelativePath(versionDir, f.File)}{(f.Entry is null ? "" : "!" + f.Entry)}: {f.What}"));
        return new Result(lockDoc, trust, keyId, problems);
    }
}
