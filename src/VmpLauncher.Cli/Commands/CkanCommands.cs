using System.CommandLine;
using VmpLauncher.Ckan;
using VmpLauncher.Ckan.Backend;

namespace VmpLauncher.Cli.Commands;

internal static class CkanCommands
{
    public static Command Build(Option<string?> dataDirOption)
    {
        var ckan = new Command("ckan", "CKAN backend diagnostics");

        var ksp = new Option<string>("--ksp") { Description = "Path to an existing KSP install with a CKAN registry (read-only).", Required = true };
        var locked = new Option<string?>("--locked-ckan") { Description = "A hand-made .ckan metapackage to test the importer with." };
        var cache = new Option<string?>("--cache") { Description = "An existing CKAN download cache to read from (default: launcher cache)." };
        var sample = new Option<int>("--sample") { Description = "Seed module count for the fake-instance resolution test.", DefaultValueFactory = _ => 6 };

        var probe = new Command("probe", "Run the slice-1 CKAN contract checks against a real install") { ksp, locked, cache, sample };
        probe.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            CkanBootstrap.Initialize(paths);
            var worker = new CkanWorker();
            try
            {
                return worker.Run(() => new CkanProbe(Console.Out).Run(new CkanProbe.Options(
                    parse.GetValue(ksp)!, parse.GetValue(locked), parse.GetValue(cache), paths, parse.GetValue(sample))));
            }
            finally
            {
                worker.Dispose();
            }
        });
        ckan.Subcommands.Add(probe);
        return ckan;
    }
}
