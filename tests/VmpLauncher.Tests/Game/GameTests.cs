using System.IO;
using System.Text;
using VmpLauncher.Game;
using Xunit;

namespace VmpLauncher.Tests.Game;

public class VdfParserTests
{
    [Fact]
    public void Parses_library_folders()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Users\\x\\Desktop\\Steam"
                    "apps"
                    {
                        "220200"    "1234"
                    }
                }
                "1"
                {
                    "path"      "D:\\SteamLibrary"
                }
            }
            """;
        var root = VdfParser.Parse(vdf);
        Assert.Equal(@"C:\Users\x\Desktop\Steam", root.GetString("libraryfolders", "0", "path"));
        Assert.Equal(@"D:\SteamLibrary", root.GetString("libraryfolders", "1", "path"));
        Assert.Equal("1234", root.GetString("libraryfolders", "0", "apps", "220200"));
    }

    [Fact]
    public void Parses_app_manifest_installdir()
    {
        var root = VdfParser.Parse("\"AppState\"\n{\n\t\"appid\"\t\t\"220200\"\n\t\"installdir\"\t\t\"Kerbal Space Program\"\n}\n");
        Assert.Equal("Kerbal Space Program", root.GetString("AppState", "installdir"));
    }
}

public class VmpSettingsFileTests
{
    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "vmpl-tests", Guid.NewGuid().ToString("N"), "settings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private const string Sample = """
        <?xml version="1.0" encoding="utf-16"?>
        <SettingStructure xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Language>English</Language>
          <PlayerName>Player</PlayerName>
          <DisclaimerAccepted>false</DisclaimerAccepted>
          <PlayerColor>
            <r>0.5</r>
            <g>0.25</g>
            <b>1</b>
            <a>1</a>
          </PlayerColor>
          <Servers>
            <ServerEntry>
              <Port>8800</Port>
              <Name>Old LAN</Name>
              <Address>192.168.1.5</Address>
              <Password>lanpw</Password>
            </ServerEntry>
          </Servers>
          <Mtu>1200</Mtu>
          <SomeFutureSetting>keep-me</SomeFutureSetting>
        </SettingStructure>
        """;

    [Fact]
    public void Loads_utf16_declared_utf8_bytes_and_preserves_unknown_elements()
    {
        var path = WriteTemp(Sample);
        var doc = VmpSettingsFile.Load(path);
        Assert.Equal("Player", VmpSettingsFile.GetPlayerName(doc));
        Assert.False(VmpSettingsFile.GetDisclaimerAccepted(doc));
        VmpSettingsFile.SetPlayerName(doc, "Vlad");
        VmpSettingsFile.SetDisclaimerAccepted(doc, true);
        VmpSettingsFile.UpsertServer(doc, new VmpSettingsFile.ServerEntry("Train - Playit", "signaling.example.ply.gg", 18113, "secret"));
        VmpSettingsFile.UpsertServer(doc, new VmpSettingsFile.ServerEntry("old lan", "192.168.1.9", 8800, null));
        VmpSettingsFile.Save(doc, path);

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>", text, StringComparison.Ordinal);
        Assert.True(text.Contains("<SomeFutureSetting>keep-me</SomeFutureSetting>", StringComparison.Ordinal), text);
        Assert.True(text.Contains("<r>0.5</r>", StringComparison.Ordinal), text);

        var again = VmpSettingsFile.Load(path);
        var servers = VmpSettingsFile.GetServers(again);
        Assert.Equal(2, servers.Count);
        var lan = servers.Single(s => s.Name == "Old LAN");
        Assert.Equal("192.168.1.9", lan.Address);
        Assert.Equal("lanpw", lan.Password); // upsert without password keeps the existing one
        var playit = servers.Single(s => s.Name == "Train - Playit");
        Assert.Equal(18113, playit.Port);
        Assert.Equal("secret", playit.Password);
        Assert.Equal("Vlad", VmpSettingsFile.GetPlayerName(again));
        Assert.True(VmpSettingsFile.GetDisclaimerAccepted(again));
    }

    [Fact]
    public void Creates_minimal_file_when_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "vmpl-tests", Guid.NewGuid().ToString("N"), "settings.xml");
        var doc = VmpSettingsFile.LoadOrCreate(path);
        VmpSettingsFile.SetPlayerName(doc, "Kerman_1");
        VmpSettingsFile.Save(doc, path);
        Assert.True(File.Exists(path));
        Assert.Equal("Kerman_1", VmpSettingsFile.GetPlayerName(VmpSettingsFile.Load(path)));
    }

    [Theory]
    [InlineData("Vlad", true)]
    [InlineData("a_b-c123", true)]
    [InlineData("", false)]
    [InlineData("sixteen_chars_xx", false)]
    [InlineData("has space", false)]
    public void Validates_player_names(string name, bool ok)
        => Assert.Equal(ok, VmpSettingsFile.PlayerNamePattern.IsMatch(name));
}

public class LmpClientInspectorTests
{
    [Fact]
    public void Reads_informational_version_of_a_known_assembly()
    {
        // Any .NET assembly with an InformationalVersion works; use this test assembly's dependency.
        var dll = typeof(VmpSettingsFile).Assembly.Location;
        var info = LmpClientInspector.InspectDll(dll);
        Assert.NotNull(info.InformationalVersion);
        Assert.False(info.SupportsAutoJoin);
    }

    [Fact]
    public void Reads_real_vmp_client_when_present()
    {
        var hostDll = @"C:\Users\rjwol\Desktop\Kerbal Space Program\GameData\VladMultiplayer\Plugins\LmpClient.dll";
        if (!File.Exists(hostDll))
        {
            return; // not on the host machine
        }
        var info = LmpClientInspector.InspectDll(hostDll);
        Assert.StartsWith("0.2.0-train.", info.ExactBuild, StringComparison.Ordinal);
    }
}
