using System.Globalization;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Transactions;

namespace VmpLauncher.Core.Profiles;

/// <summary>
/// Rebuilds a profile from scratch while keeping the player's own files: the old copy is set aside (a rename next to it,
/// so the same drive), <c>build</c> makes a fresh one in the same place, <see cref="PlayerFiles"/> moves the player's
/// files across, and the old copy is deleted. If building or moving fails, or is cancelled, the old copy is put back as
/// it was. Deleting only ever removes links: hard links, junctions and symbolic links are not followed.
/// </summary>
public static class ProfileReinstaller
{
    public const string OldCopyMarker = ".old-";

    /// <param name="Kept">Null when there was no old copy (a first install).</param>
    /// <param name="LeftOver">The old copy when it could not be deleted yet (a file in use); <see cref="CleanUpOldCopies"/> retries.</param>
    public sealed record Outcome<T>(T Built, PlayerFiles.Result? Kept, string? LeftOver);

    public static async Task<Outcome<T>> RunAsync<T>(string root, FileRules rules, Func<Task<T>> build, TextWriter? log = null)
    {
        root = root.TrimEnd('\\', '/');
        var aside = root + OldCopyMarker + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var hadCopy = Directory.Exists(root);
        if (hadCopy)
        {
            try
            {
                await MoveWithRetryAsync(root, aside).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(ErrorCode.InstallFailed, "A file in the pack copy is in use. Close KSP, CKAN and any window showing that folder, then try again.", RecoveryHint.Retry, ex);
            }
            log?.WriteLine($"Set the old copy aside: {aside}");
        }
        try
        {
            var built = await build().ConfigureAwait(false);
            if (!hadCopy)
            {
                return new Outcome<T>(built, null, null);
            }
            var kept = PlayerFiles.MoveInto(new ProfilePaths(aside).GameDir, new ProfilePaths(root).GameDir, rules, log);
            log?.WriteLine("Removing the old copy…");
            return new Outcome<T>(built, kept, TryDeleteTree(aside, log) ? null : aside);
        }
        catch (Exception) when (hadCopy)
        {
            await PutBackAsync(root, aside, log).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A rename fails while any file inside is open, and antivirus, the search indexer or a file check can hold
    /// one for a moment: keep trying for about ten seconds.</summary>
    private static async Task MoveWithRetryAsync(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    private static async Task PutBackAsync(string root, string aside, TextWriter? log)
    {
        try
        {
            if (Directory.Exists(root))
            {
                await DeleteWithRetryAsync(root).ConfigureAwait(false); // the half-built new copy
            }
            await MoveWithRetryAsync(aside, root).ConfigureAwait(false);
            log?.WriteLine("The reinstall did not finish; the old copy is back as it was.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(ErrorCode.RollbackFailed,
                $"The reinstall failed and the old copy could not be put back by itself. Nothing is lost: it is in {aside}. Try the reinstall again.", RecoveryHint.Reinstall, ex);
        }
    }

    private static async Task DeleteWithRetryAsync(string dir)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    private static bool TryDeleteTree(string dir, TextWriter? log)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.WriteLine($"Could not remove {dir} yet ({ex.Message}); it is removed later.");
            return false;
        }
    }

    /// <summary>Deletes old copies a reinstall set aside but could not remove. Only once <paramref name="current"/> is a
    /// finished install: until then an old copy may be the only good one. Returns how many were removed.</summary>
    public static int CleanUpOldCopies(ProfilePaths current, TextWriter? log = null)
    {
        if (InstalledState.TryRead(current) is null || TransactionJournal.FindIncomplete(current) is not null)
        {
            return 0;
        }
        var root = current.Root.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(root);
        if (parent is null || !Directory.Exists(parent))
        {
            return 0;
        }
        return Directory.EnumerateDirectories(parent, Path.GetFileName(root) + OldCopyMarker + "*").ToList().Count(dir => TryDeleteTree(dir, log));
    }
}
