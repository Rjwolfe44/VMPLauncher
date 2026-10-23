using System.IO;
using System.Text.Json;
using VmpLauncher.Core;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class DataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-migrate-" + Guid.NewGuid().ToString("N"));
    private readonly string _legacy;
    private readonly string _target;

    public DataMigrationTests()
    {
        _legacy = Path.Combine(_root, "VmpLauncher");
        _target = Path.Combine(_root, "VmpLauncherData");
        Directory.CreateDirectory(_legacy);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(_legacy, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private void SeedOldLayout()
    {
        // Velopack's own files share the folder and must stay.
        Write(@"current\VmpLauncher.exe", "exe");
        Write("Update.exe", "update");
        Write(@"cache\ABCD1234-Mod-1.0.zip", "zip");
        Write(@"cache\vmpl-verified.json", JsonSerializer.Serialize(new { files = new Dictionary<string, object> { [Path.Combine(_legacy, "cache", "ABCD1234-Mod-1.0.zip")] = new { size = 3, sha256 = "x" } } }));
        Write(@"ckan\config.json", JsonSerializer.Serialize(new { DownloadCacheDir = Path.Combine(_legacy, "cache"), GameInstances = Array.Empty<object>() }));
        Write(@"profiles\vmp\game\GameData\keep.cfg", Path.Combine(_legacy, "untouched"));
        Write("settings.json", JsonSerializer.Serialize(new { localPackDir = Path.Combine(_root, "VmpLauncherOther", "x"), playerName = "Vlad" }));
        Write("secrets.bin", "dpapi");
    }

    [Fact]
    public void Moves_data_out_and_rewrites_saved_paths()
    {
        SeedOldLayout();

        var outcome = DataMigration.Run(_legacy, _target);

        Assert.False(outcome.UseLegacy);
        Assert.True(File.Exists(Path.Combine(_target, "cache", "ABCD1234-Mod-1.0.zip")));
        Assert.True(File.Exists(Path.Combine(_target, "secrets.bin")));
        Assert.True(File.Exists(Path.Combine(_target, "profiles", "vmp", "game", "GameData", "keep.cfg")));
        Assert.False(Directory.Exists(Path.Combine(_legacy, "cache")));
        Assert.True(File.Exists(Path.Combine(_legacy, "current", "VmpLauncher.exe")));
        Assert.True(File.Exists(Path.Combine(_legacy, "Update.exe")));

        var config = File.ReadAllText(Path.Combine(_target, "ckan", "config.json"));
        Assert.Equal(Path.Combine(_target, "cache"), JsonDocument.Parse(config).RootElement.GetProperty("DownloadCacheDir").GetString());
        var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(_target, "cache", "vmpl-verified.json"))).RootElement.GetProperty("files");
        Assert.Equal(Path.Combine(_target, "cache", "ABCD1234-Mod-1.0.zip"), index.EnumerateObject().Single().Name);

        // Only paths inside the old root change: a sibling folder that merely starts with the same name stays.
        var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(_target, "settings.json"))).RootElement;
        Assert.Equal(Path.Combine(_root, "VmpLauncherOther", "x"), settings.GetProperty("localPackDir").GetString());
        // Game files are never rewritten.
        Assert.Equal(Path.Combine(_legacy, "untouched"), File.ReadAllText(Path.Combine(_target, "profiles", "vmp", "game", "GameData", "keep.cfg")));

        // Running again is a no-op.
        var again = DataMigration.Run(_legacy, _target);
        Assert.False(again.UseLegacy);
        Assert.Empty(again.Notes);
    }

    [Fact]
    public void Leaves_both_alone_when_the_new_folder_already_has_data()
    {
        SeedOldLayout();
        Directory.CreateDirectory(Path.Combine(_target, "profiles", "other"));

        var outcome = DataMigration.Run(_legacy, _target);

        Assert.False(outcome.UseLegacy);
        Assert.Contains(outcome.Notes, n => n.Contains("both", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_legacy, "cache", "ABCD1234-Mod-1.0.zip")));
        Assert.False(Directory.Exists(Path.Combine(_target, "cache")));
    }

    [Fact]
    public void Empty_folders_in_the_new_location_do_not_block_the_move()
    {
        SeedOldLayout();
        Directory.CreateDirectory(Path.Combine(_target, "cache"));
        Directory.CreateDirectory(Path.Combine(_target, "logs"));

        var outcome = DataMigration.Run(_legacy, _target);

        Assert.False(outcome.UseLegacy);
        Assert.True(File.Exists(Path.Combine(_target, "cache", "ABCD1234-Mod-1.0.zip")));
    }

    [Fact]
    public void Rolls_back_and_keeps_the_old_folder_when_a_file_is_in_use()
    {
        SeedOldLayout();
        Write(@"logs\launcher-20261023.log", "open");
        using (new FileStream(Path.Combine(_legacy, "logs", "launcher-20261023.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var outcome = DataMigration.Run(_legacy, _target);

            Assert.True(outcome.UseLegacy);
            Assert.True(File.Exists(Path.Combine(_legacy, "cache", "ABCD1234-Mod-1.0.zip")));
            Assert.True(File.Exists(Path.Combine(_legacy, "ckan", "config.json")));
            Assert.False(Directory.Exists(Path.Combine(_target, "cache")));
        }
    }

    [Fact]
    public void Nothing_to_do_without_old_data()
    {
        Write(@"current\VmpLauncher.exe", "exe");

        var outcome = DataMigration.Run(_legacy, _target);

        Assert.False(outcome.UseLegacy);
        Assert.Empty(outcome.Notes);
        Assert.False(Directory.Exists(_target));
    }
}
