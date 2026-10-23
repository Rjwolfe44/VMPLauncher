using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VmpLauncher.Core;

/// <summary>
/// Up to 0.2.1 the launcher kept its data in <c>%LOCALAPPDATA%\VmpLauncher</c>, which is also Velopack's install
/// folder: Setup renames a non-empty install folder and deletes it after installing, and uninstall deletes it too.
/// This moves the data next to it (same drive, so every move is a rename and hard links survive) and rewrites the
/// absolute paths a few JSON files keep. Velopack's own files are never touched.
/// </summary>
public static class DataMigration
{
    /// <summary>Everything the launcher writes into its data root.</summary>
    public static readonly IReadOnlyList<string> Items = new[]
    {
        "cache", "ckan", "logs", "profiles", "probe", "settings.json", "secrets.bin", "capture-cache.json",
    };

    // JSON under these folders never holds launcher paths and can be huge (game files, CKAN repo data, snapshots).
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "game", "repos", "snapshots", "quarantine", "private", "logs" };

    public sealed record Outcome(bool UseLegacy, IReadOnlyList<string> Notes);

    /// <summary>The default data location, migrating 0.2.1-era data into it first. Overrides skip migration.</summary>
    public static (LauncherPaths Paths, IReadOnlyList<string> Notes) ResolveDefault()
    {
        var paths = LauncherPaths.Default();
        if (!paths.IsDefaultLocation)
        {
            return (paths, Array.Empty<string>());
        }
        var outcome = Run(LauncherPaths.LegacyDefaultRoot, paths.DataRoot);
        return (outcome.UseLegacy ? new LauncherPaths(LauncherPaths.LegacyDefaultRoot) : paths, outcome.Notes);
    }

    public static Outcome Run(string legacyRoot, string targetRoot)
    {
        var notes = new List<string>();
        legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
        if (!Directory.Exists(legacyRoot))
        {
            return new Outcome(false, notes);
        }
        var present = Items.Where(i => HasData(Path.Combine(legacyRoot, i))).ToList();
        if (present.Count == 0)
        {
            return new Outcome(false, notes);
        }
        if (Items.Any(i => HasData(Path.Combine(targetRoot, i))))
        {
            notes.Add($"Launcher data exists in both {legacyRoot} and {targetRoot}; using {targetRoot} and leaving the old folder alone.");
            return new Outcome(false, notes);
        }

        var moved = new List<(string From, string To)>();
        try
        {
            Directory.CreateDirectory(targetRoot);
            foreach (var item in present)
            {
                var from = Path.Combine(legacyRoot, item);
                var to = Path.Combine(targetRoot, item);
                if (Directory.Exists(to))
                {
                    Directory.Delete(to); // empty (HasData said so); Directory.Move needs the name free
                }
                if (Directory.Exists(from))
                {
                    Directory.Move(from, to);
                }
                else
                {
                    File.Move(from, to);
                }
                moved.Add((from, to));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not move launcher data out of {legacyRoot} ({ex.Message}); using it in place this time.");
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (Directory.Exists(moved[i].To))
                    {
                        Directory.Move(moved[i].To, moved[i].From);
                    }
                    else
                    {
                        File.Move(moved[i].To, moved[i].From);
                    }
                }
                catch (Exception rex) when (rex is IOException or UnauthorizedAccessException)
                {
                    notes.Add($"Could not move {moved[i].To} back: {rex.Message}");
                }
            }
            return new Outcome(true, notes);
        }

        notes.Add($"Moved launcher data from {legacyRoot} to {targetRoot}: {string.Join(", ", present)}.");
        var rewritten = RewritePaths(targetRoot, legacyRoot, targetRoot, notes);
        if (rewritten > 0)
        {
            notes.Add($"Updated saved paths in {rewritten} file(s).");
        }
        return new Outcome(false, notes);
    }

    private static bool HasData(string path)
        => File.Exists(path) || (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());

    /// <summary>Rewrites JSON string values and property names that point into <paramref name="from"/>.</summary>
    public static int RewritePaths(string root, string from, string to, List<string>? notes = null)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        var count = 0;
        foreach (var file in JsonFiles(root))
        {
            try
            {
                var text = File.ReadAllText(file);
                if (text.IndexOf(JsonEncodedText.Encode(from, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value, StringComparison.OrdinalIgnoreCase) < 0
                    && text.IndexOf(from, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var changed = false;
                var updated = Rewrite(node, s => MapPath(s, from, to), ref changed);
                if (!changed)
                {
                    continue;
                }
                var json = updated?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new System.Text.UTF8Encoding(false));
                File.Move(tmp, file, overwrite: true);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                notes?.Add($"Could not update paths in {file}: {ex.Message}");
            }
        }
        return count;
    }

    private static string? MapPath(string s, string from, string to)
    {
        if (s.Length < from.Length || !s.StartsWith(from, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (s.Length > from.Length && s[from.Length] is not ('\\' or '/'))
        {
            return null; // e.g. "...\VmpLauncherData" when from is "...\VmpLauncher"
        }
        return to + s[from.Length..];
    }

    private static JsonNode? Rewrite(JsonNode? node, Func<string, string?> map, ref bool changed)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj.ToList())
                {
                    obj.Remove(key);
                    var newKey = map(key);
                    changed |= newKey is not null;
                    result[newKey ?? key] = Rewrite(value, map, ref changed);
                }
                return result;
            case JsonArray arr:
                var items = arr.ToList();
                arr.Clear();
                var newArr = new JsonArray();
                foreach (var item in items)
                {
                    newArr.Add(Rewrite(item, map, ref changed));
                }
                return newArr;
            case JsonValue v when v.TryGetValue<string>(out var s) && map(s) is { } mapped:
                changed = true;
                return JsonValue.Create(mapped);
            default:
                return node;
        }
    }

    private static IEnumerable<string> JsonFiles(string dir)
    {
        IEnumerable<string> files;
        IEnumerable<string> subdirs;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.json").ToList();
            subdirs = Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var f in files)
        {
            if (new FileInfo(f).Length <= 16 * 1024 * 1024)
            {
                yield return f;
            }
        }
        foreach (var d in subdirs)
        {
            if (!SkipDirs.Contains(Path.GetFileName(d)))
            {
                foreach (var f in JsonFiles(d))
                {
                    yield return f;
                }
            }
        }
    }
}
