using System.Text.RegularExpressions;

namespace VmpLauncher.Core.Packs;

/// <summary>
/// Minimal, case-insensitive glob for GameData-relative paths: <c>**</c> spans directories, <c>*</c> and <c>?</c>
/// stay within one segment. Patterns and paths use forward slashes.
/// </summary>
public sealed class GlobMatcher
{
    private readonly Regex[] _patterns;

    public GlobMatcher(IEnumerable<string> globs)
    {
        _patterns = globs.Select(ToRegex).ToArray();
    }

    public bool IsEmpty => _patterns.Length == 0;

    public bool IsMatch(string relativePath)
    {
        var p = relativePath.Replace('\\', '/');
        foreach (var r in _patterns)
        {
            if (r.IsMatch(p))
            {
                return true;
            }
        }
        return false;
    }

    private static Regex ToRegex(string glob)
    {
        var g = glob.Replace('\\', '/');
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*')
            {
                if (i + 1 < g.Length && g[i + 1] == '*')
                {
                    // "**/" matches zero or more directories; a trailing "**" matches anything.
                    var consumedSlash = i + 2 < g.Length && g[i + 2] == '/';
                    sb.Append(consumedSlash ? "(?:.*/)?" : ".*");
                    i += consumedSlash ? 2 : 1;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}

public sealed class FileRuleSet
{
    public FileRuleSet(FileRules rules)
    {
        Generated = new GlobMatcher(rules.Generated);
        PerPlayer = new GlobMatcher(rules.PerPlayer);
        MutableOwned = new GlobMatcher(rules.MutableOwned);
        Stock = new GlobMatcher(rules.Stock);
    }

    public GlobMatcher Generated { get; }
    public GlobMatcher PerPlayer { get; }
    public GlobMatcher MutableOwned { get; }
    public GlobMatcher Stock { get; }

    /// <summary>Files the launcher never owns, verifies or removes.</summary>
    public bool IsIgnored(string relativePath)
        => Stock.IsMatch(relativePath) || Generated.IsMatch(relativePath) || PerPlayer.IsMatch(relativePath);
}
