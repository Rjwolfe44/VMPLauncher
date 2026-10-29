namespace VmpLauncher.Net.Doctor;

public enum DoctorStatus
{
    Running,
    Pass,
    Warn,
    Fail,
    Skipped,
}

/// <summary>One-click remedy the UI can offer next to a step.</summary>
public enum DoctorFix
{
    EnterPassword,
    UpdatePack,
    InstallZeroTier,
    JoinZeroTier,
    AskHostAuthorize,
    CheckFirewall,
    RefreshServers,
}

/// <param name="Id">Stable id, e.g. <c>route.playit.reach</c>. A Running step is later replaced by a final step with the same id.</param>
/// <param name="Title">Short label ("Playit tunnel: server reachable").</param>
/// <param name="Status">Running while in progress, then the verdict.</param>
/// <param name="Summary">Plain-language sentence for players.</param>
/// <param name="Details">Technical detail for the report (already redacted).</param>
/// <param name="Fix">Suggested remedy, if any.</param>
public sealed record DoctorStep(string Id, string Title, DoctorStatus Status, string Summary, string? Details = null, DoctorFix? Fix = null);
