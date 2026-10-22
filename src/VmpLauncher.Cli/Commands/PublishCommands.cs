using System.CommandLine;
using VmpLauncher.Ckan;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Publishing;

namespace VmpLauncher.Cli.Commands;

internal static class PublishCommands
{
    public static IEnumerable<Command> Build(Option<string?> dataDirOption)
    {
        yield return KeyCommand();
        yield return CaptureCommand(dataDirOption);
        yield return VerifyExportCommand();
    }

    private static string DefaultKeyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMPLauncher", "publisher");

    /// <summary>Env var first (CI / explicit), then the DPAPI-protected passphrase stored next to the key.</summary>
    private static string? ResolvePassphrase(string envName, string keyDir, string keyId)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }
        return OperatingSystem.IsWindows() ? PassphraseStore.TryLoad(keyDir, keyId) : null;
    }

    private static Command KeyCommand()
    {
        var key = new Command("key", "Publisher signing key (ECDSA P-256)");
        var keyId = new Option<string>("--key-id") { Description = "Key id, e.g. vmp-host-2026a.", Required = true };
        var owner = new Option<string?>("--owner") { Description = "Owner label stored with the public key." };
        var passEnv = new Option<string>("--passphrase-env") { Description = "Environment variable holding the passphrase.", DefaultValueFactory = _ => "VMPL_KEY_PASSPHRASE" };
        var dir = new Option<string?>("--dir") { Description = "Key directory (default %APPDATA%\\VMPLauncher\\publisher)." };

        var noStore = new Option<bool>("--no-store-passphrase") { Description = "Do not keep a DPAPI-protected copy of the passphrase next to the key (you must then set the env var every time)." };
        var gen = new Command("generate", "Generate a new publisher key and print the trusted-keys.json entry") { keyId, owner, passEnv, dir, noStore };
        gen.SetAction(parse =>
        {
            var keyDir = parse.GetValue(dir) ?? DefaultKeyDir;
            var pass = Environment.GetEnvironmentVariable(parse.GetValue(passEnv)!);
            var generated = false;
            if (string.IsNullOrEmpty(pass))
            {
                if (!OperatingSystem.IsWindows() || parse.GetValue(noStore))
                {
                    Console.Error.WriteLine($"Set {parse.GetValue(passEnv)} to the passphrase first (never pass it on the command line).");
                    return 2;
                }
                pass = PassphraseStore.GenerateRandom();
                generated = true;
            }
            var path = Path.Combine(keyDir, parse.GetValue(keyId)! + ".key.json");
            if (File.Exists(path))
            {
                Console.Error.WriteLine($"{path} already exists; refusing to overwrite a signing key.");
                return 2;
            }
            using var k = PublisherKey.Generate(parse.GetValue(keyId)!);
            k.SaveEncrypted(path, pass);
            var pub = k.PublicInfo(parse.GetValue(owner));
            var pubPath = Path.Combine(keyDir, parse.GetValue(keyId)! + ".public.json");
            LauncherJson.WriteFile(pubPath, pub);
            if (OperatingSystem.IsWindows() && !parse.GetValue(noStore))
            {
                PassphraseStore.Save(keyDir, parse.GetValue(keyId)!, pass);
                Console.WriteLine($"Passphrase stored (DPAPI, this Windows user only): {PassphraseStore.PathFor(keyDir, parse.GetValue(keyId)!)}{(generated ? "  [randomly generated]" : "")}");
                Console.WriteLine("Back up the key folder; without the .key.json AND this Windows account (or the passphrase) nobody can sign packs with this key.");
            }
            Console.WriteLine($"Private key (encrypted): {path}");
            Console.WriteLine($"Public key              : {pubPath}");
            Console.WriteLine("Add this entry to src\\VmpLauncher.Core\\Security\\trusted-keys.json and rebuild the launcher:");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(pub, LauncherJson.Pretty));
            return 0;
        });

        var file = new Argument<string>("file") { Description = "Document to sign (a .sig file is written next to it)." };
        var sign = new Command("sign", "Sign a file with the publisher key") { file, keyId, passEnv, dir };
        sign.SetAction(parse =>
        {
            var keyDir = parse.GetValue(dir) ?? DefaultKeyDir;
            var pass = ResolvePassphrase(parse.GetValue(passEnv)!, keyDir, parse.GetValue(keyId)!);
            if (string.IsNullOrEmpty(pass))
            {
                Console.Error.WriteLine($"No passphrase: set {parse.GetValue(passEnv)} or generate the key with a stored passphrase.");
                return 2;
            }
            using var k = PublisherKey.LoadEncrypted(Path.Combine(keyDir, parse.GetValue(keyId)! + ".key.json"), pass);
            k.SignFile(parse.GetValue(file)!);
            Console.WriteLine($"signed {parse.GetValue(file)} -> .sig");
            return 0;
        });

        var vfile = new Argument<string>("file") { Description = "Signed document (expects file.sig)." };
        var keysFile = new Option<string?>("--keys") { Description = "Extra trusted-keys JSON file (defaults to keys built into the launcher)." };
        var verify = new Command("verify", "Verify a signature against the trusted publisher keys") { vfile, keysFile };
        verify.SetAction(parse =>
        {
            var verifier = SignatureVerifier.Default(parse.GetValue(keysFile));
            var level = verifier.VerifyFile(parse.GetValue(vfile)!, out var kid, out var reason);
            Console.WriteLine($"{SignatureVerifier.DescribeTrust(level)}{(kid is null ? "" : $" (key {kid})")}{(reason is null ? "" : $": {reason}")}");
            return level == TrustLevel.VerifiedPublisher ? 0 : 3;
        });

        key.Subcommands.Add(gen);
        key.Subcommands.Add(sign);
        key.Subcommands.Add(verify);
        return key;
    }

    private static Command CaptureCommand(Option<string?> dataDirOption)
    {
        var packFile = new Option<string>("--pack") { Description = "Pack definition (*.vmpack.json).", Required = true };
        var ksp = new Option<string>("--ksp") { Description = "Host KSP install (read-only).", Required = true };
        var cache = new Option<string>("--cache") { Description = "Host CKAN download cache.", Required = true };
        var output = new Option<string>("--out") { Description = "Output root (a <packId>/<version> folder is created).", Required = true };
        var accept = new Option<bool>("--accept-overrides") { Description = "Ship modified managed files as deliberate replace overrides." };
        var allowDirty = new Option<bool>("--allow-dirty") { Description = "Allow custom packages built from a dirty git tree." };
        var policy = new Option<string?>("--policy") { Description = "KSPModControl.xml to ship with the pack." };
        var privateJson = new Option<string?>("--private-connection") { Description = "HOSTING\\connection.private.json (read only to learn secrets for the scan; never copied)." };
        var keyId = new Option<string?>("--key-id") { Description = "Sign with this publisher key (needs VMPL_KEY_PASSPHRASE)." };
        var keyDir = new Option<string?>("--key-dir") { Description = "Key directory." };
        var version = new Option<string?>("--version") { Description = "Override the pack version." };

        var capture = new Command("capture", "Capture the host install into a signed pack export (local only; nothing is published)") { packFile, ksp, cache, output, accept, allowDirty, policy, privateJson, keyId, keyDir, version };
        capture.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            CkanBootstrap.Initialize(paths);
            PublisherKey? key = null;
            if (parse.GetValue(keyId) is { } kid)
            {
                var kdir = parse.GetValue(keyDir) ?? DefaultKeyDir;
                var pass = ResolvePassphrase("VMPL_KEY_PASSPHRASE", kdir, kid);
                if (string.IsNullOrEmpty(pass))
                {
                    Console.Error.WriteLine("No passphrase: set VMPL_KEY_PASSPHRASE or generate the key with a stored passphrase.");
                    return 2;
                }
                key = PublisherKey.LoadEncrypted(Path.Combine(kdir, kid + ".key.json"), pass);
            }
            using var worker = new CkanWorker();
            try
            {
                var report = worker.Run(() => new PackExporter(Console.Out).Export(new PackExporter.Request(
                    parse.GetValue(packFile)!, parse.GetValue(ksp)!, parse.GetValue(cache)!, parse.GetValue(output)!, paths,
                    parse.GetValue(accept), parse.GetValue(allowDirty), parse.GetValue(policy), parse.GetValue(privateJson), key, parse.GetValue(version))));
                Console.WriteLine();
                Console.WriteLine($"Export      : {report.VersionDir}");
                Console.WriteLine($"Lock        : {report.LockPath}");
                Console.WriteLine($"Release assets ({report.ReleaseAssets.Count}): {string.Join(", ", report.ReleaseAssets.Select(Path.GetFileName))}");
                Console.WriteLine($"Private sideload ({report.SideloadAssets.Count}): {string.Join(", ", report.SideloadAssets.Select(Path.GetFileName))}");
                Console.WriteLine($"Elapsed     : {report.Elapsed}");
                foreach (var w in report.Warnings)
                {
                    Console.WriteLine($"warning: {w}");
                }
                return 0;
            }
            catch (LauncherException ex)
            {
                return ProfileCommands.ReportError(ex);
            }
            finally
            {
                key?.Dispose();
            }
        });
        return capture;
    }

    private static Command VerifyExportCommand()
    {
        var dir = new Argument<string>("dir") { Description = "Export version directory." };
        var keysFile = new Option<string?>("--keys") { Description = "Extra trusted-keys JSON." };
        var cmd = new Command("verify-export", "Validate an export folder: lock/inventory consistency, signatures, asset hashes, secret patterns") { dir, keysFile };
        cmd.SetAction(parse =>
        {
            var d = parse.GetValue(dir)!;
            var lockPath = Directory.EnumerateFiles(d, "*.vmpack.lock.json").FirstOrDefault();
            if (lockPath is null)
            {
                Console.Error.WriteLine("No lock in that folder.");
                return 2;
            }
            var problems = new List<string>();
            var (lockDoc, inventory, _) = ProfileOperations.LoadAndValidate(new ProfileOperations.LockSource(lockPath, Path.Combine(d, "inventory.json.gz")));
            Console.WriteLine($"lock {lockDoc.Pack.Id} {lockDoc.Pack.Version}: {lockDoc.Ckan.Modules.Count} modules, {inventory.Files.Count} files, {lockDoc.Overrides.Count} overrides");
            var verifier = SignatureVerifier.Default(parse.GetValue(keysFile));
            var trust = verifier.VerifyFile(lockPath, out var kid, out var reason);
            Console.WriteLine($"signature: {SignatureVerifier.DescribeTrust(trust)}{(kid is null ? "" : $" ({kid})")}{(reason is null ? "" : $" - {reason}")}");
            foreach (var m in lockDoc.Ckan.Modules.Where(m => m.Origin == Core.Locking.ModuleOrigin.Custom))
            {
                var file = Directory.EnumerateFiles(d, m.Artifact!.FileName ?? "*", SearchOption.AllDirectories).FirstOrDefault();
                if (file is null)
                {
                    problems.Add($"{m.Identifier}: asset {m.Artifact.FileName} missing from export");
                    continue;
                }
                var sha = Core.Hashing.Sha256Util.HashFile(file);
                if (!Core.Hashing.Sha256Util.HexEquals(sha, m.Artifact.Sha256))
                {
                    problems.Add($"{m.Identifier}: asset hash mismatch");
                }
                if (new FileInfo(file).Length >= 2L * 1024 * 1024 * 1024)
                {
                    problems.Add($"{m.Identifier}: asset is 2 GiB or larger; GitHub Releases rejects it");
                }
            }
            var scanner = new SecretScanner();
            var findings = scanner.ScanDirectory(d);
            problems.AddRange(findings.Select(f => $"secret pattern: {Path.GetRelativePath(d, f.File)}{(f.Entry is null ? "" : "!" + f.Entry)}: {f.What}"));
            foreach (var p in problems)
            {
                Console.WriteLine($"  problem: {p}");
            }
            Console.WriteLine(problems.Count == 0 ? "export OK" : $"{problems.Count} problem(s)");
            return problems.Count == 0 ? 0 : 3;
        });
        return cmd;
    }
}
