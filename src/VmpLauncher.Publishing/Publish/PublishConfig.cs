using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Publishing.Publish;

/// <summary>
/// Host-only settings for <c>vmpl publish</c>: where the host install, the packs repository and the game server are.
/// Lives under the publisher key directory (never in a pack or a repository) so server paths stay private.
/// </summary>
public sealed record PublishConfig
{
    public string Schema { get; init; } = "vmpl-publish/1";
    /// <summary>Pack definition this configuration belongs to (absolute path).</summary>
    public required string PackFile { get; init; }
    public required string HostKspDir { get; init; }
    public required string HostCacheDir { get; init; }
    /// <summary>Local clone of the public packs repository (e.g. VMP-Packs).</summary>
    public required string PacksRepoDir { get; init; }
    public string PacksRepoBranch { get; init; } = "main";
    /// <summary>Where exports are written before staging.</summary>
    public required string ExportRoot { get; init; }
    /// <summary>The server policy as currently deployed (e.g. VMP\HOSTING\KSPModControl.xml). Patched in place on --execute.</summary>
    public required string PolicyFile { get; init; }
    /// <summary>HOSTING\connection.private.json: read only to learn secrets for the scan; never copied.</summary>
    public string? PrivateConnectionFile { get; init; }
    public required string KeyId { get; init; }
    public string? KeyDir { get; init; }
    /// <summary>SSH alias of the game server host (from ~/.ssh/config). Null disables the server step.</summary>
    public string? ServerHost { get; init; }
    /// <summary>Server install root on that host, holding Config\ and Manage-VmpServer.ps1.</summary>
    public string? ServerRoot { get; init; }
    /// <summary>Local mirror of the server's Config folder to keep in step (e.g. VMP\HOSTING\VMPServer\Config).</summary>
    public string? LocalServerConfigDir { get; init; }
    /// <summary>Public catalog URL (defaults to the packs repository raw URL derived from the pack's policyUrl).</summary>
    public string? CatalogUrl { get; init; }

    public static string DefaultPath(string keyDir, string packId) => Path.Combine(keyDir, $"publish.{packId}.json");

    public static PublishConfig? TryLoad(string path) => File.Exists(path) ? LauncherJson.ReadFile<PublishConfig>(path) : null;

    public void Save(string path) => LauncherJson.WriteFile(path, this);
}
