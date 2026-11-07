using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Profiles;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class TextureQualityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vmpl-texture-" + Guid.NewGuid().ToString("N"));
    private string Cfg => Path.Combine(_dir, "settings.cfg");
    private string Backup => Cfg + ".vmpl-backup";

    public TextureQualityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Shaped like KSP's own file: a comment, flat settings, then sections (one has a nested TEXTURE_QUALITY that must never be touched).</summary>
    private static string Settings(string qualityLine, string eol = "\r\n", bool bom = false, bool withKey = true)
    {
        var lines = new List<string>
        {
            "// KSP Game Settings", "", "SETTINGS_FILE_VERSION = 1.3.0", "LANGUAGE = en-us", "FULLSCREEN = True",
            "QUALITY_PRESET = 4", "ANTI_ALIASING = 4",
        };
        if (withKey)
        {
            lines.Add(qualityLine);
        }
        lines.AddRange(new[]
        {
            "SYNC_VBL = 1", "FRAMERATE_LIMIT = 180", "MISSION_VALIDATOR_MODE = Manual",
            "INPUT_DEVICES", "{", "}", "PITCH_DOWN", "{", "\tprimary = W", "\tsecondary = None", "\tTEXTURE_QUALITY = 3", "}", "",
        });
        return (bom ? "ï»¿" : string.Empty) + string.Join(eol, lines);
    }

    // Bytes map 1:1 to chars (Latin-1), so string equality here is byte equality.
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);
    private void Write(string text) => File.WriteAllBytes(Cfg, Bytes(text));
    private string Read() => Encoding.Latin1.GetString(File.ReadAllBytes(Cfg));

    private static TextureQualityOutcome Run(string dir, TextureQualityPolicy policy, bool autoDone = false, Func<string, bool>? running = null)
        => TextureQualitySetting.Apply(dir, policy, autoDone, running ?? (_ => false));

    [Theory]
    [InlineData("TEXTURE_QUALITY = 0", "TEXTURE_QUALITY = 1", "\r\n", false)]
    [InlineData("TEXTURE_QUALITY = 0", "TEXTURE_QUALITY = 1", "\n", false)]
    [InlineData("TEXTURE_QUALITY = 0", "TEXTURE_QUALITY = 1", "\r\n", true)]
    [InlineData("TEXTURE_QUALITY=0", "TEXTURE_QUALITY=1", "\r\n", false)]
    [InlineData("  TEXTURE_QUALITY   =   0   // full", "  TEXTURE_QUALITY   =   1   // full", "\r\n", false)]
    [InlineData("TEXTURE_QUALITY = 0 ", "TEXTURE_QUALITY = 1 ", "\r\n", false)]
    public void Auto_edits_only_the_one_line_and_keeps_every_other_byte(string before, string after, string eol, bool bom)
    {
        Write(Settings(before, eol, bom));
        var outcome = Run(_dir, TextureQualityPolicy.Auto);
        Assert.Equal(TextureQualityStatus.Changed, outcome.Status);
        Assert.True(outcome.RecordAutoDone);
        Assert.Equal(Settings(after, eol, bom), Read());
    }

    [Fact]
    public void Missing_key_is_added_after_the_last_top_level_setting_and_not_inside_a_section()
    {
        Write(Settings(string.Empty, withKey: false));
        var outcome = Run(_dir, TextureQualityPolicy.Auto);
        Assert.Equal(TextureQualityStatus.Changed, outcome.Status);
        var expected = Settings(string.Empty, withKey: false).Replace("MISSION_VALIDATOR_MODE = Manual\r\n", "MISSION_VALIDATOR_MODE = Manual\r\nTEXTURE_QUALITY = 1\r\n");
        Assert.Equal(expected, Read());
    }

    [Fact]
    public void Key_twice_is_followed_by_an_explicit_policy_but_mixed_values_are_left_by_Auto()
    {
        var twice = Settings("TEXTURE_QUALITY = 0").Replace("SYNC_VBL = 1", "TEXTURE_QUALITY = 1\r\nSYNC_VBL = 1");
        Write(twice);
        Assert.Equal(TextureQualityStatus.Unchanged, Run(_dir, TextureQualityPolicy.Auto).Status);
        Assert.Equal(twice, Read());

        Assert.Equal(TextureQualityStatus.Changed, Run(_dir, TextureQualityPolicy.Quarter).Status);
        Assert.Equal(twice.Replace("TEXTURE_QUALITY = 0", "TEXTURE_QUALITY = 2").Replace("TEXTURE_QUALITY = 1", "TEXTURE_QUALITY = 2"), Read());
    }

    [Fact]
    public void Auto_changes_Full_to_Half_once_and_never_overrides_a_later_choice()
    {
        Write(Settings("TEXTURE_QUALITY = 0"));
        var first = Run(_dir, TextureQualityPolicy.Auto);
        Assert.True(first.Changed && first.RecordAutoDone);
        Assert.Equal(0, first.Before);
        Assert.Equal(1, first.After);

        Write(Settings("TEXTURE_QUALITY = 0")); // the player picked Full again in KSP
        var second = Run(_dir, TextureQualityPolicy.Auto, autoDone: true);
        Assert.Equal(TextureQualityStatus.Left, second.Status);
        Assert.Equal(Settings("TEXTURE_QUALITY = 0"), Read());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Auto_never_lowers_an_already_lowered_value_but_still_counts_as_done(int value)
    {
        var text = Settings($"TEXTURE_QUALITY = {value}");
        Write(text);
        var outcome = Run(_dir, TextureQualityPolicy.Auto);
        Assert.Equal(TextureQualityStatus.Unchanged, outcome.Status);
        Assert.True(outcome.RecordAutoDone);
        Assert.Equal(text, Read());
        Assert.False(File.Exists(Backup));
    }

    [Theory]
    [InlineData(TextureQualityPolicy.Full, 0, 1)]
    [InlineData(TextureQualityPolicy.Half, 1, 0)]
    [InlineData(TextureQualityPolicy.Half, 1, 3)]
    [InlineData(TextureQualityPolicy.Quarter, 2, 1)]
    [InlineData(TextureQualityPolicy.Full, 0, 2)]
    public void Explicit_policies_set_that_exact_value_even_after_Auto_ran(TextureQualityPolicy policy, int wanted, int start)
    {
        Write(Settings($"TEXTURE_QUALITY = {start}"));
        var outcome = Run(_dir, policy, autoDone: true);
        Assert.Equal(TextureQualityStatus.Changed, outcome.Status);
        Assert.False(outcome.RecordAutoDone);
        Assert.Equal(Settings($"TEXTURE_QUALITY = {wanted}"), Read());
        Assert.Equal(TextureQualityStatus.Unchanged, Run(_dir, policy, autoDone: true).Status); // no rewrite when already right
    }

    [Fact]
    public void Leave_touches_nothing()
    {
        var text = Settings("TEXTURE_QUALITY = 0");
        Write(text);
        var stamp = File.GetLastWriteTimeUtc(Cfg);
        Assert.Equal(TextureQualityStatus.Left, Run(_dir, TextureQualityPolicy.Leave).Status);
        Assert.Equal(text, Read());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Cfg));
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void Backup_is_made_once_and_keeps_the_original()
    {
        var original = Settings("TEXTURE_QUALITY = 0");
        Write(original);
        Run(_dir, TextureQualityPolicy.Half);
        Run(_dir, TextureQualityPolicy.Quarter);
        Assert.Equal(Settings("TEXTURE_QUALITY = 2"), Read());
        Assert.Equal(original, Encoding.Latin1.GetString(File.ReadAllBytes(Backup)));
    }

    [Fact]
    public void Read_only_file_is_left_alone_and_Auto_is_not_marked_done()
    {
        var text = Settings("TEXTURE_QUALITY = 0");
        Write(text);
        File.SetAttributes(Cfg, FileAttributes.ReadOnly);
        try
        {
            var outcome = Run(_dir, TextureQualityPolicy.Auto);
            Assert.Equal(TextureQualityStatus.ReadOnly, outcome.Status);
            Assert.False(outcome.RecordAutoDone);
            Assert.Equal(text, Read());
        }
        finally
        {
            File.SetAttributes(Cfg, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Refuses_while_KSP_runs_from_the_folder()
    {
        var text = Settings("TEXTURE_QUALITY = 0");
        Write(text);
        var outcome = Run(_dir, TextureQualityPolicy.Half, running: d => d == _dir);
        Assert.Equal(TextureQualityStatus.KspRunning, outcome.Status);
        Assert.False(outcome.RecordAutoDone);
        Assert.Equal(text, Read());
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void A_failed_write_leaves_the_original_intact_and_the_next_launch_can_retry()
    {
        var text = Settings("TEXTURE_QUALITY = 0");
        Write(text);
        var temp = Cfg + TextureQualitySetting.TempSuffix;
        Directory.CreateDirectory(temp); // the temp file cannot be created here
        var outcome = Run(_dir, TextureQualityPolicy.Auto);
        Assert.Equal(TextureQualityStatus.Failed, outcome.Status);
        Assert.False(outcome.RecordAutoDone);
        Assert.Equal(text, Read());
        Directory.Delete(temp);
        Assert.Equal(TextureQualityStatus.Changed, Run(_dir, TextureQualityPolicy.Auto).Status);
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void A_locked_file_does_not_throw()
    {
        Write(Settings("TEXTURE_QUALITY = 0"));
        using var hold = new FileStream(Cfg, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var outcome = Run(_dir, TextureQualityPolicy.Auto);
        Assert.Equal(TextureQualityStatus.Failed, outcome.Status);
        Assert.False(outcome.RecordAutoDone);
    }

    [Fact]
    public void No_file_is_not_created_and_odd_files_are_left_alone()
    {
        Assert.Equal(TextureQualityStatus.NoFile, Run(_dir, TextureQualityPolicy.Auto).Status);
        Assert.False(File.Exists(Cfg));

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Settings("TEXTURE_QUALITY = 0"))).ToArray();
        File.WriteAllBytes(Cfg, utf16);
        Assert.Equal(TextureQualityStatus.Unsupported, Run(_dir, TextureQualityPolicy.Half).Status);
        Assert.Equal(utf16, File.ReadAllBytes(Cfg));

        var odd = Settings("TEXTURE_QUALITY = True");
        Write(odd);
        Assert.Equal(TextureQualityStatus.Unsupported, Run(_dir, TextureQualityPolicy.Half).Status);
        Assert.Equal(odd, Read());
    }

    [Fact]
    public void Service_applies_Auto_once_per_profile_raises_the_notice_and_a_fresh_seed_starts_over()
    {
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        var store = new AppSettingsStore(new VmpLauncher.Core.LauncherPaths(data));
        var service = new TextureQualityService(store, NullLogger<TextureQualityService>.Instance);
        var game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        var cfg = Path.Combine(game, "settings.cfg");
        File.WriteAllBytes(cfg, Bytes(Settings("TEXTURE_QUALITY = 0")));

        Assert.True(service.Apply("pack", game).Changed);
        Assert.True(store.Current.TextureQualityNotice);
        Assert.Contains("pack", store.Current.TextureQualityAutoDone);

        File.WriteAllBytes(cfg, Bytes(Settings("TEXTURE_QUALITY = 0"))); // the player chose Full in KSP
        Assert.False(service.Apply("pack", game).Changed);
        Assert.Equal(Settings("TEXTURE_QUALITY = 0"), Encoding.Latin1.GetString(File.ReadAllBytes(cfg)));

        Assert.True(service.Apply("pack", game, freshlySeeded: true).Changed); // a recreated profile starts over

        store.Update(s => s with { TextureQualityPolicy = "leave", TextureQualityNotice = false });
        File.WriteAllBytes(cfg, Bytes(Settings("TEXTURE_QUALITY = 0")));
        Assert.Equal(TextureQualityStatus.Left, service.Apply("other", game).Status);
        store.Update(s => s with { TextureQualityPolicy = "quarter" });
        Assert.True(service.Apply("other", game).Changed);
        Assert.False(store.Current.TextureQualityNotice); // explicit choices never raise the Auto notice
    }
}
