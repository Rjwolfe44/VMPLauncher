using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.Publishing;

/// <summary>
/// Last line of defence before anything leaves the host: every output file (and every zip entry) is searched
/// for the host's real secrets (game/admin passwords from the live client settings and the private connection
/// file), their base64 forms, and generic patterns. Secret values live only in memory and are never logged.
/// </summary>
public sealed class SecretScanner
{
    private readonly List<byte[]> _needles = new();
    private readonly List<string> _needleLabels = new();

    private static readonly Regex[] GenericPatterns =
    {
        new(@"<Password>[^<]+</Password>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"""[A-Za-z]*pass(word)?[A-Za-z]*""\s*:\s*""[^""]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?i)secret_key\s*[=:]\s*\S+", RegexOptions.Compiled),
        new(@"(?i)(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}", RegexOptions.Compiled),
    };

    private static readonly string[] ForbiddenFileNames = { "settings.xml", "settings_bkp.xml", "connection.private.json", "GeneralSettings.xml" };

    public int NeedleCount => _needles.Count;

    /// <summary>Loads passwords from the live client settings.xml and HOSTING\connection.private.json (values stay in memory).</summary>
    /// <remarks>
    /// Fails closed: a configured secret source that is missing or unreadable stops the export, because scanning with
    /// fewer needles than expected would report "clean" without having looked for the real password.
    /// </remarks>
    public void LoadFromHost(string? settingsXmlPath, string? privateJsonPath)
    {
        if (settingsXmlPath is not null && File.Exists(settingsXmlPath))
        {
            try
            {
                var doc = XDocument.Parse(File.ReadAllText(settingsXmlPath, new UTF8Encoding(false)));
                foreach (var pw in doc.Descendants("Password").Select(e => e.Value).Where(v => !string.IsNullOrWhiteSpace(v)))
                {
                    Add(pw, "client settings password");
                }
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or DecoderFallbackException)
            {
                throw new LauncherException(ErrorCode.InvalidPack, $"Secret scan: could not read {settingsXmlPath}, so the game password cannot be checked for.", RecoveryHint.None, ex);
            }
        }
        if (privateJsonPath is not null && !File.Exists(privateJsonPath))
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"Secret scan: the private connection file {privateJsonPath} is configured but missing.", RecoveryHint.None);
        }
        if (privateJsonPath is not null)
        {
            var before = _needles.Count;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(privateJsonPath));
                foreach (var prop in Walk(doc.RootElement))
                {
                    if (prop.Name.Contains("pass", StringComparison.OrdinalIgnoreCase) || prop.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) || prop.Name.Contains("token", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String ? prop.Value.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            Add(v, $"private json {prop.Name}");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
            {
                throw new LauncherException(ErrorCode.InvalidPack, $"Secret scan: could not parse {privateJsonPath}.", RecoveryHint.None, ex);
            }
            if (_needles.Count == before)
            {
                throw new LauncherException(ErrorCode.InvalidPack, $"Secret scan: {privateJsonPath} holds no password, secret or token to look for; refusing to scan blind.", RecoveryHint.None);
            }
        }
    }

    public void Add(string secret, string label)
    {
        if (secret.Length < 4)
        {
            return; // too short to scan meaningfully
        }
        _needles.Add(Encoding.UTF8.GetBytes(secret));
        _needleLabels.Add(label);
        _needles.Add(Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret))));
        _needleLabels.Add(label + " (base64)");
        _needles.Add(Encoding.Unicode.GetBytes(secret));
        _needleLabels.Add(label + " (utf-16)");
    }

    public sealed record Finding(string File, string? Entry, string What);

    public IReadOnlyList<Finding> ScanDirectory(string dir)
    {
        var findings = new List<Finding>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (ForbiddenFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new Finding(file, null, $"forbidden file name {name}"));
            }
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                findings.AddRange(ScanZip(file));
            }
            else if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using var gz = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                findings.AddRange(ScanBytes(ms.ToArray(), file, "(decompressed)"));
            }
            else
            {
                var bytes = File.ReadAllBytes(file);
                findings.AddRange(ScanBytes(bytes, file, null));
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    findings.AddRange(ScanInlineContent(bytes, file));
                }
            }
        }
        return findings;
    }

    /// <summary>
    /// Override files travel inside the lock as base64 ("inlineBase64"); a base64 needle only matches a secret that
    /// starts on a 3-byte boundary, so decode every inline payload and scan the real bytes.
    /// </summary>
    private IEnumerable<Finding> ScanInlineContent(byte[] json, string file)
    {
        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            yield break;
        }
        using (doc)
        {
            foreach (var p in Walk(doc.RootElement).Where(p => p.Name.Equals("inlineBase64", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String).ToList())
            {
                byte[] decoded;
                try
                {
                    decoded = Convert.FromBase64String(p.Value.GetString()!);
                }
                catch (FormatException)
                {
                    continue;
                }
                foreach (var f in ScanBytes(decoded, file, "inline override content"))
                {
                    yield return f;
                }
            }
        }
    }

    private IEnumerable<Finding> ScanZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/'))
            {
                continue;
            }
            var name = Path.GetFileName(e.FullName);
            if (ForbiddenFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) || e.FullName.Contains("/Data/", StringComparison.OrdinalIgnoreCase) && e.FullName.Contains("VladMultiplayer", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Finding(zipPath, e.FullName, $"forbidden entry {e.FullName}");
            }
            using var ms = new MemoryStream();
            using (var s = e.Open())
            {
                s.CopyTo(ms);
            }
            foreach (var f in ScanBytes(ms.ToArray(), zipPath, e.FullName))
            {
                yield return f;
            }
        }
    }

    private IEnumerable<Finding> ScanBytes(byte[] bytes, string file, string? entry)
    {
        for (var i = 0; i < _needles.Count; i++)
        {
            if (bytes.AsSpan().IndexOf(_needles[i]) >= 0)
            {
                yield return new Finding(file, entry, $"contains {_needleLabels[i]}");
            }
        }
        if (LooksTextual(bytes))
        {
            var text = Encoding.UTF8.GetString(bytes);
            var name = Path.GetFileName(entry ?? file);
            // Mod localization files legitimately contain UI strings like <Password>Password:</Password>; the XML
            // element pattern only applies to settings-style files. Needle scans above cover everything.
            var inLocalization = (entry ?? file).Replace('\\', '/').Contains("/Localization/", StringComparison.OrdinalIgnoreCase);
            var isSettingsLike = !inLocalization && (name.Contains("setting", StringComparison.OrdinalIgnoreCase) || name.Contains("connection", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            foreach (var p in GenericPatterns)
            {
                if (ReferenceEquals(p, GenericPatterns[0]) && !isSettingsLike)
                {
                    continue;
                }
                var m = p.Match(text);
                if (m.Success && !IsEmptyPasswordElement(m.Value))
                {
                    yield return new Finding(file, entry, $"matches pattern {p}");
                }
            }
        }
    }

    private static bool IsEmptyPasswordElement(string match) => Regex.IsMatch(match, @"<Password>\s*</Password>", RegexOptions.IgnoreCase);

    private static bool LooksTextual(byte[] bytes)
    {
        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        var control = 0;
        foreach (var b in sample)
        {
            if (b == 0 || (b < 0x20 && b is not (9 or 10 or 13)))
            {
                control++;
            }
        }
        return control < sample.Length / 20;
    }

    private static IEnumerable<System.Text.Json.JsonProperty> Walk(System.Text.Json.JsonElement e)
    {
        if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                yield return p;
                foreach (var c in Walk(p.Value))
                {
                    yield return c;
                }
            }
        }
        else if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                foreach (var c in Walk(item))
                {
                    yield return c;
                }
            }
        }
    }
}
