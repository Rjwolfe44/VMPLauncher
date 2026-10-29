namespace VmpLauncher.Core.Catalog;

/// <summary>What the player asked to change in one setup, plus the choices they made while reviewing it.</summary>
public sealed record ChangeRequest
{
    public IReadOnlyCollection<string> Install { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Remove { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Upgrade { get; init; } = Array.Empty<string>();
    /// <summary>Installed like <see cref="Install"/> but marked as dependencies (a shared setup's auto-installed mods).</summary>
    public IReadOnlyCollection<string> Dependencies { get; init; } = Array.Empty<string>();
    /// <summary>Exact versions to install (a shared setup); mods not listed get their newest compatible version.</summary>
    public IReadOnlyDictionary<string, string> Versions { get; init; } = new Dictionary<string, string>();
    /// <summary>"Several mods provide X": virtual identifier → the mod the player picked.</summary>
    public IReadOnlyDictionary<string, string> ProvidesChoices { get; init; } = new Dictionary<string, string>();
    /// <summary>Recommended or suggested mods the player ticked.</summary>
    public IReadOnlyCollection<string> AcceptedSuggestions { get; init; } = Array.Empty<string>();
    /// <summary>Suggestions already shown, so the review does not offer them again.</summary>
    public IReadOnlyCollection<string> SeenSuggestions { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Install.Count == 0 && Remove.Count == 0 && Upgrade.Count == 0 && Dependencies.Count == 0;
}

public enum ChangeAction
{
    Install,
    Upgrade,
    Remove,
}

/// <param name="Reason">Why it is in the plan: picked by the player, needed by another mod, no longer needed…</param>
public sealed record PlannedChange(string Identifier, string Name, string Version, ChangeAction Action, string Reason, bool ByPlayer, long DownloadSize, bool Cached);

/// <param name="Kind">recommended, suggested or supported.</param>
public sealed record ModSuggestion(string Identifier, string Name, string Abstract, string Kind, IReadOnlyList<string> By, bool PreChecked);

/// <summary>A dependency several mods can satisfy; the player picks one before the plan can be applied.</summary>
public sealed record ProvidesChoice(string Requested, string RequestedBy, IReadOnlyList<ProvidesOption> Options);

public sealed record ProvidesOption(string Identifier, string Name, string Abstract, bool Cached, int Downloads);

/// <summary>The full effect of a <see cref="ChangeRequest"/>, computed by CKAN's resolver without changing anything.</summary>
/// <param name="Notes">Things worth knowing that do not block the change (a shared version replaced by a newer one).</param>
public sealed record ChangePlan(
    IReadOnlyList<PlannedChange> Changes,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Errors,
    IReadOnlyList<ModSuggestion> Suggestions,
    ProvidesChoice? Choice,
    long DownloadBytes,
    int DownloadCount)
{
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool CanApply => Conflicts.Count == 0 && Errors.Count == 0 && Choice is null && Changes.Count > 0;
}

public sealed record ApplyReport(int Installed, int Upgraded, int Removed, TimeSpan Elapsed, IReadOnlyList<string> Notes);
