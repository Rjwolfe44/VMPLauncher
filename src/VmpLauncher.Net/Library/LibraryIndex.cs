using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Net.Directory;

namespace VmpLauncher.Net.Library;

/// <summary>One release of one mod: the CKAN module document exactly as signed, plus the two fields everything keys on.</summary>
public sealed record LibraryModule(string Identifier, string Version, JsonElement Json);

/// <summary>How the "Vlad's mods" tab presents one mod beyond its CKAN metadata.</summary>
public sealed record LibraryShowcase(string Tagline, IReadOnlyList<string> Highlights, string Accent, int Order, Uri? Banner);

/// <summary>
/// Vlad's mod library: a signed, host-published list of the KSP mods Vlad makes (<c>library.json</c> + <c>.sig</c>).
/// Each entry is a CKAN module document, so the launcher can hand the whole list to the same CKAN engine that installs
/// every other mod. The signature is what makes it trustworthy; <see cref="Parse"/> additionally refuses anything that
/// could point a download or an install somewhere it should never go, so even a mistake in a signed file cannot do harm.
/// </summary>
public sealed partial record LibraryIndex
{
    public const int CurrentSchemaVersion = 1;
    public const string VladLibraryId = "vlad";
    public const int MaxBytes = 512 * 1024;

    /// <summary>Downloads may only come from release assets in Vlad's own GitHub repositories.</summary>
    public const string AllowedDownloadPrefix = "https://github.com/Rjwolfe44/";

    /// <summary>Pictures may only come from Vlad's own GitHub repositories too.</summary>
    public static readonly string[] AllowedImagePrefixes = { "https://github.com/Rjwolfe44/", "https://raw.githubusercontent.com/Rjwolfe44/" };

    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxModules = 400;

