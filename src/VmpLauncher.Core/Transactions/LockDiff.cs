using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;

namespace VmpLauncher.Core.Transactions;

/// <summary>What changes between two locks. Drives "download only what changed" and the CKAN add/remove plan.</summary>
public sealed record LockDiff
{
    public required IReadOnlyList<LockedModule> Added { get; init; }
    public required IReadOnlyList<LockedModule> Removed { get; init; }
    /// <summary>Same identifier, different version or metadata (old, new).</summary>
    public required IReadOnlyList<(LockedModule Old, LockedModule New)> Changed { get; init; }
    public required IReadOnlyList<LockedModule> Unchanged { get; init; }
    public required IReadOnlyList<LockOverride> OverridesAdded { get; init; }
    public required IReadOnlyList<LockOverride> OverridesRemoved { get; init; }
    public required IReadOnlyList<LockOverride> OverridesChanged { get; init; }

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0 && OverridesAdded.Count == 0 && OverridesRemoved.Count == 0 && OverridesChanged.Count == 0;

    /// <summary>Modules whose archives may need fetching (artifact hash not already present is decided by the cache).</summary>
    public IEnumerable<LockedModule> ModulesNeedingArtifacts => Added.Concat(Changed.Select(c => c.New)).Where(m => m.Artifact is not null);

    public IEnumerable<string> IdentifiersToUninstall => Removed.Concat(Changed.Select(c => c.Old)).Where(m => m.Kind != ModuleKindDto.Dlc).Select(m => m.Identifier);
    public IEnumerable<LockedModule> ModulesToInstall => Added.Concat(Changed.Select(c => c.New)).Where(m => m.Kind != ModuleKindDto.Dlc);

    public static LockDiff Compute(PackLock from, PackLock to)
    {
        var oldById = from.Ckan.Modules.ToDictionary(m => m.Identifier, StringComparer.Ordinal);
        var newById = to.Ckan.Modules.ToDictionary(m => m.Identifier, StringComparer.Ordinal);
        var added = new List<LockedModule>();
        var changed = new List<(LockedModule, LockedModule)>();
        var unchanged = new List<LockedModule>();
        foreach (var n in to.Ckan.Modules)
        {
            if (!oldById.TryGetValue(n.Identifier, out var o))
            {
                added.Add(n);
            }
            else if (o.Version != n.Version || !Sha256Util.HexEquals(o.MetadataSha256, n.MetadataSha256) || !Sha256Util.HexEquals(o.Artifact?.Sha256, n.Artifact?.Sha256))
            {
                changed.Add((o, n));
            }
            else
            {
                unchanged.Add(n);
            }
        }
        var removed = from.Ckan.Modules.Where(o => !newById.ContainsKey(o.Identifier)).ToList();

        static string Key(LockOverride o) => $"{o.Op}|{o.Path}|{o.Sha256}|{o.BaseSha256}|{o.RenameTo}";
        var oldOv = from.Overrides.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var newOv = to.Overrides.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var ovAdded = to.Overrides.Where(o => !oldOv.ContainsKey(o.Id)).ToList();
        var ovRemoved = from.Overrides.Where(o => !newOv.ContainsKey(o.Id)).ToList();
        var ovChanged = to.Overrides.Where(o => oldOv.TryGetValue(o.Id, out var old) && Key(old) != Key(o)).ToList();

        return new LockDiff
        {
            Added = added, Removed = removed, Changed = changed, Unchanged = unchanged,
            OverridesAdded = ovAdded, OverridesRemoved = ovRemoved, OverridesChanged = ovChanged,
        };
    }

    public string Summary()
        => $"{Added.Count} added, {Changed.Count} changed, {Removed.Count} removed, {Unchanged.Count} unchanged; overrides +{OverridesAdded.Count} ~{OverridesChanged.Count} -{OverridesRemoved.Count}";
}
