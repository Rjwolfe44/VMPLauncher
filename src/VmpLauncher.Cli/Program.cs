using System.CommandLine;
using VmpLauncher.Core;

namespace VmpLauncher.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        var dataDir = new Option<string?>("--data-dir") { Description = "Launcher data directory (default %LOCALAPPDATA%\\VmpLauncherData or VMPL_DATA_DIR)." };
        var root = new RootCommand("vmpl: VMP Launcher command line") { dataDir };

        var version = new Command("version", "Print launcher and CKAN core versions");
        version.SetAction(_ =>
        {
            Console.WriteLine($"vmpl {typeof(Program).Assembly.GetName().Version}");
            Console.WriteLine($"CKAN core {Ckan.CkanBootstrap.CoreVersion}");
            return 0;
        });
        root.Subcommands.Add(version);
        root.Subcommands.Add(Commands.CkanCommands.Build(dataDir));
        root.Subcommands.Add(Commands.LockCommands.Build(dataDir));
        foreach (var c in Commands.ProfileCommands.Build(dataDir))
        {
            root.Subcommands.Add(c);
        }
        root.Subcommands.Add(Commands.GameCommands.Build());
        root.Subcommands.Add(Commands.PolicyCommands.Build(dataDir));
        root.Subcommands.Add(Commands.NetCommands.Build());
        root.Subcommands.Add(Commands.PrivateCommands.Build());
        foreach (var c in Commands.ReleaseCommands.Build(dataDir))
        {
            root.Subcommands.Add(c);
        }
        foreach (var c in Commands.PublishCommands.Build(dataDir))
        {
            root.Subcommands.Add(c);
        }

        return root.Parse(args).Invoke();
    }

    internal static LauncherPaths ResolvePaths(string? dataDir)
        => dataDir is null ? DataMigration.ResolveDefault().Paths : new LauncherPaths(dataDir);
}
