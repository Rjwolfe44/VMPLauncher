using System.CommandLine;
using System.Globalization;
using VmpLauncher.Ckan;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Publishing.Library;
using VmpLauncher.Publishing.Publish;

namespace VmpLauncher.Cli.Commands;

/// <summary><c>vmpl library publish</c> and <c>vmpl library verify-remote</c>: Vlad's mod library (the signed list behind the "Vlad's mods" tab).</summary>
internal static class LibraryCommands
{
    public static Command Build(Option<string?> dataDirOption)
        => new("library", "Vlad's mod library: build, sign and publish library.json, and check what friends will get") { PublishCommand(dataDirOption), VerifyRemoteCommand() };

    private static Command PublishCommand(Option<string?> dataDirOption)
    {
        var packFile = new Option<string>("--pack") { Description = "Pack definition (*.vmpack.json): its publish-config says where the packs repository, export folder and signing key are.", Required = true };
        var definition = new Option<string?>("--definition") { Description = "Library definition (default: library\\vlad.library.json in the first folder above --pack that has one)." };
        var keyDir = new Option<string?>("--key-dir") { Description = "Key directory (default %APPDATA%\\VMPLauncher\\publisher)." };
        var allowDirty = new Option<bool>("--allow-dirty") { Description = "Allow mods whose version comes from a dirty git tree." };
        var execute = new Option<bool>("--execute") { Description = "Run the external actions: release assets, copy into the packs repository, git push." };
        var yes = new Option<bool>("--yes") { Description = "Skip the PUBLISH confirmation prompt (for scripts)." };
        var noRemote = new Option<bool>("--no-remote-check") { Description = "After --execute, skip fetching the published library back for verification." };

        var cmd = new Command("publish", "Build and sign library.json from the library definition, then publish it. Dry run unless --execute.")
        { packFile, definition, keyDir, allowDirty, execute, yes, noRemote };
        cmd.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            CkanBootstrap.Initialize(paths);
            var packPath = Path.GetFullPath(parse.GetValue(packFile)!);
            var pack = LauncherJson.ReadFile<PackDefinition>(packPath);
            var kdir = parse.GetValue(keyDir) ?? ReleaseCommands.DefaultKeyDir;
            var cfgPath = PublishConfig.DefaultPath(kdir, pack.Id);
            var cfg = PublishConfig.TryLoad(cfgPath);
            if (cfg is null)
            {
                Console.Error.WriteLine($"No publish configuration at {cfgPath}. Run: vmpl publish-config --pack ... (see --help).");
                return 2;
            }
            var definitionPath = parse.GetValue(definition) is { } given ? Path.GetFullPath(given) : LibraryDefinition.FindNextTo(packPath);
            if (definitionPath is null || !File.Exists(definitionPath))
            {
                Console.Error.WriteLine(definitionPath is null
                    ? $"No library\\vlad.library.json found in or above {Path.GetDirectoryName(packPath)}. Pass --definition."
                    : $"The library definition {definitionPath} does not exist.");
                return 2;
            }
            var pass = ReleaseCommands.ResolvePassphrase(kdir, cfg.KeyId);
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
                Console.Write("This will publish Vlad's mod library publicly (and upload release assets that are missing). Type PUBLISH to continue: ");
                if (Console.ReadLine()?.Trim() != "PUBLISH")
                {
                    Console.Error.WriteLine("Aborted.");
                    return 2;
                }
            }
            using var key = PublisherKey.LoadEncrypted(Path.Combine(cfg.KeyDir ?? kdir, cfg.KeyId + ".key.json"), pass);
            var options = new LibraryPublisher.Options(cfg, definitionPath, doExecute, parse.GetValue(allowDirty), parse.GetValue(noRemote));
            LibraryPublisher.Report report;
            try
            {
                using var worker = new CkanWorker();
                report = worker.Run(() => new LibraryPublisher(Console.Out).Run(options, key));
            }
            catch (LauncherException ex)
            {
                return ProfileCommands.ReportError(ex);
            }
            var staged = report.Unchanged ? "nothing (the published library is already current)"
                : report.Executed ? report.StagingDir
                : $"{report.StagingDir} (scratch folder; the packs repository was not touched)";
            Console.WriteLine();
            Console.WriteLine($"Library     : sequence {report.Sequence}{(report.Unchanged ? " (unchanged)" : string.Empty)}, {report.Modules.Count} module(s)");
            Console.WriteLine($"Staged      : {staged}");
            Console.WriteLine($"Pushed      : {report.PushedCommit ?? (report.Executed ? "no (nothing to push)" : "no (dry run)")}");
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
            if (report.Remote is null)
            {
                return 0;
            }
            Console.WriteLine();
            Console.WriteLine("Remote check (what friends will see):");
            return PrintRemote(report.Remote);
        });
        return cmd;
    }

    private static Command VerifyRemoteCommand()
    {
        var url = new Option<string>("--url") { Description = "Where library.json is published.", DefaultValueFactory = _ => LibraryRemoteVerifier.DefaultUrl };
        var expect = new Option<long?>("--expect-sequence") { Description = "Fail unless the published library has exactly this sequence." };
        var cmd = new Command("verify-remote", "Fetch the published library and verify its signature, strict format and downloads, then list its mods") { url, expect };
        cmd.SetAction(async (parse, ct) =>
        {
            var result = await new LibraryRemoteVerifier().VerifyAsync(parse.GetValue(url)!, parse.GetValue(expect), null, ct).ConfigureAwait(false);
            return PrintRemote(result);
        });
        return cmd;
    }

    private static int PrintRemote(LibraryRemoteVerifier.Result r)
    {
        foreach (var l in r.Lines)
        {
            Console.WriteLine($"  {l}");
        }
        if (r.Modules.Count > 0)
        {
            Console.WriteLine("  modules:");
            foreach (var m in r.Modules)
            {
                Console.WriteLine($"    {m.Identifier} {m.Version}  {m.Download}");
            }
        }
        foreach (var p in r.Problems)
        {
            Console.WriteLine($"  problem: {p}");
        }
        Console.WriteLine(r.Ok ? $"remote OK (sequence {r.Sequence?.ToString(CultureInfo.InvariantCulture)})" : $"{r.Problems.Count} problem(s)");
        return r.Ok ? 0 : 3;
    }
}
