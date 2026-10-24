using System.CommandLine;
using VmpLauncher.Ckan;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Net.Probing;
using VmpLauncher.Publishing.Policy;
using VmpLauncher.Publishing.Publish;

namespace VmpLauncher.Cli.Commands;

/// <summary><c>vmpl publish</c>, <c>vmpl publish-config</c>, <c>vmpl verify-remote</c>, <c>vmpl policy update</c>.</summary>
internal static class ReleaseCommands
{
    public static IEnumerable<Command> Build(Option<string?> dataDirOption)
    {
        yield return PublishConfigCommand();
        yield return PublishCommand(dataDirOption);
        yield return VerifyRemoteCommand();
        yield return PolicyUpdateCommand();
    }

    internal static string DefaultKeyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMPLauncher", "publisher");

    private static Command PublishConfigCommand()
    {
        var packFile = new Option<string>("--pack") { Description = "Pack definition (*.vmpack.json).", Required = true };
        var ksp = new Option<string>("--ksp") { Description = "Host KSP install (read-only).", Required = true };
        var cache = new Option<string>("--cache") { Description = "Host CKAN download cache.", Required = true };
        var repo = new Option<string>("--packs-repo") { Description = "Local clone of the public packs repository (VMP-Packs).", Required = true };
        var branch = new Option<string>("--branch") { Description = "Branch to push.", DefaultValueFactory = _ => "main" };
        var exportRoot = new Option<string>("--export-root") { Description = "Where exports are written before staging.", Required = true };
        var policy = new Option<string>("--policy") { Description = "The deployed server policy (VMP\\HOSTING\\KSPModControl.xml); patched in place on --execute.", Required = true };
        var privateJson = new Option<string?>("--private-connection") { Description = "HOSTING\\connection.private.json (read only to learn secrets for the scan; never copied)." };
        var keyId = new Option<string>("--key-id") { Description = "Publisher key id.", Required = true };
        var keyDir = new Option<string?>("--key-dir") { Description = "Key directory (default %APPDATA%\\VMPLauncher\\publisher)." };
        var serverHost = new Option<string?>("--server-host") { Description = "SSH alias of the game server (omit to never touch the server)." };
        var serverRoot = new Option<string?>("--server-root") { Description = "Server install root on that host (holds Config\\ and Manage-VmpServer.ps1)." };
        var localMirror = new Option<string?>("--local-server-config") { Description = "Local mirror of the server Config folder to keep in step." };
        var catalogUrl = new Option<string?>("--catalog-url") { Description = "Public catalog URL (default derived from the pack's policyUrl)." };

        var cmd = new Command("publish-config", "Write the host-only publish configuration for a pack (server paths stay out of the pack and the repository)")
        { packFile, ksp, cache, repo, branch, exportRoot, policy, privateJson, keyId, keyDir, serverHost, serverRoot, localMirror, catalogUrl };
        cmd.SetAction(parse =>
        {
            var pack = LauncherJson.ReadFile<PackDefinition>(parse.GetValue(packFile)!);
            var kdir = parse.GetValue(keyDir) ?? DefaultKeyDir;
            var cfg = new PublishConfig
            {
                PackFile = Path.GetFullPath(parse.GetValue(packFile)!),
                HostKspDir = Path.GetFullPath(parse.GetValue(ksp)!),
                HostCacheDir = Path.GetFullPath(parse.GetValue(cache)!),
                PacksRepoDir = Path.GetFullPath(parse.GetValue(repo)!),
                PacksRepoBranch = parse.GetValue(branch)!,
                ExportRoot = Path.GetFullPath(parse.GetValue(exportRoot)!),
                PolicyFile = Path.GetFullPath(parse.GetValue(policy)!),
                PrivateConnectionFile = parse.GetValue(privateJson) is { } pj ? Path.GetFullPath(pj) : null,
                KeyId = parse.GetValue(keyId)!,
                KeyDir = parse.GetValue(keyDir),
                ServerHost = parse.GetValue(serverHost),
                ServerRoot = parse.GetValue(serverRoot),
                LocalServerConfigDir = parse.GetValue(localMirror) is { } lm ? Path.GetFullPath(lm) : null,
                CatalogUrl = parse.GetValue(catalogUrl),
            };
            var path = PublishConfig.DefaultPath(kdir, pack.Id);
            Directory.CreateDirectory(kdir);
            cfg.Save(path);
            Console.WriteLine($"Saved {path}");
            Console.WriteLine(cfg.ServerHost is null ? "Server step: disabled (no --server-host)." : $"Server step: {cfg.ServerHost} -> {cfg.ServerRoot}");
            return 0;
        });
        return cmd;
    }

