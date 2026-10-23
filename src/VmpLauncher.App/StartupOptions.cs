namespace VmpLauncher.App;

/// <summary>
/// Command-line options. Supported:
/// <c>--data-dir &lt;path&gt;</c> (isolated launcher state, used by tests and the clean-profile proof),
/// <c>--no-backdrop</c> (force the solid fallback instead of Mica),
/// <c>--background</c> (dev screenshots: open without taking focus, on a secondary monitor when there is one),
/// and a single positional activation argument: a <c>vmpack://</c> URL or a <c>*.vmpack</c> file path.
/// </summary>
public sealed record StartupOptions(string? DataDir, bool NoBackdrop, string? Activation, string? StartPage = null, bool Background = false)
{
    public static StartupOptions Parse(string[] args)
    {
        string? dataDir = null;
        var noBackdrop = false;
        string? activation = null;
        string? page = null;
        var background = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                case "--page" when i + 1 < args.Length:
                    page = args[++i];
                    break;
                case "--no-backdrop":
                    noBackdrop = true;
                    break;
                case "--background":
                    background = true;
                    break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        activation ??= args[i];
                    }
                    break;
            }
        }
        return new StartupOptions(dataDir, noBackdrop, activation, page, background);
    }
}
