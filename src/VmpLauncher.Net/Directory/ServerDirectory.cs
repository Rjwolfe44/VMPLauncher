using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.Net.Directory;

/// <summary>How a route reaches the server. Serialized lowercase: playit | zerotier | lan | direct.</summary>
public enum RouteKind
{
    Playit,
    Zerotier,
    Lan,
    Direct,
}

/// <summary>
/// Signed, host-published list of ways to reach the pack's VMP server (<c>servers.json</c>). It never carries the
/// game password: <see cref="ServerDirectory.Parse"/> refuses any key matching /pass/i except the boolean
/// <c>server.passwordRequired</c> flag.
/// </summary>
public sealed partial record ServerDirectory
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxBytes = 256 * 1024;

    public int SchemaVersion { get; init; }
    public string PackId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
    public ServerInfo Server { get; init; } = new();
    public IReadOnlyList<ServerRoute> Routes { get; init; } = [];
    public string? Motd { get; init; }
    public MaintenanceNotice? Maintenance { get; init; }

    /// <summary>Routes best-first (lowest <see cref="ServerRoute.Priority"/> first, then file order).</summary>
    [JsonIgnore]
    public IEnumerable<ServerRoute> RoutesByPriority => Routes.Select((r, i) => (r, i)).OrderBy(t => t.r.Priority).ThenBy(t => t.i).Select(t => t.r);

    /// <summary>Strict parse of untrusted bytes: size cap, password-key refusal, schema and value checks.</summary>
    public static ServerDirectory Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxBytes)
        {
            throw Invalid($"The server list is {utf8Json.Length} bytes; the limit is {MaxBytes}.");
        }
        if (FindForbiddenKey(utf8Json) is { } key)
        {
            throw new LauncherException(ErrorCode.Tampered,
                $"The server list contains a field named '{key}'. Server lists must never carry passwords, so it was refused.",
                RecoveryHint.AskHost);
        }
        ServerDirectory? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ServerDirectory>(utf8Json, LauncherJson.Pretty);
        }
        catch (JsonException e)
        {
            throw Invalid($"The server list is not valid JSON: {e.Message}", e);
        }
        if (doc is null)
        {
            throw Invalid("The server list is empty.");
        }
        doc.Validate();
        return doc;
    }

    private void Validate()
    {
        var problems = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) problems.Add($"schemaVersion {SchemaVersion} is not supported (expected {CurrentSchemaVersion})");
        if (string.IsNullOrWhiteSpace(PackId)) problems.Add("packId is missing");
        if (Sequence < 0) problems.Add("sequence must be >= 0");
        if (Server is null || string.IsNullOrWhiteSpace(Server.Id)) problems.Add("server.id is missing");
        if (Server is { Mtu: < 0 or > 9000 }) problems.Add("server.mtu is out of range");
        if (Routes is null || Routes.Count == 0) problems.Add("routes is empty");
        foreach (var r in Routes ?? [])
        {
            if (r is null) { problems.Add("null route"); continue; }
            if (string.IsNullOrWhiteSpace(r.Id)) problems.Add("a route has no id");
            if (string.IsNullOrWhiteSpace(r.Host)) problems.Add($"route '{r.Id}' has no host");
            if (r.Port is <= 0 or > 65535) problems.Add($"route '{r.Id}' port {r.Port} is out of range");
            if (!Enum.IsDefined(r.Kind)) problems.Add($"route '{r.Id}' has an unknown kind");
        }
        if (Routes is not null && Routes.Where(r => r is not null).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            problems.Add("route ids are not unique");
        }
        if (problems.Count > 0)
        {
            throw new LauncherException(ErrorCode.InvalidPack, "The server list is malformed: " + string.Join("; ", problems) + ".", RecoveryHint.AskHost, details: problems);
        }
    }

    /// <summary>Returns the first property name matching /pass/i (other than a boolean passwordRequired), or null.</summary>
    public static string? FindForbiddenKey(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }
                var name = reader.GetString() ?? string.Empty;
                if (!PassRegex().IsMatch(name))
                {
                    continue;
                }
                if (!reader.Read())
                {
                    return name;
                }
                var allowed = string.Equals(name, "passwordRequired", StringComparison.Ordinal)
                    && reader.TokenType is JsonTokenType.True or JsonTokenType.False;
                if (!allowed)
                {
                    return name;
                }
            }
        }
        catch (JsonException e)
        {
            throw Invalid($"The server list is not valid JSON: {e.Message}", e);
        }
        return null;
    }

    [GeneratedRegex("pass", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PassRegex();

    private static LauncherException Invalid(string message, Exception? inner = null)
        => new(ErrorCode.InvalidPack, message, RecoveryHint.AskHost, inner);
}

public sealed record ServerInfo
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ProtocolForkId { get; init; } = string.Empty;
    public string RequiredBuild { get; init; } = string.Empty;
    public bool PasswordRequired { get; init; }
    public int Mtu { get; init; } = 1200;
}

public sealed record ServerRoute
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Name of the matching entry in the VMP client's server list.</summary>
    public string? VmpEntryName { get; init; }

    public RouteKind Kind { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }

    /// <summary>Lower is tried first.</summary>
    public int Priority { get; init; }

    public RequiredSoftware? RequiresSoftware { get; init; }
    public string? Notes { get; init; }
}

public sealed record RequiredSoftware
{
    public string Name { get; init; } = string.Empty;
    public string? Url { get; init; }

    /// <summary>ZeroTier network id (16 hex chars).</summary>
    public string? NetworkId { get; init; }

    /// <summary>IPv4 CIDR the route lives in, e.g. 10.37.239.0/24.</summary>
    public string? Subnet { get; init; }
}

public sealed record MaintenanceNotice
{
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset? Until { get; init; }
}
