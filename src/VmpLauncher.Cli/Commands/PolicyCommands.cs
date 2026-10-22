using System.CommandLine;
using VmpLauncher.Core.Policy;
using VmpLauncher.Core.Profiles;

namespace VmpLauncher.Cli.Commands;

internal static class PolicyCommands
{
    public static Command Build(Option<string?> dataDirOption)
    {
        var profile = new Option<string?>("--profile") { Description = "Profile id (uses its game folder)." };
        var game = new Option<string?>("--game") { Description = "Or an explicit KSP game folder." };
        var policy = new Option<string>("--policy") { Description = "KSPModControl.xml (file path).", Required = true };
        var cmd = new Command("policy-check", "Compare GameData DLL hashes against the server's KSPModControl.xml exactly like the VMP client does") { profile, game, policy };
        cmd.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            var gameDir = parse.GetValue(game) ?? (parse.GetValue(profile) is { } id ? new ProfileStore(paths).Get(id).GameDir : null);
            if (gameDir is null)
            {
                Console.Error.WriteLine("Give --profile or --game.");
                return 2;
            }
            var pol = ModControlPolicy.Parse(parse.GetValue(policy)!);
            Console.WriteLine($"Policy: {pol.MandatoryPlugins.Count} mandatory DLLs, {pol.OptionalPlugins.Count} optional, {pol.ForbiddenPlugins.Count} forbidden, allowNonListed={pol.AllowNonListedPlugins}, DLC [{string.Join(", ", pol.RequiredExpansions)}], {pol.AllowedParts} parts, {pol.AllowedResources} resources");
            var r = ModControlChecker.Check(gameDir, pol);
            Console.WriteLine($"Checked {r.Checked} DLLs in {r.Elapsed}: {r.MissingMandatory.Count} missing, {r.ShaMismatch.Count} hash mismatches, {r.NonListed.Count} non-listed, {r.Forbidden.Count} forbidden, {r.MissingDlc.Count} missing DLC");
            foreach (var m in r.MissingMandatory.Concat(r.ShaMismatch).Concat(r.NonListed).Concat(r.Forbidden).Take(40))
            {
                Console.WriteLine($"  {m.Kind,-10} {m.Path}{(m.Expected is null ? "" : $"  expected {m.Expected[..Math.Min(23, m.Expected.Length)]}…")}{(m.Actual is null ? "" : $"  actual {m.Actual[..Math.Min(23, m.Actual.Length)]}…")}");
            }
            Console.WriteLine(r.Passes ? "POLICY OK: the server would accept this GameData." : "POLICY MISMATCH: the server would reject this GameData.");
            return r.Passes ? 0 : 3;
        });
        return cmd;
    }
}
