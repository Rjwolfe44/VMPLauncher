namespace VmpLauncher.Ckan.Backend;

/// <summary>
/// CKAN's <c>CKAN/registry.locked</c> deletes itself when its owner closes it (DeleteOnClose), even when a process is
/// killed. After a power cut or a blue screen it stays behind, and CKAN then refuses that KSP folder ("in use") forever.
/// Whoever really holds the lock keeps it open without sharing, so a lock file that can be opened is stale and safe to
/// remove.
/// </summary>
public static class CkanLockFile
{
    public static string PathFor(string gameDir) => Path.Combine(gameDir, "CKAN", "registry.locked");

    /// <summary>Removes the lock file when nobody holds it. False when there is none or CKAN (or a launcher) has it open.</summary>
    public static bool ClearStale(string gameDir, TextWriter? log = null)
    {
        var path = PathFor(gameDir);
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
            File.Delete(path);
            log?.WriteLine("Removed a CKAN lock file left behind by a crash.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
