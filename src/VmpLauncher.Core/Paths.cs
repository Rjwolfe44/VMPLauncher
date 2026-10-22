namespace VmpLauncher.Core;

/// <summary>
/// Well-known launcher directories. Everything lives under <c>%LOCALAPPDATA%\VmpLauncher</c> by default
/// (never inside the Velopack app directory, which uninstall deletes). Tests and the CLI can override the
/// root with <c>--data-dir</c> / <c>VMPL_DATA_DIR</c>.
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
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new LauncherPaths(Path.Combine(local, "VmpLauncher"));
    }

    public string DataRoot { get; }
    public string CkanDir => Path.Combine(DataRoot, "ckan");
    public string CkanConfigFile => Path.Combine(CkanDir, "config.json");
    public string CkanReposDir => Path.Combine(CkanDir, "repos");
    public string CacheDir => Path.Combine(DataRoot, "cache");
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
