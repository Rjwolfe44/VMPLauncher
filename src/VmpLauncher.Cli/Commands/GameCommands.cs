using System.CommandLine;
using VmpLauncher.Game;

namespace VmpLauncher.Cli.Commands;

internal static class GameCommands
{
    public static Command Build()
    {
        var game = new Command("game", "KSP install detection and VMP client inspection");

        var detect = new Command("detect", "Find KSP installs (Steam libraries, defaults)");
        detect.SetAction(_ =>
        {
            Console.WriteLine($"Steam libraries: {string.Join("; ", KspLocator.SteamLibraries())}");
            foreach (var i in KspLocator.Discover())
            {
                Console.WriteLine($"[{i.Source,-10}] {i.Path}");
                Console.WriteLine($"             exe={i.HasExecutable} build={i.BuildId ?? "?"} version={i.Version ?? "?"} MH={i.HasMakingHistory} BG={i.HasBreakingGround} free={i.FreeBytesOnDrive / (1024.0 * 1024 * 1024):F1} GB complete={i.IsComplete("03190")}");
            }
            return 0;
        });

        var gameDir = new Option<string>("--game") { Description = "KSP game directory.", Required = true };
        var inspect = new Command("inspect", "Inspect a KSP folder: version, DLC, installed VMP client, saved servers (passwords redacted)") { gameDir };
        inspect.SetAction(parse =>
        {
            var dir = parse.GetValue(gameDir)!;
            var info = KspLocator.Inspect(dir);
            Console.WriteLine($"{info.Path}: exe={info.HasExecutable} build={info.BuildId} version={info.Version} MH={info.HasMakingHistory} BG={info.HasBreakingGround}");
            var client = LmpClientInspector.Inspect(dir);
            Console.WriteLine(client is null
                ? "VMP client: not installed"
                : $"VMP client: build '{client.ExactBuild}' assembly {client.AssemblyVersion} autojoin={client.SupportsAutoJoin} metadata=[{string.Join(", ", client.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}]");
            var settingsPath = VmpSettingsFile.PathFor(dir);
            if (File.Exists(settingsPath))
            {
                var doc = VmpSettingsFile.Load(settingsPath);
                Console.WriteLine($"settings.xml: player='{VmpSettingsFile.GetPlayerName(doc)}' disclaimer={VmpSettingsFile.GetDisclaimerAccepted(doc)}");
                foreach (var s in VmpSettingsFile.GetServers(doc))
                {
                    Console.WriteLine($"  server '{s.Name}' {s.Address}:{s.Port} password={(string.IsNullOrEmpty(s.Password) ? "(none)" : "(set, hidden)")}");
                }
            }
            else
            {
                Console.WriteLine("settings.xml: absent");
            }
            return 0;
        });

        game.Subcommands.Add(detect);
        game.Subcommands.Add(inspect);
        return game;
    }
}
