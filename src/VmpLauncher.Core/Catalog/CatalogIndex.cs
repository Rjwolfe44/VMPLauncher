using System.Globalization;
using System.Text;

namespace VmpLauncher.Core.Catalog;

public enum CatalogFilter
{
    Compatible,
    All,
    Installed,
    Updates,
    NotInstalled,
    New,
    Incompatible,
    Cached,
    /// <summary>Only the mods in Vlad's library.</summary>
    Vlad,
}

public enum CatalogSort
{
    Popular,
    RecentlyUpdated,
    Name,
    Size,
}

/// <summary>
/// Search over a catalog snapshot. Matching follows CKAN's search: every word must appear in the name, identifier,
/// summary or authors, comparing letters and digits only ("mech jeb" and "MechJeb2" both find MechJeb).
/// </summary>
public sealed class CatalogIndex
{
    private readonly Item[] _items;

    private sealed record Item(CatalogEntry Entry, string Name, string Identifier, string Abstract, string Authors);

    public CatalogIndex(IReadOnlyList<CatalogEntry> entries)
    {
        _items = entries.Select(e => new Item(e, Normalize(e.Name), Normalize(e.Identifier), Normalize(e.Abstract), Normalize(e.Authors))).ToArray();
    }

    public int Count => _items.Length;

    /// <summary>Lower-case letters and digits only, so punctuation and spacing never decide a match.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    public static bool Matches(CatalogFilter filter, CatalogEntry e) => filter switch
    {
        CatalogFilter.Compatible => e.IsCompatible || e.IsInstalled,
        CatalogFilter.Installed => e.IsInstalled || e.IsAutodetected,
        CatalogFilter.Updates => e.HasUpdate,
        CatalogFilter.NotInstalled => !e.IsInstalled && !e.IsAutodetected && e.IsCompatible,
        CatalogFilter.New => e.IsNew,
        CatalogFilter.Incompatible => !e.IsCompatible && !e.IsInstalled,
        CatalogFilter.Cached => e.IsCached,
        CatalogFilter.Vlad => e.IsVlad,
        _ => true,
    };

    public IReadOnlyList<CatalogEntry> Query(string? text, CatalogFilter filter, string? tag, CatalogSort sort)
    {
        var terms = (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(t => t.Length > 0)
            .ToArray();
        var joined = string.Concat(terms);
        var hits = new List<(CatalogEntry Entry, int Rank, int Coverage)>();
        foreach (var it in _items)
        {
            if (!Matches(filter, it.Entry) || (tag is { Length: > 0 } && !it.Entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }
            var rank = 0;
            var all = true;
            foreach (var t in terms)
            {
                var r = it.Name == t ? 0
                    : it.Name.StartsWith(t, StringComparison.Ordinal) ? 1
                    : it.Name.Contains(t, StringComparison.Ordinal) ? 2
                    : it.Identifier.Contains(t, StringComparison.Ordinal) ? 3
                    : it.Abstract.Contains(t, StringComparison.Ordinal) || it.Authors.Contains(t, StringComparison.Ordinal) ? 4
                    : -1;
                if (r < 0)
                {
                    all = false;
                    break;
                }
                rank = Math.Max(rank, r);
            }
            if (all)
            {
                // How much of the name the search covers, in tenths: "mech jeb" puts "MechJeb 2" above
                // "MechJeb and Engineer for all!" even though the latter has more downloads.
                var coverage = rank <= 2 && it.Name.Length > 0 && it.Name.Contains(joined, StringComparison.Ordinal) ? joined.Length * 10 / it.Name.Length : 0;
                hits.Add((it.Entry, rank, coverage));
            }
        }

        IOrderedEnumerable<(CatalogEntry Entry, int Rank, int Coverage)> ordered = terms.Length > 0 && sort == CatalogSort.Popular
            ? hits.OrderBy(h => Math.Min(h.Rank, 2)).ThenByDescending(h => h.Coverage).ThenBy(h => h.Rank).ThenByDescending(h => h.Entry.Downloads)
            : sort switch
            {
                CatalogSort.RecentlyUpdated => hits.OrderByDescending(h => h.Entry.ReleaseDate ?? DateTime.MinValue),
                CatalogSort.Name => hits.OrderBy(h => h.Entry.Name, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true)),
                CatalogSort.Size => hits.OrderByDescending(h => h.Entry.DownloadSize),
                _ => hits.OrderByDescending(h => h.Entry.Downloads),
            };
        return ordered.ThenBy(h => h.Entry.Name, StringComparer.OrdinalIgnoreCase).Select(h => h.Entry).ToList();
    }
}