    public int SchemaVersion { get; init; }
    public string LibraryId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Tagline { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
    public IReadOnlyList<LibraryModule> Modules { get; init; } = Array.Empty<LibraryModule>();
    public IReadOnlyDictionary<string, LibraryShowcase> Showcase { get; init; } = new Dictionary<string, LibraryShowcase>();

    /// <summary>Distinct mod identifiers, in showcase order (mods without a showcase entry last, by name).</summary>
    public IReadOnlyList<string> Identifiers => Modules
        .Select(m => m.Identifier)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(i => Showcase.TryGetValue(i, out var s) ? s.Order : int.MaxValue)
        .ThenBy(i => i, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Strict parse of untrusted bytes: size cap, secret-key refusal, schema and value checks.</summary>
    public static LibraryIndex Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxBytes)
        {
            throw Invalid($"The mod library is {utf8Json.Length} bytes; the limit is {MaxBytes}.");
        }
        if (FindSecretKey(utf8Json) is { } key)
        {
            throw new LauncherException(ErrorCode.Tampered,
                $"The mod library contains a field named '{key}'. Libraries must never carry passwords, so it was refused.", RecoveryHint.AskHost);
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException e)
        {
            throw Invalid($"The mod library is not valid JSON: {e.Message}", e);
        }
        using (doc)
        {
            var root = doc.RootElement;
            var problems = new List<string>();
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The mod library is not a JSON object.");
            }
            var index = new LibraryIndex
            {
                SchemaVersion = Int(root, "schemaVersion", problems),
                LibraryId = Str(root, "libraryId", 1, 40, problems),
                Name = Str(root, "name", 1, 80, problems),
                Tagline = OptionalStr(root, "tagline", 200, problems) ?? string.Empty,
                Sequence = Long(root, "sequence", problems),
                IssuedAt = Date(root, "issuedAt", problems),
                Modules = ReadModules(root, problems),
            };
            index = index with { Showcase = ReadShowcase(root, index.Modules, problems) };
            if (index.SchemaVersion != CurrentSchemaVersion)
            {
                problems.Add($"schemaVersion {index.SchemaVersion} is not supported (expected {CurrentSchemaVersion})");
            }
            if (index.Sequence < 0)
            {
                problems.Add("sequence must be >= 0");
            }
            if (problems.Count > 0)
            {
                throw new LauncherException(ErrorCode.InvalidPack, "The mod library is malformed: " + string.Join("; ", problems.Take(8)) + ".", RecoveryHint.AskHost, details: problems);
            }
            return index;
        }
    }

    /// <summary>
    /// The first property name containing "pass" (a library must never carry a password), or null. The showcase is keyed
    /// by mod identifier, so those keys are skipped: a mod called Compass must not make the whole library fail. (They are
    /// still checked against the identifiers of the modules the library lists.)
    /// </summary>
    internal static string? FindSecretKey(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 64 });
        var openers = new Stack<string?>();
        string? pending = null;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pending = reader.GetString();
                        var isShowcaseKey = openers.Count == 2 && openers.Peek() == "showcase";
                        if (!isShowcaseKey && pending is not null && PassPattern().IsMatch(pending))
                        {
                            return pending;
                        }
                        break;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        openers.Push(pending);
                        pending = null;
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        openers.Pop();
                        break;
                    default:
                        pending = null;
                        break;
                }
            }
        }
        catch (JsonException e)
        {
            throw Invalid($"The mod library is not valid JSON: {e.Message}", e);
        }
        return null;
    }

    // ---- modules ----

    private static List<LibraryModule> ReadModules(JsonElement root, List<string> problems)
    {
        var result = new List<LibraryModule>();
        if (!root.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
        {
            problems.Add("modules is missing");
            return result;
        }
        if (modules.GetArrayLength() > MaxModules)
        {
            problems.Add($"more than {MaxModules} modules");
            return result;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                problems.Add("a module is not an object");
                continue;
            }
            var before = problems.Count;
            var ident = Str(m, "identifier", 1, 64, problems);
            var version = Str(m, "version", 1, 64, problems);
            var label = ident.Length > 0 ? ident : "(no identifier)";
            if (ident.Length > 0 && !IdentifierPattern().IsMatch(ident))
            {
                problems.Add($"{label}: the identifier has characters that are not allowed");
            }
            if (version.Length > 0 && !VersionPattern().IsMatch(version))
            {
                problems.Add($"{label}: the version '{version}' has characters that are not allowed");
            }
            if (!seen.Add(ident + "\u0001" + version))
            {
                problems.Add($"{label} {version} is listed twice");
            }
            CheckModule(label, m, problems);
            if (problems.Count == before)
            {
                result.Add(new LibraryModule(ident, version, m.Clone()));
            }
        }
        return result;
    }

    private static void CheckModule(string label, JsonElement m, List<string> problems)
    {
        Str(m, "spec_version", 1, 16, problems, label);
        Str(m, "name", 1, 80, problems, label);
        OptionalStr(m, "abstract", 300, problems, label);
        OptionalStr(m, "description", 8000, problems, label);
        var kind = OptionalStr(m, "kind", 20, problems, label);
        if (kind is not null && !string.Equals(kind, "package", StringComparison.Ordinal))
        {
            problems.Add($"{label}: only packages are allowed in the library (kind '{kind}')");
        }
        if (!m.TryGetProperty("license", out var license) || !(license.ValueKind == JsonValueKind.String || license.ValueKind == JsonValueKind.Array))
        {
            problems.Add($"{label}: license is missing");
        }

        // The download: one https URL on a release of one of Vlad's own repositories, with a hash and a size.
        if (!m.TryGetProperty("download", out var download) || download.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{label}: download must be one URL");
        }
        else if (!IsAllowedDownload(download.GetString()!, out var why))
        {
            problems.Add($"{label}: download {why}");
        }
        if (!m.TryGetProperty("download_hash", out var hash) || hash.ValueKind != JsonValueKind.Object
            || !hash.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || !Sha256Pattern().IsMatch(sha.GetString()!))
        {
            problems.Add($"{label}: download_hash.sha256 is missing or not a SHA-256");
        }
        if (!m.TryGetProperty("download_size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes <= 0 || bytes > MaxDownloadBytes)
        {
            problems.Add($"{label}: download_size is missing or out of range");
        }

        // What the install does: only these folders, never outside the game folder.
        if (!m.TryGetProperty("install", out var install) || install.ValueKind != JsonValueKind.Array || install.GetArrayLength() is 0 or > 16)
        {
            problems.Add($"{label}: install is missing");
        }
        else
        {
            foreach (var step in install.EnumerateArray())
            {
                CheckInstallStep(label, step, problems);
            }
        }

        foreach (var relation in RelationKeys)
        {
            if (m.TryGetProperty(relation, out var list))
            {
                CheckRelations(label, relation, list, 0, problems);
            }
        }
        if (m.TryGetProperty("provides", out var provides))
        {
            if (provides.ValueKind != JsonValueKind.Array || provides.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String || !IdentifierPattern().IsMatch(p.GetString()!)))
            {
                problems.Add($"{label}: provides is not a list of identifiers");
            }
        }
        if (m.TryGetProperty("resources", out var resources))
        {
            if (resources.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{label}: resources is not an object");
            }
            else
            {
                foreach (var r in resources.EnumerateObject())
                {
                    if (r.Value.ValueKind != JsonValueKind.String || !Uri.TryCreate(r.Value.GetString(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0)
                    {
                        problems.Add($"{label}: resources.{r.Name} must be an https link");
                    }
                }
            }
        }
        if (m.TryGetProperty("tags", out var tags) && (tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() > 12
            || tags.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String || !TagPattern().IsMatch(t.GetString()!))))
        {
            problems.Add($"{label}: tags must be up to 12 short words");
        }
    }

    private static readonly string[] SelectorKeys = { "file", "find", "find_regexp" };
    private static readonly string[] PathKeys = { "file", "find", "as" };
    private static readonly string[] RelationKeys = { "depends", "recommends", "suggests", "conflicts", "supports" };

    private static readonly HashSet<string> InstallKeys = new(StringComparer.Ordinal)
    {
        "file", "find", "find_regexp", "find_matches_files", "install_to", "as", "filter", "filter_regexp", "include_only", "include_only_regexp", "comment",
    };

    private static void CheckInstallStep(string label, JsonElement step, List<string> problems)
    {
        if (step.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{label}: an install step is not an object");
            return;
        }
        foreach (var p in step.EnumerateObject())
        {
            if (!InstallKeys.Contains(p.Name))
            {
                problems.Add($"{label}: install steps may not use '{p.Name}'");
            }
        }
        var selectors = SelectorKeys.Count(k => step.TryGetProperty(k, out _));
        if (selectors != 1)
        {
            problems.Add($"{label}: an install step needs exactly one of file, find or find_regexp");
        }
        // CKAN leaves install_to out of a module's JSON when it is the default (GameData), so a missing one is fine.
        if (step.TryGetProperty("install_to", out var to) && (to.ValueKind != JsonValueKind.String || !IsAllowedInstallTarget(to.GetString()!)))
        {
            problems.Add($"{label}: install_to must be GameData, Ships or Scenarios (or a folder inside them)");
        }
        foreach (var k in PathKeys)
        {
            if (step.TryGetProperty(k, out var v) && (v.ValueKind != JsonValueKind.String || !IsSafeRelative(v.GetString()!, allowSeparators: k != "as")))
            {
                problems.Add($"{label}: install step '{k}' is not a safe relative name");
            }
        }
    }

    private static void CheckRelations(string label, string kind, JsonElement list, int depth, List<string> problems)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 64 || depth > 2)
        {
            problems.Add($"{label}: {kind} is not a list of mods");
            return;
        }
        foreach (var r in list.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{label}: a {kind} entry is not an object");
                continue;
            }
            if (r.TryGetProperty("any_of", out var any))
            {
                CheckRelations(label, kind, any, depth + 1, problems);
                continue;
            }
            if (!r.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !IdentifierPattern().IsMatch(name.GetString()!))
            {
                problems.Add($"{label}: a {kind} entry has no valid name");
            }
        }
    }

    internal static bool IsAllowedDownload(string url, out string why)
    {
        why = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            why = "must be an https link";
            return false;
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort)
        {
            why = "may not carry a user name, a port, a query or a fragment";
            return false;
        }
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/Rjwolfe44/", StringComparison.Ordinal)
            || !uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal)
            || uri.AbsolutePath.Contains("..", StringComparison.Ordinal))
        {
            why = $"must be a release download from {AllowedDownloadPrefix}";
            return false;
        }
        return true;
    }

    private static bool IsAllowedInstallTarget(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/'))
        {
            return false;
        }
        return InstallTargetPattern().IsMatch(path);
    }

    private static bool IsSafeRelative(string path, bool allowSeparators)
    {
        if (path.Length is 0 or > 200 || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl))
        {
            return false;
        }
        return allowSeparators || !path.Contains('/');
    }

    // ---- showcase ----

    private static Dictionary<string, LibraryShowcase> ReadShowcase(JsonElement root, IReadOnlyList<LibraryModule> modules, List<string> problems)
    {
        var result = new Dictionary<string, LibraryShowcase>(StringComparer.Ordinal);
        if (!root.TryGetProperty("showcase", out var showcase))
        {
            return result;
        }
        if (showcase.ValueKind != JsonValueKind.Object)
        {
            problems.Add("showcase is not an object");
            return result;
        }
        var known = modules.Select(m => m.Identifier).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in showcase.EnumerateObject())
        {
            if (!known.Contains(entry.Name))
            {
                problems.Add($"showcase describes '{entry.Name}', which is not in the library");
                continue;
            }
            var e = entry.Value;
            if (e.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"showcase.{entry.Name} is not an object");
                continue;
            }
            var tagline = OptionalStr(e, "tagline", 140, problems, "showcase." + entry.Name) ?? string.Empty;
            var accent = OptionalStr(e, "accent", 7, problems, "showcase." + entry.Name) ?? "#7C5CFF";
            if (!AccentPattern().IsMatch(accent))
            {
                problems.Add($"showcase.{entry.Name}.accent must look like #7C5CFF");
                accent = "#7C5CFF";
            }
            var highlights = new List<string>();
            if (e.TryGetProperty("highlights", out var hl))
            {
                if (hl.ValueKind != JsonValueKind.Array || hl.GetArrayLength() > 6 || hl.EnumerateArray().Any(h => h.ValueKind != JsonValueKind.String || h.GetString()!.Length is 0 or > 120 || h.GetString()!.Any(char.IsControl)))
                {
                    problems.Add($"showcase.{entry.Name}.highlights must be up to 6 short lines");
                }
                else
                {
                    highlights.AddRange(hl.EnumerateArray().Select(h => h.GetString()!));
                }
            }
            var order = e.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var ord) ? ord : 100;
            Uri? banner = null;
            if (e.TryGetProperty("banner", out var b) && b.ValueKind == JsonValueKind.String)
            {
                var text = b.GetString()!;
                if (Uri.TryCreate(text, UriKind.Absolute, out var bu) && bu.Scheme == Uri.UriSchemeHttps && bu.UserInfo.Length == 0
                    && AllowedImagePrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)) && !text.Contains("..", StringComparison.Ordinal))
                {
                    banner = bu;
                }
                else
                {
                    problems.Add($"showcase.{entry.Name}.banner must be a picture in one of Vlad's GitHub repositories");
                }
            }
            result[entry.Name] = new LibraryShowcase(tagline, highlights, accent, order, banner);
        }
        return result;
    }

    // ---- small readers ----

    private static string Str(JsonElement e, string name, int min, int max, List<string> problems, string? where = null)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{Where(where)}{name} is missing");
            return string.Empty;
        }
        var s = v.GetString()!;
        if (s.Length < min || s.Length > max || s.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            problems.Add($"{Where(where)}{name} must be {min} to {max} characters without control characters");
            return string.Empty;
        }
        return s;
    }

    private static string? OptionalStr(JsonElement e, string name, int max, List<string> problems, string? where = null)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length > max || v.GetString()!.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            problems.Add($"{Where(where)}{name} must be text of at most {max} characters");
            return null;
        }
        return v.GetString();
    }

    private static string Where(string? where) => where is null ? string.Empty : where + ": ";

    private static int Int(JsonElement e, string name, List<string> problems)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }
        problems.Add($"{name} is missing");
        return 0;
    }

    private static long Long(JsonElement e, string name, List<string> problems)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
        {
            return n;
        }
        problems.Add($"{name} is missing");
        return 0;
    }

    private static DateTimeOffset Date(JsonElement e, string name, List<string> problems)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
        {
            return d;
        }
        problems.Add($"{name} is missing or not a date");
        return default;
    }

    private static LauncherException Invalid(string message, Exception? inner = null)
        => new(ErrorCode.InvalidPack, message, RecoveryHint.AskHost, inner);

    [GeneratedRegex("pass", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PassPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+:~-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _-]{0,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccentPattern();

    [GeneratedRegex(@"^(GameData|Ships|Scenarios)(/[A-Za-z0-9 ._()+@-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallTargetPattern();
}
