using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.App.Services;

/// <summary>What the UI shows for a failure: a plain-language title and message, a suggestion, and the technical details behind an expander.</summary>
public sealed record UserFacingError(string Title, string Message, string? Suggestion, string Details, RecoveryHint Hint)
{
    /// <summary>The button that fixes this error on the error card (<see cref="RecoveryActions"/>), or null when the page's
    /// own button is the way forward (Retry) or there is nothing to click.</summary>
    public string? ActionLabel => Hint switch
    {
        RecoveryHint.Repair => "Repair files",
        RecoveryHint.Restore => "Restore",
        RecoveryHint.Reinstall => "Reinstall the pack copy",
        RecoveryHint.ChooseAnotherFolder => "Choose KSP folder",
        RecoveryHint.FreeDiskSpace => "Free up space",
        RecoveryHint.ProvideFile => "Private mods",
        _ => null,
    };
}

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
                ErrorCode.NotEnoughDiskSpace => ("Not enough disk space", "Free some space on that drive. Settings can clean up old downloads."),
                ErrorCode.ArtifactUnavailable => ("A download failed", "Check your internet connection and try again. If it keeps failing, the host may need to republish a package."),
                ErrorCode.ArtifactCorrupt => ("A download was corrupted", "It was discarded. Try again."),
                ErrorCode.PrivateArtifactNeeded => ("Private package needed", "Paste the host's invite code on Home (Private mods), or ask the host for the file."),
                ErrorCode.ResolverMismatch => ("Pack is inconsistent", "Ask the host to re-export the pack."),
                ErrorCode.DependencyConflict => ("Pack has conflicting mods", "Ask the host to fix the pack."),
                ErrorCode.Tampered => ("Pack failed verification", "The pack files do not match their signature or hashes. Do not install it; tell the host."),
                ErrorCode.InvalidLock or ErrorCode.InvalidPack => ("Pack cannot be read", "Ask the host to re-export the pack."),
                ErrorCode.VerifyFailed when le.Hint == RecoveryHint.Reinstall => ("Files still do not match the pack", "Repair could not fix it. Reinstall the pack copy: your saves, craft, screenshots and settings are kept."),
                ErrorCode.VerifyFailed => ("Files do not match the pack", "Repair puts them back from your downloads."),
                ErrorCode.RollbackFailed when le.Hint == RecoveryHint.Reinstall => ("The reinstall could not finish", "Nothing is lost. Try the reinstall again; if it keeps failing, copy the report for the host."),
                ErrorCode.RollbackFailed => ("Could not restore automatically", "Press Restore to try again. If it keeps failing, Troubleshoot > Reinstall the pack copy keeps your saves."),
                ErrorCode.NetworkUnavailable => ("No connection", "Check your internet connection and try again. The mod list you already have still works."),
                ErrorCode.ModConflict => ("These mods do not fit together", "Remove one of the conflicting mods from your changes and review again."),
                ErrorCode.CkanBusy => ("CKAN has this KSP folder open", "Close CKAN and try again. If CKAN isn't running, restart your PC (something else holds its lock)."),
                ErrorCode.InstallFailed => ("Installation failed", "Nothing was changed. Try again; if it repeats, press Copy report and send it to the host."),
                _ => ("Something went wrong", null),
            };
            var details = string.Join("\n", new[] { $"[{le.Code}] {le.Message}" }.Concat(le.Details).Concat(le.InnerException is null ? Array.Empty<string>() : new[] { le.InnerException.ToString() }));
            return new UserFacingError(title, le.Message, suggestion, details, le.Hint);
        }
        return new UserFacingError("Unexpected error", ex.Message, "Copy the diagnostics and send them to the host.", ex.ToString(), RecoveryHint.None);
    }
}