    private static Command PublishCommand(Option<string?> dataDirOption)
    {
        var packFile = new Option<string>("--pack") { Description = "Pack definition (*.vmpack.json); its publish-config must exist.", Required = true };
        var keyDir = new Option<string?>("--key-dir") { Description = "Key directory (default %APPDATA%\\VMPLauncher\\publisher)." };
        var version = new Option<string?>("--version") { Description = "Pack version (default: next yyyy.MM.dd.N)." };
        var notes = new Option<string?>("--notes") { Description = "Release notes line(s) for the changelog and commit message." };
        var generated = new Option<string?>("--policy-generated") { Description = "A KSPModControl.xml freshly generated in-game (+ SHA); use after adding/removing mods." };
        var allowPatched = new Option<bool>("--allow-patched-policy") { Description = "Accept a patched policy even when DLLs were added or removed (parts list unchanged)." };
        var execute = new Option<bool>("--execute") { Description = "Stage into the real repository and run the external actions (push, release assets, server policy)." };
        var yes = new Option<bool>("--yes") { Description = "Skip the PUBLISH confirmation prompt (for scripts)." };
        var skipServer = new Option<bool>("--skip-server") { Description = "Do not touch the game server even if the policy changed." };
        var skipAssets = new Option<bool>("--skip-assets") { Description = "Do not create GitHub release assets." };
        var accept = new Option<bool>("--accept-overrides") { Description = "Ship modified managed files as deliberate replace overrides." };
        var allowDirty = new Option<bool>("--allow-dirty") { Description = "Allow custom packages built from a dirty git tree." };
        var noRemote = new Option<bool>("--no-remote-check") { Description = "After --execute, skip fetching the published files back for verification." };

        var cmd = new Command("publish", "Release a pack version: policy -> export -> verify -> stage -> push + server update. Dry run unless --execute.")
        { packFile, keyDir, version, notes, generated, allowPatched, execute, yes, skipServer, skipAssets, accept, allowDirty, noRemote };
        cmd.SetAction(async (parse, ct) =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            CkanBootstrap.Initialize(paths);
            var pack = LauncherJson.ReadFile<PackDefinition>(parse.GetValue(packFile)!);
            var kdir = parse.GetValue(keyDir) ?? DefaultKeyDir;
            var cfgPath = PublishConfig.DefaultPath(kdir, pack.Id);
            var cfg = PublishConfig.TryLoad(cfgPath);
            if (cfg is null)
            {
                Console.Error.WriteLine($"No publish configuration at {cfgPath}. Run: vmpl publish-config --pack ... (see --help).");
                return 2;
            }
            var pass = ResolvePassphrase(kdir, cfg.KeyId);
            if (string.IsNullOrEmpty(pass))
            {
                Console.Error.WriteLine("No passphrase: set VMPL_KEY_PASSPHRASE or generate the key with a stored passphrase.");
                return 2;
            }
            var doExecute = parse.GetValue(execute);
            if (doExecute && !parse.GetValue(yes))
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("--execute needs --yes when stdin is not a terminal.");
                    return 2;
                }
                Console.Write($"This will push {pack.Id} publicly and may restart the game server. Type PUBLISH to continue: ");
                if (Console.ReadLine()?.Trim() != "PUBLISH")
                {
                    Console.Error.WriteLine("Aborted.");
                    return 2;
                }
            }
            using var key = PublisherKey.LoadEncrypted(Path.Combine(cfg.KeyDir ?? kdir, cfg.KeyId + ".key.json"), pass);
            var options = new Publisher.Options(cfg, parse.GetValue(version), parse.GetValue(notes), parse.GetValue(generated), parse.GetValue(allowPatched),
                doExecute, parse.GetValue(skipServer), parse.GetValue(skipAssets), parse.GetValue(accept), parse.GetValue(allowDirty));
            Publisher.Report report;
            try
            {
                using var worker = new CkanWorker();
                report = worker.Run(() => new Publisher(Console.Out).Run(options, key, paths, ct));
            }
            catch (LauncherException ex)
            {
                return ProfileCommands.ReportError(ex);
            }
            Console.WriteLine();
            Console.WriteLine($"Version     : {report.Version}");
            Console.WriteLine($"Export      : {report.ExportVersionDir}");
            Console.WriteLine($"Staged      : {report.StagedRepoDir}{(report.Executed ? "" : " (scratch mirror; nothing in the real repository changed)")}");
            Console.WriteLine($"Policy      : {report.Policy.Summary}");
            Console.WriteLine($"Pushed      : {report.PushedCommit ?? "no (dry run)"}");
            Console.WriteLine($"Elapsed     : {report.Elapsed}");
            foreach (var w in report.Warnings)
            {
                Console.WriteLine($"warning: {w}");
            }
            if (!report.Executed)
            {
                Console.WriteLine("Dry run only. Re-run with --execute to publish.");
                return 0;
            }
            if (parse.GetValue(noRemote))
            {
                return 0;
            }
            var catalogUrl = cfg.CatalogUrl ?? DeriveCatalogUrl(pack);
            if (catalogUrl is null)
            {
                Console.WriteLine("No catalog URL known; skipping the remote check.");
                return 0;
            }
            Console.WriteLine();
            Console.WriteLine("Remote check (what friends will see):");
            var remote = await new RemoteVerifier(probe: new VmpServerProbe()).VerifyAsync(catalogUrl, pack.Id, report.Version, ct).ConfigureAwait(false);
            return PrintRemote(remote);
        });
        return cmd;
    }

    private static Command VerifyRemoteCommand()
    {
        var packId = new Option<string>("--pack-id") { Description = "Pack id in the catalog.", Required = true };
        var catalog = new Option<string>("--catalog") { Description = "Catalog URL.", DefaultValueFactory = _ => "https://raw.githubusercontent.com/Rjwolfe44/VMP-Packs/main/catalog.json" };
        var expect = new Option<string?>("--expect-version") { Description = "Fail unless the stable channel is this version." };
        var noProbe = new Option<bool>("--no-probe") { Description = "Do not probe the server routes." };
        var cmd = new Command("verify-remote", "Fetch the published catalog, channel, lock, inventory, policy and servers.json and verify signatures, hashes and reachability") { packId, catalog, expect, noProbe };
        cmd.SetAction(async (parse, ct) =>
        {
            var r = await new RemoteVerifier(probe: parse.GetValue(noProbe) ? null : new VmpServerProbe()).VerifyAsync(parse.GetValue(catalog)!, parse.GetValue(packId)!, parse.GetValue(expect), ct).ConfigureAwait(false);
            return PrintRemote(r);
        });
        return cmd;
    }

    private static Command PolicyUpdateCommand()
    {
        var policy = new Option<string>("--policy") { Description = "Current KSPModControl.xml.", Required = true };
        var game = new Option<string?>("--game") { Description = "KSP folder whose GameData DLLs are the truth (not needed with --generated)." };
        var generated = new Option<string?>("--generated") { Description = "Normalize this in-game generated file instead of patching --policy." };
        var output = new Option<string?>("--out") { Description = "Output path (default: overwrite --policy)." };
        var update = new Command("update", "Re-hash the policy's DLL list against a GameData folder (or normalize an in-game file)") { policy, game, generated, output };
        update.SetAction(parse =>
        {
            var outPath = parse.GetValue(output) ?? parse.GetValue(policy)!;
            try
            {
                if (parse.GetValue(generated) is null && parse.GetValue(game) is null)
                {
                    Console.Error.WriteLine("--game is required unless --generated is given.");
                    return 2;
                }
                var r = parse.GetValue(generated) is { } g
                    ? ModControlPatcher.Normalize(g, outPath)
                    : ModControlPatcher.Patch(parse.GetValue(policy)!, parse.GetValue(game)!, outPath);
                static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "(none)" : sha[..Math.Min(8, sha.Length)] + "...";
                foreach (var ch in r.Changes)
                {
                    Console.WriteLine($"  {ch.Kind,-8} {ch.Path}{(ch.Kind == "updated" ? $"  {Short(ch.OldSha)} -> {Short(ch.NewSha)}" : "")}");
                }
                Console.WriteLine($"{r.Summary} -> {r.OutputPath}");
                if (r.StructureChanged)
                {
                    Console.WriteLine("note: DLLs were added or removed; the AllowedParts list may be stale. Regenerate in-game if mods changed.");
                }
                return 0;
            }
            catch (LauncherException ex)
            {
                return ProfileCommands.ReportError(ex);
            }
        });
        var cmd = new Command("policy", "Server policy (KSPModControl.xml) tools") { update };
        return cmd;
    }

    private static int PrintRemote(RemoteVerifier.Result r)
    {
        foreach (var l in r.Lines)
        {
            Console.WriteLine($"  {l}");
        }
        foreach (var p in r.Problems)
        {
            Console.WriteLine($"  problem: {p}");
        }
        Console.WriteLine(r.Ok ? $"remote OK (stable = {r.PublishedVersion})" : $"{r.Problems.Count} problem(s)");
        return r.Ok ? 0 : 3;
    }

    internal static string? ResolvePassphrase(string keyDir, string keyId)
    {
        var fromEnv = Environment.GetEnvironmentVariable("VMPL_KEY_PASSPHRASE");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }
        return OperatingSystem.IsWindows() ? PassphraseStore.TryLoad(keyDir, keyId) : null;
    }

    private static string? DeriveCatalogUrl(PackDefinition pack)
    {
        var url = pack.Multiplayer.PolicyUrl ?? pack.Multiplayer.ServerDirectoryUrl;
        if (url is null)
        {
            return null;
        }
        var i = url.IndexOf("/packs/" + pack.Id + "/", StringComparison.Ordinal);
        return i < 0 ? null : url[..i] + "/catalog.json";
    }
}
