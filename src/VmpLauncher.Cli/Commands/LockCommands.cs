using System.CommandLine;
using VmpLauncher.Ckan;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Ckan.Locking;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Cli.Commands;

internal static class LockCommands
{
    public static Command Build(Option<string?> dataDirOption)
    {
        var lockCmd = new Command("lock", "Generate and inspect pack locks");

        var ksp = new Option<string>("--ksp") { Description = "KSP install whose CKAN registry is the source of truth (read-only).", Required = true };
        var cache = new Option<string>("--cache") { Description = "CKAN download cache holding every pinned archive.", Required = true };
        var pack = new Option<string?>("--pack") { Description = "Pack definition (*.vmpack.json). If omitted a default one is written next to the lock." };
        var packId = new Option<string>("--pack-id") { Description = "Pack id when no --pack is given.", DefaultValueFactory = _ => "vmp-train-career" };
        var packName = new Option<string>("--pack-name") { Description = "Pack display name when no --pack is given.", DefaultValueFactory = _ => "VMP Train Career" };
        var version = new Option<string?>("--version") { Description = "Pack version (default: UTC timestamp yyyy.MM.dd.HHmm)." };
        var output = new Option<string>("--out") { Description = "Output directory.", Required = true };
        var verifyHashes = new Option<bool>("--verify-host-hashes") { Description = "Also hash every owned file on the host disk against the archive content (slow, finds overrides)." };

        var fromRegistry = new Command("from-registry", "Build a lock + inventory from a live KSP/CKAN install") { ksp, cache, pack, packId, packName, version, output, verifyHashes };
        fromRegistry.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            CkanBootstrap.Initialize(paths);
            var outDir = parse.GetValue(output)!;
            PackDefinition def;
            string? packSha = null;
            var packPath = parse.GetValue(pack);
            if (packPath is not null)
            {
                def = LauncherJson.ReadFile<PackDefinition>(packPath);
                packSha = Sha256Util.HashFile(packPath);
            }
            else
            {
                def = new PackDefinition
                {
                    Id = parse.GetValue(packId)!,
                    Name = parse.GetValue(packName)!,
                    Author = "Rjwolfe44",
                    Version = parse.GetValue(version) ?? DateTime.UtcNow.ToString("yyyy.MM.dd.HHmm", System.Globalization.CultureInfo.InvariantCulture),
                };
                Directory.CreateDirectory(outDir);
                var written = Path.Combine(outDir, $"{def.Id}.vmpack.json");
                LauncherJson.WriteFile(written, def);
                packSha = Sha256Util.HashFile(written);
                Console.WriteLine($"Wrote default pack definition {written}");
            }
            if (parse.GetValue(version) is { } v)
            {
                def = def with { Version = v };
            }

            using var worker = new CkanWorker();
            var report = worker.Run(() => new LockGenerator(Console.Out).Generate(new LockGenerator.Request(
                parse.GetValue(ksp)!, parse.GetValue(cache)!, def, outDir, paths, parse.GetValue(verifyHashes), packSha)));

            Console.WriteLine();
            Console.WriteLine($"Lock      : {report.LockPath}");
            Console.WriteLine($"Inventory : {report.InventoryPath}");
            Console.WriteLine($"Modules   : {report.Modules}   Files: {report.Files}   Installed bytes: {report.TotalBytes:N0}");
            Console.WriteLine($"Elapsed   : {report.Elapsed}");
            foreach (var w in report.Warnings)
            {
                Console.WriteLine($"warning: {w}");
            }
            if (report.ReproductionDiffs.Count > 0)
            {
                Console.WriteLine($"Reproduction differences ({report.ReproductionDiffs.Count}):");
                foreach (var d in report.ReproductionDiffs.Take(40))
                {
                    Console.WriteLine($"  {d}");
                }
            }
            return 0;
        });
        lockCmd.Subcommands.Add(fromRegistry);
        return lockCmd;
    }
}
