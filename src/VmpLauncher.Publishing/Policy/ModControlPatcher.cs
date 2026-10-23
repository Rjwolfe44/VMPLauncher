using System.Diagnostics;
using System.Text;
using System.Xml;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Policy;

namespace VmpLauncher.Publishing.Policy;

/// <summary>
/// Keeps the server's <c>KSPModControl.xml</c> in step with the host GameData without a trip through the in-game
/// generator: every <c>MandatoryPlugins/DllFile</c> is re-hashed exactly like VMP's <c>ModSystem</c> does, new DLLs
/// are appended, vanished DLLs are dropped. Parts and resources are left alone because only the game knows them,
/// so a pack that adds or removes a mod should still regenerate in-game (see <see cref="Normalize"/>).
/// Output is written with the same XmlWriter settings as <c>update-mod-control.ps1</c>, so an unchanged policy
/// round-trips byte-for-byte.
/// </summary>
public static class ModControlPatcher
{
    public sealed record DllChange(string Path, string Kind, string? OldSha, string? NewSha);

    public sealed record Result(string OutputPath, IReadOnlyList<DllChange> Changes, int MandatoryDlls, bool Changed)
    {
        public int Updated => Changes.Count(c => c.Kind == "updated");
        public int Added => Changes.Count(c => c.Kind == "added");
        public int Removed => Changes.Count(c => c.Kind == "removed");
        /// <summary>Hash-only changes keep the part list valid; added or removed DLLs usually mean the part list is stale too.</summary>
        public bool StructureChanged => Added > 0 || Removed > 0;
        public string Summary => Changed ? $"{Updated} hash(es) updated, {Added} DLL(s) added, {Removed} removed ({MandatoryDlls} mandatory)" : $"unchanged ({MandatoryDlls} mandatory)";
    }

    /// <summary>Re-hash the policy's mandatory DLL list against <paramref name="gameDir"/>\GameData and write the result.</summary>
    public static Result Patch(string policyPath, string gameDir, string outputPath, CancellationToken ct = default)
    {
        var doc = Load(policyPath);
        var root = doc.DocumentElement!;
        var mandatory = root.SelectSingleNode("MandatoryPlugins") as XmlElement ?? throw Invalid("MandatoryPlugins section is missing");
        var optional = root.SelectSingleNode("OptionalPlugins") as XmlElement;
        if (optional is not null && optional.SelectNodes("DllFile")!.Count > 0)
        {
            throw Invalid("OptionalPlugins is not empty; run Normalize on an in-game file first");
        }

        var actual = ModControlChecker.HashGameDataDlls(gameDir, cache: null, ct);
        var gameData = Path.Combine(gameDir, "GameData");
        var changes = new List<DllChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dll in mandatory.SelectNodes("DllFile")!.Cast<XmlElement>().ToList())
        {
            var pathNode = dll.SelectSingleNode("FilePath") ?? throw Invalid("DllFile without FilePath");
            var key = pathNode.InnerText.Trim().Replace('\\', '/').ToLowerInvariant();
            if (!seen.Add(key))
            {
                throw Invalid($"duplicate DLL path {key}");
            }
            var shaNode = dll.SelectSingleNode("Sha") as XmlElement;
            var oldSha = shaNode?.InnerText.Trim();
            if (!actual.TryGetValue(key, out var newSha))
            {
                mandatory.RemoveChild(dll);
                changes.Add(new DllChange(key, "removed", oldSha, null));
                continue;
            }
            if (!string.Equals(oldSha, newSha, StringComparison.OrdinalIgnoreCase))
            {
                if (shaNode is null)
                {
                    shaNode = doc.CreateElement("Sha");
                    dll.AppendChild(shaNode);
                }
                shaNode.InnerText = newSha;
                var text = dll.SelectSingleNode("Text") as XmlElement;
                if (text is not null)
                {
                    text.InnerText = DescribeDll(Path.Combine(gameData, key.Replace('/', Path.DirectorySeparatorChar)));
                }
                changes.Add(new DllChange(key, "updated", oldSha, newSha));
            }
        }

