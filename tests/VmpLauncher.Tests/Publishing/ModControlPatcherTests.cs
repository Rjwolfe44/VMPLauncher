using System.IO;
using System.Security.Cryptography;
using System.Text;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Publishing.Policy;
using Xunit;

namespace VmpLauncher.Tests.Publishing;

public sealed class ModControlPatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-patcher-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;

    public ModControlPatcherTests()
    {
        _game = Path.Combine(_root, "KSP");
        Directory.CreateDirectory(Path.Combine(_game, "GameData", "ModA", "Plugins"));
        Directory.CreateDirectory(Path.Combine(_game, "GameData", "ModB"));
        Directory.CreateDirectory(Path.Combine(_game, "GameData", "Squad", "Plugins"));
        WriteDll("ModA/Plugins/ModA.dll", "a-v1");
        WriteDll("ModB/ModB.dll", "b-v1");
        WriteDll("Squad/Plugins/Stock.dll", "stock"); // skipped like VMP does
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Unchanged_policy_round_trips_byte_for_byte()
    {
        var policy = WritePolicy(("moda/plugins/moda.dll", Hash("a-v1")), ("modb/modb.dll", Hash("b-v1")));
        var before = File.ReadAllBytes(policy);
        var r = ModControlPatcher.Patch(policy, _game, policy);
        Assert.False(r.Changed);
        Assert.Equal(2, r.MandatoryDlls);
        Assert.Equal(before, File.ReadAllBytes(policy));
    }

    [Fact]
    public void Rebuilt_dll_gets_new_hash_added_and_removed_are_tracked_and_parts_survive()
    {
        var policy = WritePolicy(("moda/plugins/moda.dll", Hash("a-v0")), ("modgone/gone.dll", Hash("gone")));
        var outPath = Path.Combine(_root, "out.xml");
        var r = ModControlPatcher.Patch(policy, _game, outPath);
        Assert.True(r.Changed);
        Assert.Equal(1, r.Updated);
        Assert.Equal(1, r.Added);    // modb/modb.dll
        Assert.Equal(1, r.Removed);  // modgone/gone.dll
        Assert.True(r.StructureChanged);
        var text = File.ReadAllText(outPath);
        Assert.Contains(Hash("a-v1"), text, StringComparison.Ordinal);
        Assert.Contains("<FilePath>modb/modb.dll</FilePath>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("modgone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("squad/", text, StringComparison.Ordinal);
        Assert.Contains("<string>somePart</string>", text, StringComparison.Ordinal);
        Assert.Contains("<string>MakingHistory</string>", text, StringComparison.Ordinal);
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.False(File.ReadAllBytes(outPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "no BOM");
        // Hash-only change is not a structure change.
        var policy2 = WritePolicy(("moda/plugins/moda.dll", Hash("a-v0")), ("modb/modb.dll", Hash("b-v1")));
        var r2 = ModControlPatcher.Patch(policy2, _game, Path.Combine(_root, "out2.xml"));
        Assert.True(r2.Changed);
        Assert.False(r2.StructureChanged);
    }

    [Fact]
    public void Duplicate_paths_are_rejected()
    {
        var policy = WritePolicy(("moda/plugins/moda.dll", Hash("a-v1")), ("moda/plugins/moda.dll", Hash("a-v1")));
        Assert.Throws<LauncherException>(() => ModControlPatcher.Patch(policy, _game, Path.Combine(_root, "dup.xml")));
    }

    [Fact]
    public void Normalize_moves_optional_plugins_to_mandatory_and_forbids_non_listed()
    {
        var generated = Path.Combine(_root, "generated.xml");
        File.WriteAllText(generated, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ModControlStructure xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <RequiredExpansions><string>MakingHistory</string></RequiredExpansions>
              <AllowNonListedPlugins>true</AllowNonListedPlugins>
              <MandatoryPlugins />
              <OptionalPlugins>
                <DllFile><Text>ModA. Version: 1</Text><FilePath>moda/plugins/moda.dll</FilePath><Sha>{Hash("a-v1")}</Sha></DllFile>
                <DllFile><Text>ModB. Version: 1</Text><FilePath>modb/modb.dll</FilePath><Sha>{Hash("b-v1")}</Sha></DllFile>
              </OptionalPlugins>
              <ForbiddenPlugins />
              <AllowedParts><string>somePart</string></AllowedParts>
              <AllowedResources><string>LiquidFuel</string></AllowedResources>
            </ModControlStructure>
            """);
        var outPath = Path.Combine(_root, "normalized.xml");
        var r = ModControlPatcher.Normalize(generated, outPath);
        Assert.Equal(2, r.MandatoryDlls);
        var text = File.ReadAllText(outPath);
        Assert.Contains("<AllowNonListedPlugins>false</AllowNonListedPlugins>", text, StringComparison.Ordinal);
        Assert.Contains("<OptionalPlugins />", text, StringComparison.Ordinal);
        // The normalized file is a valid patch input and already matches the game.
        var r2 = ModControlPatcher.Patch(outPath, _game, outPath);
        Assert.False(r2.Changed);
    }

    [Fact]
    public void Normalize_refuses_missing_sha()
    {
        var generated = Path.Combine(_root, "nosha.xml");
        File.WriteAllText(generated, """
            <?xml version="1.0" encoding="utf-8"?>
            <ModControlStructure>
              <RequiredExpansions /><AllowNonListedPlugins>true</AllowNonListedPlugins><MandatoryPlugins />
              <OptionalPlugins><DllFile><Text>x</Text><FilePath>moda/plugins/moda.dll</FilePath><Sha></Sha></DllFile></OptionalPlugins>
              <ForbiddenPlugins /><AllowedParts /><AllowedResources />
            </ModControlStructure>
            """);
        Assert.Throws<LauncherException>(() => ModControlPatcher.Normalize(generated, Path.Combine(_root, "n.xml")));
    }

    private void WriteDll(string rel, string content)
        => File.WriteAllText(Path.Combine(_game, "GameData", rel.Replace('/', Path.DirectorySeparatorChar)), content);

    private static string Hash(string content) => BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private string WritePolicy(params (string Path, string Sha)[] dlls)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        sb.Append("<ModControlStructure xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n");
        sb.Append("  <RequiredExpansions>\r\n    <string>MakingHistory</string>\r\n  </RequiredExpansions>\r\n");
        sb.Append("  <AllowNonListedPlugins>false</AllowNonListedPlugins>\r\n  <MandatoryPlugins>\r\n");
        foreach (var (p, s) in dlls)
        {
            sb.Append("    <DllFile>\r\n      <Text>").Append(Path.GetFileNameWithoutExtension(p)).Append(". Version: </Text>\r\n      <FilePath>").Append(p).Append("</FilePath>\r\n      <Sha>").Append(s).Append("</Sha>\r\n    </DllFile>\r\n");
        }
        sb.Append("  </MandatoryPlugins>\r\n  <OptionalPlugins />\r\n  <ForbiddenPlugins />\r\n");
        sb.Append("  <AllowedParts>\r\n    <string>somePart</string>\r\n  </AllowedParts>\r\n  <AllowedResources>\r\n    <string>LiquidFuel</string>\r\n  </AllowedResources>\r\n</ModControlStructure>");
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
