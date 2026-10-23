using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.App.Services;

/// <summary>What the UI shows for a failure: a plain-language title and message, a suggestion, and the technical details behind an expander.</summary>
public sealed record UserFacingError(string Title, string Message, string? Suggestion, string Details, RecoveryHint Hint);

public static class ErrorCatalog
{
    public static UserFacingError From(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return new UserFacingError("Cancelled", "The operation was cancelled and your previous files were restored.", null, ex.ToString(), RecoveryHint.None);
        }
        if (ex is LauncherException le)
        {
            var (title, suggestion) = le.Code switch
            {
                ErrorCode.GameNotFound => ("KSP not found", "Choose the folder that contains KSP_x64.exe."),
                ErrorCode.WrongGameVersion => ("Wrong KSP version", "This pack needs KSP 1.12.5 (build 03190). Update or verify your game in Steam."),
                ErrorCode.MissingDlc => ("Missing DLC", "Making History and Breaking Ground must be installed in your KSP."),
                ErrorCode.GameRunning => ("KSP is running", "Close Kerbal Space Program and try again."),
                ErrorCode.ProfileBusy => ("Another operation is running", "Wait for it to finish, or close the other launcher window."),
                ErrorCode.ProfilePathInvalid => ("Profile folder not usable", "Pick a short path on an NTFS drive, e.g. C:\\VMP."),
                ErrorCode.NotEnoughDiskSpace => ("Not enough disk space", "Free some space or choose another profile folder in Settings."),
                ErrorCode.ArtifactUnavailable => ("A download failed", "Check your internet connection and try again. If it keeps failing, the host may need to republish a package."),
                ErrorCode.ArtifactCorrupt => ("A download was corrupted", "It was discarded. Try again."),
                ErrorCode.PrivateArtifactNeeded => ("Private package needed", "Ask the host for the file and drop it onto the Install page."),
                ErrorCode.ResolverMismatch => ("Pack is inconsistent", "Ask the host to re-export the pack."),
                ErrorCode.DependencyConflict => ("Pack has conflicting mods", "Ask the host to fix the pack."),
                ErrorCode.Tampered => ("Pack failed verification", "The pack files do not match their signature or hashes. Do not install it; tell the host."),
                ErrorCode.InvalidLock or ErrorCode.InvalidPack => ("Pack cannot be read", "Ask the host to re-export the pack."),
                ErrorCode.VerifyFailed => ("Files do not match the pack", "Run Repair to restore them."),
                ErrorCode.RollbackFailed => ("Could not restore automatically", "Your previous files are kept in the profile's snapshots folder."),
                ErrorCode.NetworkUnavailable => ("No connection", "Check your internet connection and try again. The mod list you already have still works."),
                ErrorCode.ModConflict => ("These mods do not fit together", "Remove one of the conflicting mods from your changes and review again."),
                ErrorCode.CkanBusy => ("CKAN has this KSP folder open", "Close CKAN and try again."),
                ErrorCode.InstallFailed => ("Installation failed", "Your previous setup was restored. Try again; if it repeats, copy the diagnostics for the host."),
                _ => ("Something went wrong", null),
            };
            var details = string.Join("\n", new[] { $"[{le.Code}] {le.Message}" }.Concat(le.Details).Concat(le.InnerException is null ? Array.Empty<string>() : new[] { le.InnerException.ToString() }));
            return new UserFacingError(title, le.Message, suggestion, details, le.Hint);
        }
        return new UserFacingError("Unexpected error", ex.Message, "Copy the diagnostics and send them to the host.", ex.ToString(), RecoveryHint.None);
    }
}
