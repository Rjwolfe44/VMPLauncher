using System.Text.Json.Serialization;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Transactions;

public enum TxnState
{
    Planned,
    Prefetched,
    Snapshotting,
    Snapshotted,
    Applying,
    Overriding,
    Verifying,
    Committed,
    RollingBack,
    RolledBack,
    RollbackFailed,
}

/// <summary>
/// journal\&lt;txn&gt;.json: the durable record of a mutation. Written atomically after every state change so a
/// crash at any point leaves a journal that <see cref="RollbackEngine"/> can act on at next start.
/// </summary>
public sealed record TransactionJournal
{
    public required string TxnId { get; init; }
    public required string Operation { get; init; } // install | update | repair
    public TxnState State { get; init; } = TxnState.Planned;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? PreviousLockSha256 { get; init; }
    public string? TargetLockSha256 { get; init; }
    public string? TargetPackVersion { get; init; }
    public string? SnapshotDir { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public static string PathFor(ProfilePaths profile, string txnId) => Path.Combine(profile.JournalDir, txnId + ".json");

    public TransactionJournal Advance(TxnState state, string? error = null)
        => this with { State = state, UpdatedUtc = DateTimeOffset.UtcNow, Error = error ?? Error };

    public void Write(ProfilePaths profile) => LauncherJson.WriteFile(PathFor(profile, TxnId), this);

    public static IReadOnlyList<TransactionJournal> ReadAll(ProfilePaths profile)
    {
        if (!Directory.Exists(profile.JournalDir))
        {
            return Array.Empty<TransactionJournal>();
        }
        var list = new List<TransactionJournal>();
        foreach (var f in Directory.EnumerateFiles(profile.JournalDir, "*.json"))
        {
            try
            {
                list.Add(LauncherJson.ReadFile<TransactionJournal>(f));
            }
            catch (Exception)
            {
                // unreadable journal: leave it for diagnostics
            }
        }
        return list.OrderBy(j => j.StartedUtc).ToList();
    }

    /// <summary>A journal whose transaction neither committed nor rolled back: the profile may be half-changed.</summary>
    public static TransactionJournal? FindIncomplete(ProfilePaths profile)
        => ReadAll(profile).LastOrDefault(j => j.State is not (TxnState.Committed or TxnState.RolledBack or TxnState.Planned or TxnState.Prefetched));

    public static string NewId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
}

/// <summary>snapshots\&lt;txn&gt;\manifest.json: what the game folder looked like before the transaction.</summary>
public sealed record SnapshotManifest
{
    public required string TxnId { get; init; }
    public required DateTimeOffset Utc { get; init; }
    public required IReadOnlyList<SnapshotEntry> Files { get; init; }
    /// <summary>Files copied (not linked): CKAN registry and launcher state, relative to the profile root.</summary>
    public required IReadOnlyList<string> CopiedStateFiles { get; init; }
}

/// <summary>p = game-relative path (forward slashes); s = size; m = mtime ticks; f = NTFS file id of the original.</summary>
public sealed record SnapshotEntry
{
    [JsonPropertyName("p")] public required string Path { get; init; }
    [JsonPropertyName("s")] public required long Size { get; init; }
    [JsonPropertyName("m")] public required long LastWriteTicks { get; init; }
    [JsonPropertyName("f")] public string? FileId { get; init; }
}
