using VmpLauncher.Core;

namespace VmpLauncher.Ckan;

/// <summary>
/// Must run before any CKAN type is touched. CKAN resolves its config file from the
/// <c>CKAN_CONFIG_FILE</c> environment variable in a static initializer, so this is the only
/// way to keep the launcher's CKAN state separate from the user's own CKAN installation.
/// </summary>
public static class CkanBootstrap
{
    private static bool _done;
    private static readonly object Gate = new();

    public static void Initialize(LauncherPaths paths)
    {
        lock (Gate)
        {
            if (_done)
            {
                return;
            }
            paths.EnsureCreated();
            if (!File.Exists(paths.CkanConfigFile))
            {
                var json = $$"""
                {
                  "DownloadCacheDir": {{System.Text.Json.JsonSerializer.Serialize(paths.CacheDir)}},
                  "CacheSizeLimit": null,
                  "PreferredHosts": [],
                  "GlobalInstallFiltersByGame": {},
                  "GameInstances": []
                }
                """;
                File.WriteAllText(paths.CkanConfigFile, json);
            }
            Environment.SetEnvironmentVariable("CKAN_CONFIG_FILE", paths.CkanConfigFile);
            _done = true;
        }
    }

    /// <summary>Version of the CKAN core library compiled into this launcher.</summary>
    public static string CoreVersion => CKAN.Meta.GetVersion();
}
