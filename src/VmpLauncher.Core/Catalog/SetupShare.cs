using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Core.Catalog;

/// <summary>A setup's mod list as a friend receives it: names and exact versions from the CKAN catalog only.</summary>
public sealed record SharedSetup
{
    public string Schema { get; init; } = SetupShare.Schema;
    public required string Name { get; init; }
    public string? By { get; init; }
    public string? GameVersion { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyList<SharedMod> Mods { get; init; }
}

/// <param name="Auto">Installed only as a dependency; the receiving resolver may pull it in by itself.</param>
public sealed record SharedMod(string Id, string Version, bool Auto = false);

/// <summary>
/// Shared setups travel as a <c>.vmpack</c> file (JSON) or a <c>VMPSHARE1-…</c> code (the same list, compressed,
/// short enough for a chat message when the setup is small). Parsing is strict and bounded because the text comes
/// from other people: nothing here installs anything, it only describes what to review.
/// </summary>
public static partial class SetupShare
{
    public const string Schema = "vmpshare/1";
    public const string CodePrefix = "VMPSHARE1-";
    /// <summary>Codes longer than this do not fit a Discord message; share the file instead.</summary>
    public const int ChatLimit = 1900;
    private const int MaxMods = 3000;
    private const int MaxTextBytes = 512 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"VMPSHARE1-[A-Za-z0-9_-]+")]
    private static partial Regex CodePattern();

    public static string ToJson(SharedSetup setup) => Encoding.UTF8.GetString(LauncherJson.SerializeBytes(Validate(setup)));

    public static string ToCode(SharedSetup setup)
    {
        Validate(setup);
        var sb = new StringBuilder();
        sb.Append("vmpshare1\n").Append(OneLine(setup.Name)).Append('\n').Append(OneLine(setup.By)).Append('\n').Append(OneLine(setup.GameVersion)).Append('\n');
        foreach (var m in setup.Mods)
        {
            sb.Append(m.Id).Append('\t').Append(m.Version).Append(m.Auto ? "\t1" : string.Empty).Append('\n');
        }
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            deflate.Write(bytes, 0, bytes.Length);
        }
        return CodePrefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Finds a share code anywhere in <paramref name="text"/> (a pasted message, a vmpack:// link).</summary>
    public static string? FindCode(string? text) => text is null ? null : CodePattern().Match(text) is { Success: true } m ? m.Value : null;

    /// <summary>Accepts a share code, text containing one, or the contents of a shared .vmpack file.</summary>
    public static bool TryParse(string? text, out SharedSetup? setup, out string? error)
    {
        setup = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Paste a share code or choose a shared .vmpack file.";
                return false;
            }
            if (Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
            {
                error = "That is too large to be a shared setup.";
                return false;
            }
            setup = FindCode(text) is { } code ? FromCode(code) : FromJson(text);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException or ArgumentException)
        {
            error = $"That is not a shared setup: {ex.Message}";
            return false;
        }
    }

    public static SharedSetup FromJson(string json)
    {
        var setup = JsonSerializer.Deserialize<SharedSetup>(json, LauncherJson.Pretty) ?? throw new InvalidDataException("empty");
        if (setup.Schema != Schema)
        {
            throw new InvalidDataException($"unsupported format '{setup.Schema}'");
        }
        return Validate(setup);
    }

    public static SharedSetup FromCode(string code)
    {
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            throw new FormatException("not a VMPSHARE1 code");
        }
        var b64 = code[CodePrefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - b64.Length % 4) % 4), '=');
        var compressed = Convert.FromBase64String(b64);
        using var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            output.Write(chunk, 0, n);
            if (output.Length > MaxTextBytes)
            {
                throw new InvalidDataException("the code expands to too much data");
            }
        }
        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n');
        if (lines.Length < 4 || lines[0] != "vmpshare1")
        {
            throw new InvalidDataException("unknown code version");
        }
        var mods = new List<SharedMod>();
        foreach (var line in lines.Skip(4).Where(l => l.Length > 0))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                throw new InvalidDataException("damaged mod line");
            }
            mods.Add(new SharedMod(parts[0], parts[1], parts.Length > 2 && parts[2] == "1"));
        }
        return Validate(new SharedSetup
        {
            Name = lines[1],
            By = lines[2].Length == 0 ? null : lines[2],
            GameVersion = lines[3].Length == 0 ? null : lines[3],
            Mods = mods,
        });
    }

    private static SharedSetup Validate(SharedSetup s)
    {
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 60 || s.Name.Any(char.IsControl))
        {
            throw new InvalidDataException("the setup name must be 1 to 60 characters");
        }
        if (s.By is { } by && (by.Length > 40 || by.Any(char.IsControl)))
        {
            throw new InvalidDataException("the author name is not valid");
        }
        if (s.Mods.Count is 0 or > MaxMods)
        {
            throw new InvalidDataException($"a shared setup lists 1 to {MaxMods} mods");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in s.Mods)
        {
            if (!IdentifierPattern().IsMatch(m.Id) || !seen.Add(m.Id))
            {
                throw new InvalidDataException($"'{m.Id}' is not a valid mod identifier");
            }
            if (string.IsNullOrWhiteSpace(m.Version) || m.Version.Length > 64 || m.Version.Any(char.IsControl))
            {
                throw new InvalidDataException($"{m.Id} has an invalid version");
            }
        }
        return s;
    }

    private static string OneLine(string? s) => (s ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
}
