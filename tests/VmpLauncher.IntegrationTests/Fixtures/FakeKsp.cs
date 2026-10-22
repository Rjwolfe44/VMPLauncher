using System.IO;

namespace VmpLauncher.IntegrationTests.Fixtures;

/// <summary>A directory that CKAN and the launcher accept as KSP 1.12.5.3190 with both DLCs.</summary>
public static class FakeKsp
{
    public const string SentinelRelativePath = @"saves\default\persistent.sfs";
    public const string SentinelContent = "GAME { version = 1.12.5 } // sentinel: must never change";

    public static string Create(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "KSP_x64.exe"), string.Empty);
        File.WriteAllText(Path.Combine(dir, "buildID64.txt"), "build id = 03190\n2022.12.12 at 14:02:28 PST\nBranch: master\nlanguage: en-us\ndistribution name: Steam\n");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "Kerbal Space Program\n\nVersion 1.12.5\n");
        File.WriteAllText(Path.Combine(dir, "settings.cfg"), "SETTINGS { }");
        Directory.CreateDirectory(Path.Combine(dir, "KSP_x64_Data"));
        File.WriteAllText(Path.Combine(dir, "KSP_x64_Data", "globalgamemanagers"), "fake");
        Directory.CreateDirectory(Path.Combine(dir, "GameData", "Squad", "Parts"));
        File.WriteAllText(Path.Combine(dir, "GameData", "Squad", "Parts", "stock.cfg"), "PART { name = stock }");
        Directory.CreateDirectory(Path.Combine(dir, "GameData", "SquadExpansion", "MakingHistory"));
        File.WriteAllText(Path.Combine(dir, "GameData", "SquadExpansion", "MakingHistory", "readme.txt"), "Making History Expansion\nVersion 1.12.1\n");
        Directory.CreateDirectory(Path.Combine(dir, "GameData", "SquadExpansion", "Serenity"));
        File.WriteAllText(Path.Combine(dir, "GameData", "SquadExpansion", "Serenity", "readme.txt"), "Breaking Ground Expansion\nVersion 1.7.1\n");
        Directory.CreateDirectory(Path.Combine(dir, "Ships", "VAB"));
        File.WriteAllText(Path.Combine(dir, "Ships", "VAB", "Kerbal X.craft"), "ship = Kerbal X");
        var sentinel = Path.Combine(dir, SentinelRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, SentinelContent);
        return dir;
    }
}
