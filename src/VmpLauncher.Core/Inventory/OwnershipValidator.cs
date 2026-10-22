using System.Text;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Security;

namespace VmpLauncher.Core.Inventory;

/// <summary>
/// Rules every lock must satisfy before it is signed (host) and before it is applied (client):
/// unique paths (case-insensitive, NFC), exactly one owner per path, overrides that name the real owner,
/// and install roots limited to the game's mod folders.
/// </summary>
public static class OwnershipValidator
{
    private static readonly string[] AllowedRoots = { "GameData/", "Ships/", "Missions/" };

    public sealed record Problem(string Code, string Message);

    public static IReadOnlyList<Problem> Validate(IReadOnlyList<InventoryEntry> files, IReadOnlyList<LockOverride> overrides, IReadOnlyCollection<string>? rootInstallOwners = null)
    {
        var problems = new List<Problem>();
        var rootOwners = new HashSet<string>(rootInstallOwners ?? Array.Empty<string>(), StringComparer.Ordinal);
        var seen = new Dictionary<string, InventoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (!SafePath.TryNormalize(f.Path, out var norm, out var reason))
            {
                problems.Add(new Problem("unsafe-path", $"{f.Path}: {reason}"));
                continue;
            }
            if (!AllowedRoots.Any(r => norm.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            {
                // A file directly in the game root (CKAN "GameRoot" install stanza) is allowed only when the lock
                // explicitly lists its owner, so the user can see which module writes outside the mod folders.
                var isRootFile = !norm.Contains('/');
                if (!isRootFile || !rootOwners.Contains(f.Owner))
                {
                    problems.Add(new Problem("root-not-allowed", $"{f.Path} ({f.Owner}): install root must be one of {string.Join(", ", AllowedRoots)} unless the owner is listed under rootInstallOwners"));
                }
            }
            var key = norm.Normalize(NormalizationForm.FormC);
            if (seen.TryGetValue(key, out var other))
            {
                problems.Add(new Problem("duplicate-owner", $"{f.Path} is claimed by both '{other.Owner}' and '{f.Owner}'"));
            }
            else
            {
                seen[key] = f;
            }
            if (string.IsNullOrWhiteSpace(f.Owner))
            {
                problems.Add(new Problem("no-owner", $"{f.Path} has no owner"));
            }
        }

        var overrideIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in overrides)
        {
            if (!overrideIds.Add(o.Id))
            {
                problems.Add(new Problem("duplicate-override-id", o.Id));
            }
            if (!SafePath.TryNormalize(o.Path, out var norm, out var reason))
            {
                problems.Add(new Problem("unsafe-path", $"override {o.Id}: {reason}"));
                continue;
            }
            seen.TryGetValue(norm, out var owned);
            switch (o.Op)
            {
                case "add":
                    if (owned is not null && owned.Class != FileClass.Override)
                    {
                        problems.Add(new Problem("add-over-owned", $"override {o.Id} adds {o.Path} but '{owned.Owner}' already owns it"));
                    }
                    if (o.Sha256 is null)
                    {
                        problems.Add(new Problem("missing-hash", $"override {o.Id} has no sha256"));
                    }
                    break;
                case "replace":
                case "disable":
                    if (o.Owner is null || o.BaseSha256 is null)
                    {
                        problems.Add(new Problem("override-needs-owner", $"override {o.Id} ({o.Op}) must name owner and baseSha256"));
                    }
                    if (o.Op == "replace" && o.Sha256 is null)
                    {
                        problems.Add(new Problem("missing-hash", $"override {o.Id} has no sha256"));
                    }
                    if (o.Op == "disable" && string.IsNullOrWhiteSpace(o.RenameTo))
                    {
                        problems.Add(new Problem("disable-needs-rename", $"override {o.Id} must say renameTo"));
                    }
                    break;
                default:
                    problems.Add(new Problem("unknown-op", $"override {o.Id} has unknown op '{o.Op}'"));
                    break;
            }
        }
        return problems;
    }
}
