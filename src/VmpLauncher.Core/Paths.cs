namespace VmpLauncher.Core;

/// <summary>
/// Well-known launcher directories. Everything lives under <c>%LOCALAPPDATA%\VmpLauncherData</c> by default, next to
/// (never inside) Velopack's install folder <c>%LOCALAPPDATA%\VmpLauncher</c>, which Setup and uninstall delete.
/// Tests and the CLI can override the root with <c>--data-dir</c> / <c>VMPL_DATA_DIR</c>.
/// </summary>
public sealed class LauncherPaths
{
    public LauncherPaths(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public static LauncherPaths Default()
    {
        var env = Environment.GetEnvironmentVariable("VMPL_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new LauncherPaths(env);
        }
        return new LauncherPaths(DefaultRoot) { IsDefaultLocation = true };
    }

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DefaultRoot => Path.Combine(LocalAppData, "VmpLauncherData");

    /// <summary>Data root up to 0.2.1: Velopack's install folder itself. <see cref="DataMigration"/> moves it out.</summary>
    public static string LegacyDefaultRoot => Path.Combine(LocalAppData, "VmpLauncher");

    /// <summary>True when the root came from the default location rather than an override.</summary>
    public bool IsDefaultLocation { get; private init; }

    public string DataRoot { get; }
    public string CkanDir => Path.Combine(DataRoot, "ckan");
    public string CkanConfigFile => Path.Combine(CkanDir, "config.json");
    public string CkanReposDir => Path.Combine(CkanDir, "repos");
    public string CacheDir => Path.Combine(DataRoot, "cache");
    /// <summary>Verified private package zips (fetched with an invite token or dropped by the user), named by SHA-256.</summary>
    public string PrivateCacheDir => Path.Combine(CacheDir, "private");
    public string ProfilesDir => Path.Combine(DataRoot, "profiles");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string SecretsFile => Path.Combine(DataRoot, "secrets.bin");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataRoot, CkanDir, CkanReposDir, CacheDir, ProfilesDir, LogsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