        foreach (var (key, sha) in actual.Where(kv => !seen.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var dll = doc.CreateElement("DllFile");
            var text = doc.CreateElement("Text");
            text.InnerText = DescribeDll(Path.Combine(gameData, key.Replace('/', Path.DirectorySeparatorChar)));
            var filePath = doc.CreateElement("FilePath");
            filePath.InnerText = key;
            var shaEl = doc.CreateElement("Sha");
            shaEl.InnerText = sha;
            dll.AppendChild(text);
            dll.AppendChild(filePath);
            dll.AppendChild(shaEl);
            mandatory.AppendChild(dll);
            changes.Add(new DllChange(key, "added", null, sha));
        }

        Save(doc, outputPath);
        var count = mandatory.SelectNodes("DllFile")!.Count;
        return new Result(outputPath, changes, count, changes.Count > 0);
    }

    /// <summary>
    /// Turn a freshly generated in-game file (everything under OptionalPlugins, AllowNonListedPlugins true) into the
    /// server policy the way <c>update-mod-control.ps1</c> does: every plugin becomes mandatory with its SHA,
    /// non-listed plugins are refused.
    /// </summary>
    public static Result Normalize(string generatedPath, string outputPath)
    {
        var doc = Load(generatedPath);
        var root = doc.DocumentElement!;
        var mandatory = root.SelectSingleNode("MandatoryPlugins") as XmlElement ?? throw Invalid("MandatoryPlugins section is missing");
        var optional = root.SelectSingleNode("OptionalPlugins") as XmlElement ?? throw Invalid("OptionalPlugins section is missing");
        var plugins = mandatory.SelectNodes("DllFile")!.Cast<XmlElement>().Concat(optional.SelectNodes("DllFile")!.Cast<XmlElement>()).ToList();
        if (plugins.Count == 0)
        {
            throw Invalid("no plugins listed; generate KSPModControl + SHA in the fully loaded game first");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<DllChange>();
        foreach (var p in plugins)
        {
            var path = p.SelectSingleNode("FilePath")?.InnerText.Trim() ?? string.Empty;
            var sha = p.SelectSingleNode("Sha")?.InnerText.Trim() ?? string.Empty;
            if (path.Length == 0 || !seen.Add(path.ToLowerInvariant()))
            {
                throw Invalid($"empty or duplicate DLL path '{path}'");
            }
            if (!IsDashHex(sha))
            {
                throw Invalid($"missing SHA256 for {path}; generate with the + SHA option");
            }
            if (p.ParentNode == optional)
            {
                optional.RemoveChild(p);
                mandatory.AppendChild(p);
                changes.Add(new DllChange(path, "added", null, sha));
            }
        }
        optional.IsEmpty = true; // write <OptionalPlugins /> like the in-game serializer does for an empty list
        var allow = root.SelectSingleNode("AllowNonListedPlugins") as XmlElement;
        if (allow is null)
        {
            allow = doc.CreateElement("AllowNonListedPlugins");
            root.InsertBefore(allow, mandatory);
        }
        allow.InnerText = "false";
        Save(doc, outputPath);
        return new Result(outputPath, changes, mandatory.SelectNodes("DllFile")!.Count, true);
    }

    internal static string DescribeDll(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        string? version = null;
        try
        {
            version = FileVersionInfo.GetVersionInfo(file).FileVersion;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return $"{name}. Version: {version}";
    }

    private static bool IsDashHex(string s) => s.Length == 95 && s.Split('-').Length == 32 && s.Split('-').All(p => p.Length == 2 && p.All(Uri.IsHexDigit));

    private static XmlDocument Load(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        var doc = new XmlDocument { PreserveWhitespace = false };
        doc.Load(reader);
        if (doc.DocumentElement?.Name != "ModControlStructure")
        {
            throw Invalid("not a VMP mod control file");
        }
        return doc;
    }

    private static void Save(XmlDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), NewLineChars = "\r\n" };
        var tmp = path + ".tmp";
        using (var writer = XmlWriter.Create(tmp, settings))
        {
            doc.Save(writer);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static LauncherException Invalid(string what) => new(ErrorCode.InvalidPack, $"KSPModControl.xml: {what}", RecoveryHint.None);
}
