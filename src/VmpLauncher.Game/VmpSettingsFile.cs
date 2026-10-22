using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VmpLauncher.Game;

/// <summary>
/// Reads and writes VMP's <c>GameData\VladMultiplayer\Data\settings.xml</c>.
/// The mod declares <c>encoding="utf-16"</c> but writes UTF-8 bytes and reads through a StreamReader, so this
/// class always goes through UTF-8 text (no BOM) and keeps the declaration. Unknown elements are preserved.
/// Never call while KSP is running: the mod saves settings on exit and would overwrite the change.
/// </summary>
public static class VmpSettingsFile
{
    public static readonly Regex PlayerNamePattern = new("^[A-Za-z0-9_-]{1,15}$", RegexOptions.Compiled);

    public sealed record ServerEntry(string Name, string Address, int Port, string? Password);

    public static string PathFor(string gameDir) => Path.Combine(gameDir, "GameData", "VladMultiplayer", "Data", "settings.xml");

    public static XDocument Load(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false));
        using var reader = new StringReader(text);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static XDocument CreateMinimal()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement("SettingStructure",
                new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XElement("Language", "English"),
                new XElement("PlayerName", "Player"),
                new XElement("DisclaimerAccepted", "false"),
                new XElement("Servers"),
                new XElement("Mtu", "1200"),
                new XElement("AutoExpandMtu", "false"),
                new XElement("TimeoutSeconds", "30")));
        return doc;
    }

    public static XDocument LoadOrCreate(string path) => File.Exists(path) ? Load(path) : CreateMinimal();

    public static string? GetPlayerName(XDocument doc) => doc.Root?.Element("PlayerName")?.Value;
    public static bool GetDisclaimerAccepted(XDocument doc) => string.Equals(doc.Root?.Element("DisclaimerAccepted")?.Value, "true", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ServerEntry> GetServers(XDocument doc)
        => (doc.Root?.Element("Servers")?.Elements("ServerEntry") ?? Enumerable.Empty<XElement>())
            .Select(e => new ServerEntry(
                e.Element("Name")?.Value ?? string.Empty,
                e.Element("Address")?.Value ?? string.Empty,
                int.TryParse(e.Element("Port")?.Value, out var p) ? p : 8800,
                e.Element("Password")?.Value))
            .ToList();

    public static void SetPlayerName(XDocument doc, string playerName)
    {
        if (!PlayerNamePattern.IsMatch(playerName))
        {
            throw new ArgumentException("Player name must be 1-15 characters: letters, digits, '_' or '-'.", nameof(playerName));
        }
        SetElement(doc, "PlayerName", playerName);
    }

    public static void SetDisclaimerAccepted(XDocument doc, bool accepted) => SetElement(doc, "DisclaimerAccepted", accepted ? "true" : "false");

    public static void SetMtu(XDocument doc, int mtu) => SetElement(doc, "Mtu", mtu.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Inserts or updates the server entry with the same Name (case-insensitive); other entries untouched.</summary>
    public static void UpsertServer(XDocument doc, ServerEntry entry)
    {
        var root = doc.Root ?? throw new InvalidDataException("settings.xml has no root element");
        var servers = root.Element("Servers");
        if (servers is null)
        {
            servers = new XElement("Servers");
            root.Add(servers);
        }
        var existing = servers.Elements("ServerEntry").FirstOrDefault(e => string.Equals(e.Element("Name")?.Value, entry.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new XElement("ServerEntry");
            servers.Add(existing);
            SetChild(existing, "Name", entry.Name);
        }
        SetChild(existing, "Port", entry.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetChild(existing, "Address", entry.Address);
        if (entry.Password is not null)
        {
            SetChild(existing, "Password", entry.Password);
        }
        else if (existing.Element("Password") is null)
        {
            SetChild(existing, "Password", string.Empty);
        }
    }

    public static void RemoveServer(XDocument doc, string name)
        => doc.Root?.Element("Servers")?.Elements("ServerEntry")
            .Where(e => string.Equals(e.Element("Name")?.Value, name, StringComparison.OrdinalIgnoreCase))
            .ToList().ForEach(e => e.Remove());

    /// <summary>Writes UTF-8 without BOM, keeping the mod's utf-16 declaration, via temp file + replace.</summary>
    public static void Save(XDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n");
        using (var writer = XmlWriter.Create(sb, settings))
        {
            doc.Root!.Save(writer);
        }
        var text = sb.ToString();
        // Round-trip check before touching the real file.
        using (var reader = new StringReader(text))
        {
            XDocument.Load(reader);
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void SetElement(XDocument doc, string name, string value)
    {
        var root = doc.Root ?? throw new InvalidDataException("settings.xml has no root element");
        SetChild(root, name, value);
    }

    private static void SetChild(XElement parent, string name, string value)
    {
        var el = parent.Element(name);
        if (el is null)
        {
            el = new XElement(name);
            parent.Add(el);
        }
        el.Value = value;
    }
}
