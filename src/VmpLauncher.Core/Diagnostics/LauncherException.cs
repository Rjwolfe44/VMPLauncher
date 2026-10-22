namespace VmpLauncher.Core.Diagnostics;

public enum ErrorCode
{
    Unknown,
    InvalidPack,
    InvalidLock,
    Tampered,
    UnsafePath,
    GameNotFound,
    WrongGameVersion,
    MissingDlc,
    GameRunning,
    ProfileBusy,
    ProfilePathInvalid,
    NotEnoughDiskSpace,
    ArtifactUnavailable,
    ArtifactCorrupt,
    PrivateArtifactNeeded,
    ResolverMismatch,
    DependencyConflict,
    InstallFailed,
    VerifyFailed,
    RollbackFailed,
    Cancelled,
}

/// <summary>Suggested next step shown in the UI next to the error.</summary>
public enum RecoveryHint
{
    None,
    Retry,
    CloseGame,
    FreeDiskSpace,
    ChooseAnotherFolder,
    AskHost,
    ProvideFile,
    Repair,
    Restore,
}

/// <summary>Launcher-level error with a user-facing message, a code the UI maps to copy, and a recovery hint.</summary>
public sealed class LauncherException : Exception
{
    public LauncherException(ErrorCode code, string userMessage, RecoveryHint hint = RecoveryHint.None, Exception? inner = null, IReadOnlyList<string>? details = null)
        : base(userMessage, inner)
    {
        Code = code;
        Hint = hint;
        Details = details ?? Array.Empty<string>();
    }

    public ErrorCode Code { get; }
    public RecoveryHint Hint { get; }
    public IReadOnlyList<string> Details { get; }
}
