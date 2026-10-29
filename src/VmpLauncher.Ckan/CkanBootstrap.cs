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
            else
            {
                PinCacheDir(paths);
            }
            Environment.SetEnvironmentVariable("CKAN_CONFIG_FILE", paths.CkanConfigFile);
            _done = true;
        }
    }

    /// <summary>Keeps CKAN's download cache on the launcher's cache even if the data root moved since the file was written.</summary>
    private static void PinCacheDir(LauncherPaths paths)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.CkanConfigFile)) as System.Text.Json.Nodes.JsonObject;
            if (node is null || string.Equals(node["DownloadCacheDir"]?.GetValue<string>(), paths.CacheDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            node["DownloadCacheDir"] = paths.CacheDir;
            File.WriteAllText(paths.CkanConfigFile, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // CKAN falls back to its own default and the next start tries again.
        }
    }

    /// <summary>Version of the CKAN core library compiled into this launcher.</summary>
    public static string CoreVersion => CKAN.Meta.GetVersion();
}
