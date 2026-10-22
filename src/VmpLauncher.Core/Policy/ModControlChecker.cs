using System.Xml;
using VmpLauncher.Core.Hashing;

namespace VmpLauncher.Core.Policy;

/// <summary>Parsed VMP server policy (KSPModControl.xml).</summary>
public sealed record ModControlPolicy(
    IReadOnlyList<string> RequiredExpansions,
    bool AllowNonListedPlugins,
    IReadOnlyDictionary<string, string> MandatoryPlugins,
    IReadOnlyDictionary<string, string> OptionalPlugins,
    IReadOnlyList<string> ForbiddenPlugins,
    int AllowedParts,
    int AllowedResources,
    string Sha256)
{
    public static ModControlPolicy Parse(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        var doc = new XmlDocument();
        using (var reader = XmlReader.Create(path, settings))
        {
            doc.Load(reader);
        }
        var root = doc.DocumentElement ?? throw new InvalidDataException("KSPModControl.xml has no root element.");
        static IEnumerable<string> Strings(XmlNode? n) => n?.SelectNodes("string")?.Cast<XmlNode>().Select(x => x.InnerText.Trim()) ?? Enumerable.Empty<string>();
        static IReadOnlyDictionary<string, string> Dlls(XmlNode? n) =>
            (n?.SelectNodes("DllFile")?.Cast<XmlNode>() ?? Enumerable.Empty<XmlNode>())
                .Select(x => (Path: x.SelectSingleNode("FilePath")?.InnerText.Trim().ToLowerInvariant().Replace('\\', '/') ?? string.Empty, Sha: x.SelectSingleNode("Sha")?.InnerText.Trim() ?? string.Empty))
                .Where(x => x.Path.Length > 0)
                .GroupBy(x => x.Path)
                .ToDictionary(g => g.Key, g => g.First().Sha, StringComparer.Ordinal);
        return new ModControlPolicy(
            Strings(root.SelectSingleNode("RequiredExpansions")).ToList(),
            string.Equals(root.SelectSingleNode("AllowNonListedPlugins")?.InnerText.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            Dlls(root.SelectSingleNode("MandatoryPlugins")),
            Dlls(root.SelectSingleNode("OptionalPlugins")),
            Strings(root.SelectSingleNode("ForbiddenPlugins")).Select(s => s.ToLowerInvariant().Replace('\\', '/')).ToList(),
            root.SelectSingleNode("AllowedParts")?.ChildNodes.Count ?? 0,
            root.SelectSingleNode("AllowedResources")?.ChildNodes.Count ?? 0,
            Sha256Util.HashFile(path));
    }
}

/// <summary>
/// Reproduces the VMP client's handshake mod check locally so a mismatch is caught in the launcher instead of in
/// the game. Mirrors <c>ModSystem.GetModFiles</c>: every *.dll under GameData except Squad, SquadExpansion and
/// LunaMultiplayer; key = lowercase GameData-relative path; value = dash-separated uppercase SHA-256.
/// </summary>
public static class ModControlChecker
{
    private static readonly string[] SkippedTopFolders = { "squad", "squadexpansion", "lunamultiplayer" };

    public sealed record Mismatch(string Path, string Kind, string? Expected, string? Actual);

    public sealed record Result(int Checked, IReadOnlyList<Mismatch> MissingMandatory, IReadOnlyList<Mismatch> ShaMismatch, IReadOnlyList<Mismatch> NonListed, IReadOnlyList<Mismatch> Forbidden, IReadOnlyList<string> MissingDlc, TimeSpan Elapsed)
    {
        public bool Passes => MissingMandatory.Count == 0 && ShaMismatch.Count == 0 && NonListed.Count == 0 && Forbidden.Count == 0 && MissingDlc.Count == 0;
        public int Problems => MissingMandatory.Count + ShaMismatch.Count + NonListed.Count + Forbidden.Count + MissingDlc.Count;
    }

    /// <summary>Hashes GameData DLLs the way VMP does. <paramref name="cache"/> maps absolute path → (size, mtimeTicks, dashHex) to skip unchanged files.</summary>
    public static Dictionary<string, string> HashGameDataDlls(string gameDir, IDictionary<string, (long Size, long Mtime, string DashHex)>? cache = null, CancellationToken ct = default)
    {
        var gameData = Path.Combine(gameDir, "GameData");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(gameData))
        {
            return result;
        }
        foreach (var file in Directory.EnumerateFiles(gameData, "*.dll", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Windows "*.dll" also matches .dllold etc.
            }
            var rel = Path.GetRelativePath(gameData, file).Replace('\\', '/');
            var top = rel.Split('/')[0].ToLowerInvariant();
            if (SkippedTopFolders.Contains(top))
            {
                continue;
            }
            var fi = new FileInfo(file);
            string dashHex;
            if (cache is not null && cache.TryGetValue(file, out var c) && c.Size == fi.Length && c.Mtime == fi.LastWriteTimeUtc.Ticks)
            {
                dashHex = c.DashHex;
            }
            else
            {
                dashHex = Sha256Util.ToVmpDashHex(Sha256Util.HashFile(file, ct));
                if (cache is not null)
                {
                    cache[file] = (fi.Length, fi.LastWriteTimeUtc.Ticks, dashHex);
                }
            }
            result[rel.ToLowerInvariant()] = dashHex;
        }
        return result;
    }

    public static Result Check(string gameDir, ModControlPolicy policy, IDictionary<string, (long Size, long Mtime, string DashHex)>? cache = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var actual = HashGameDataDlls(gameDir, cache, ct);
        var missing = new List<Mismatch>();
        var sha = new List<Mismatch>();
        var nonListed = new List<Mismatch>();
        var forbidden = new List<Mismatch>();
        foreach (var (path, expected) in policy.MandatoryPlugins)
        {
            if (!actual.TryGetValue(path, out var have))
            {
                missing.Add(new Mismatch(path, "missing", expected, null));
            }
            else if (!string.Equals(have, expected, StringComparison.OrdinalIgnoreCase))
            {
                sha.Add(new Mismatch(path, "sha", expected, have));
            }
        }
        foreach (var (path, have) in actual)
        {
            if (policy.ForbiddenPlugins.Contains(path))
            {
                forbidden.Add(new Mismatch(path, "forbidden", null, have));
            }
            else if (!policy.AllowNonListedPlugins && !policy.MandatoryPlugins.ContainsKey(path) && !policy.OptionalPlugins.ContainsKey(path))
            {
                nonListed.Add(new Mismatch(path, "non-listed", null, have));
            }
        }
        var missingDlc = policy.RequiredExpansions
            .Where(name => !File.Exists(Path.Combine(gameDir, "GameData", "SquadExpansion", name, "readme.txt")))
            .ToList();
        return new Result(actual.Count, missing, sha, nonListed, forbidden, missingDlc, sw.Elapsed);
    }
}
