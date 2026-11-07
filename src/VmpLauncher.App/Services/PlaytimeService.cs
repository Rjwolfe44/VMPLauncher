using System.Globalization;
using System.IO;
using System.Text.Json;
using VmpLauncher.Game;

namespace VmpLauncher.App.Services;

/// <summary>The few things the playtime readers need from the machine, so tests never touch the real registry, Steam or CKAN.</summary>
public interface IPlaytimeEnvironment
{
    string? SteamPathFromRegistry();
    string LocalAppData { get; }
    /// <summary>Whole file as text, opened with sharing allowed; null when missing or unreadable.</summary>
    string? ReadText(string path);
    IEnumerable<string> SubDirectories(string path);
}

public sealed class WindowsPlaytimeEnvironment : IPlaytimeEnvironment
{
    public string? SteamPathFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IEnumerable<string> SubDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

public sealed record PlaytimeSnapshot(TimeSpan? Steam, DateTime? SteamLastPlayedUtc, TimeSpan? Ckan, TimeSpan Vmp, int VmpSessions)
{
    public TimeSpan Total => (Steam ?? TimeSpan.Zero) + (Ckan ?? TimeSpan.Zero) + Vmp;
}

/// <summary>
/// Total time in KSP: Steam's own counter (read-only), CKAN's per-instance playtime.json (read-only) and this launcher's counter.
/// The three are separate launches, so they add up without double counting. Read on this PC only; nothing is sent.
/// </summary>
public sealed class PlaytimeService
{
    private const string KspAppId = "220200";
    private readonly IPlaytimeEnvironment _env;
    private readonly PlaytimeCounter _counter;
    private (TimeSpan? Steam, DateTime? Last, TimeSpan? Ckan)? _cache;

    public PlaytimeService(PlaytimeCounter counter, IPlaytimeEnvironment env)
    {
        _counter = counter;
        _env = env;
    }

    /// <summary>Steam and CKAN are read once and cached until <paramref name="refresh"/>; the VMP counter is always current.</summary>
    public PlaytimeSnapshot Read(bool refresh = false)
    {
        if (refresh || _cache is null)
        {
            var (steam, last) = ReadSteam();
            _cache = (steam, last, ReadCkan());
        }
        var c = _cache.Value;
        var vmp = _counter.Total;
        return new PlaytimeSnapshot(c.Steam, c.Last, c.Ckan, TimeSpan.FromSeconds(vmp.Seconds), vmp.Sessions);
    }

    private (TimeSpan? Steam, DateTime? Last) ReadSteam()
    {
        var roots = new List<string>();
        if (_env.SteamPathFromRegistry() is { Length: > 0 } reg)
        {
            roots.Add(reg);
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Program Files\Steam");
        long minutes = 0;
        long lastUnix = 0;
        var found = false;
        foreach (var user in roots.Select(NormalizeOrNull).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                     .SelectMany(r => _env.SubDirectories(Path.Combine(r, "userdata"))))
        {
            if (_env.ReadText(Path.Combine(user, "config", "localconfig.vdf")) is { } text
                && ParseSteam(text) is { } p)
            {
                found = true;
                minutes += p.Minutes;
                lastUnix = Math.Max(lastUnix, p.LastPlayedUnix);
            }
        }
        return found
            ? (TimeSpan.FromMinutes(minutes), lastUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastUnix).UtcDateTime : null)
            : (null, null);
    }

    /// <summary>Reads only KSP's Playtime and LastPlayed from a localconfig.vdf; null when the section is missing.</summary>
    public static (long Minutes, long LastPlayedUnix)? ParseSteam(string vdfText)
    {
        var app = VdfParser.Parse(vdfText)["UserLocalConfigStore"]?["Software"]?["Valve"]?["Steam"]?["apps"]?[KspAppId];
        if (app?["Playtime"] is null)
        {
            return null;
        }
        long.TryParse(app["Playtime"]?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var min);
        long.TryParse(app["LastPlayed"]?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var last);
        // Unix seconds beyond year 9999 are nonsense; keep FromUnixTimeSeconds from throwing.
        return (Math.Min(min, PlaytimeCounter.MaxSeconds / 60), last is > 0 and < 253_402_300_799 ? last : 0);
    }

    private TimeSpan? ReadCkan()
    {
        var config = _env.ReadText(Path.Combine(_env.LocalAppData, "CKAN", "config.json"));
        if (config is null)
        {
            return null;
        }
        var sum = TimeSpan.Zero;
        var found = false;
        foreach (var dir in ParseCkanInstances(config).Select(NormalizeOrNull).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_env.ReadText(Path.Combine(dir, "CKAN", "playtime.json")) is { } json && ParseCkanPlaytime(json) is { } t)
            {
                found = true;
                sum += t;
            }
        }
        return found ? sum : null;
    }

    /// <summary>KSP instance folders from CKAN's config.json (KnownInstances name to path, or the newer GameInstances list).</summary>
    public static IEnumerable<string> ParseCkanInstances(string configJson)
    {
        var paths = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return paths;
            }
            if (root.TryGetProperty("KnownInstances", out var known) && known.ValueKind == JsonValueKind.Object)
            {
                paths.AddRange(known.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!));
            }
            if (root.TryGetProperty("GameInstances", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var inst in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    var game = inst.TryGetProperty("Game", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : "KSP";
                    if (game == "KSP" && inst.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
                    {
                        paths.Add(p.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return paths;
    }

    /// <summary>CKAN's {"time":"23:18:21.8062127"}: a .NET TimeSpan string ("d.hh:mm:ss" past a day).</summary>
    public static TimeSpan? ParseCkanPlaytime(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String
                && TimeSpan.TryParse(t.GetString(), CultureInfo.InvariantCulture, out var ts)
                && ts >= TimeSpan.Zero && ts.TotalSeconds <= PlaytimeCounter.MaxSeconds)
            {
                return ts;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? NormalizeOrNull(string p)
    {
        try { return string.IsNullOrWhiteSpace(p) ? null : Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>"123 h 45 min"; under an hour "45 min".</summary>
    public static string Format(TimeSpan t)
    {
        var minutes = (long)Math.Max(0, t.TotalMinutes);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }
}
