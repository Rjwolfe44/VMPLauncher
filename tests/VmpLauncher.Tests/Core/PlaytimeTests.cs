using System.Globalization;
using System.IO;
using Microsoft.Extensions.Time.Testing;
using VmpLauncher.App.Services;
using VmpLauncher.Core;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class PlaytimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vmpl-playtime-" + Guid.NewGuid().ToString("N"));

    public PlaytimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Vdf(string playtime, string lastPlayed = "1755482409") => $$"""
        "UserLocalConfigStore"
        {
            "Software" { "Valve" { "Steam" { "apps" {
                "400" { "Playtime" "999" }
                "220200" { "LastPlayed" "{{lastPlayed}}" "Playtime" "{{playtime}}" "Note" "a \"quoted\" \\ value" }
            } } } }
            "friends" { "x" "y" }
        }
        """;

    [Fact]
    public void Steam_vdf_reads_only_the_ksp_numbers_through_nesting_and_escapes()
    {
        Assert.Equal((2606L, 1755482409L), PlaytimeService.ParseSteam(Vdf("2606")));
    }

    [Fact]
    public void Steam_vdf_keys_are_case_insensitive_and_missing_section_is_null()
    {
        Assert.NotNull(PlaytimeService.ParseSteam(Vdf("5").Replace("\"Playtime\"", "\"playtime\"").Replace("\"apps\"", "\"APPS\"")));
        Assert.Null(PlaytimeService.ParseSteam("\"UserLocalConfigStore\" { \"Software\" { } }"));
        Assert.Null(PlaytimeService.ParseSteam("not vdf at all {{{"));
    }

    private sealed class FakeEnv(Dictionary<string, string> files, params string[] dirs) : IPlaytimeEnvironment
    {
        public string? Registry { get; init; }
        public string? SteamPathFromRegistry() => Registry;
        public string LocalAppData => @"C:\fake\local";
        public string? ReadText(string path) => files.TryGetValue(Path.GetFullPath(path), out var t) ? t : null;
        public IEnumerable<string> SubDirectories(string path)
            => dirs.Where(d => string.Equals(Path.GetDirectoryName(d), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Two_steam_accounts_are_summed_and_ckan_instances_are_distinct()
    {
        var steam = @"C:\fake\steam";
        var files = new Dictionary<string, string>
        {
            [Path.GetFullPath($@"{steam}\userdata\1\config\localconfig.vdf")] = Vdf("60", "1000"),
            [Path.GetFullPath($@"{steam}\userdata\2\config\localconfig.vdf")] = Vdf("90", "2000"),
            [Path.GetFullPath(@"C:\fake\local\CKAN\config.json")] =
                """{"KnownInstances":{"a":"C:\\ksp1","b":"C:\\KSP1","c":"C:\\ksp2"}}""",
            [Path.GetFullPath(@"C:\ksp1\CKAN\playtime.json")] = """{"time":"1.02:00:00"}""",
            [Path.GetFullPath(@"C:\ksp2\CKAN\playtime.json")] = """{"time":"00:30:00.5"}""",
        };
        var env = new FakeEnv(files, $@"{steam}\userdata\1", $@"{steam}\userdata\2") { Registry = steam.Replace('\\', '/') };
        var counter = new PlaytimeCounter(new LauncherPaths(_dir));
        var snap = new PlaytimeService(counter, env).Read();
        Assert.Equal(TimeSpan.FromMinutes(150), snap.Steam);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000).UtcDateTime, snap.SteamLastPlayedUtc);
        Assert.Equal(TimeSpan.FromHours(26.5) + TimeSpan.FromMilliseconds(500), snap.Ckan);
        Assert.Equal(snap.Steam + snap.Ckan, snap.Total);
    }

    [Fact]
    public void Missing_sources_are_not_found_not_errors()
    {
        var snap = new PlaytimeService(new PlaytimeCounter(new LauncherPaths(_dir)), new FakeEnv([])).Read();
        Assert.Null(snap.Steam);
        Assert.Null(snap.Ckan);
        Assert.Equal(TimeSpan.Zero, snap.Total);
    }

    [Theory]
    [InlineData("""{"time":"23:18:21.8062127"}""", 23 * 3600 + 18 * 60 + 21)]
    [InlineData("""{"time":"2.03:00:00"}""", 51 * 3600)]
    public void Ckan_timespan_parses_with_and_without_days(string json, int seconds)
        => Assert.Equal(seconds, (int)PlaytimeService.ParseCkanPlaytime(json)!.Value.TotalSeconds);

    [Theory]
    [InlineData("""{"time":"banana"}""")]
    [InlineData("""{"time":"-01:00:00"}""")]
    [InlineData("""{"other":1}""")]
    [InlineData("{broken")]
    public void Ckan_garbage_is_null(string json) => Assert.Null(PlaytimeService.ParseCkanPlaytime(json));

    [Fact]
    public void Vmp_counter_accumulates_per_profile_and_total_and_survives_a_crash()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        var paths = new LauncherPaths(_dir);
        var counter = new PlaytimeCounter(paths, clock);

        var a = counter.Begin("p1");
        clock.Advance(TimeSpan.FromSeconds(60));
        a.Flush();
        clock.Advance(TimeSpan.FromSeconds(60));
        a.Flush();
        // Second session on another profile ends normally.
        var b = counter.Begin("p2");
        clock.Advance(TimeSpan.FromSeconds(30));
        b.Flush();
        // Launcher "dies" here: no final flush for a, and a fresh instance reads the file.
        var reloaded = new PlaytimeCounter(paths, clock);
        Assert.Equal(120, reloaded.Profile("p1").Seconds);
        Assert.Equal(30, reloaded.Profile("p2").Seconds);
        Assert.Equal(150, reloaded.Total.Seconds);
        Assert.Equal(2, reloaded.Total.Sessions);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, reloaded.Total.LastPlayedUtc);
    }

    [Fact]
    public void Vmp_counter_clamps_sleep_clock_jumps_and_corrupt_files()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        var counter = new PlaytimeCounter(new LauncherPaths(_dir), clock);
        var s = counter.Begin("p");
        clock.Advance(TimeSpan.FromHours(9)); // laptop slept
        s.Flush();
        Assert.Equal((long)PlaytimeCounter.MaxStep.TotalSeconds, counter.Total.Seconds);

        File.WriteAllText(Path.Combine(_dir, "playtime.json"),
            """{"total":{"seconds":-50,"sessions":-2},"profiles":{"p":{"seconds":99999999999999,"sessions":1}}}""");
        var loaded = new PlaytimeCounter(new LauncherPaths(_dir), clock);
        Assert.Equal(0, loaded.Total.Seconds);
        Assert.Equal(0, loaded.Total.Sessions);
        Assert.Equal(PlaytimeCounter.MaxSeconds, loaded.Profile("p").Seconds);

        File.WriteAllText(Path.Combine(_dir, "playtime.json"), "{ not json");
        Assert.Equal(0, new PlaytimeCounter(new LauncherPaths(_dir), clock).Total.Seconds);
    }

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(59, "59 min")]
    [InlineData(60, "1 h 0 min")]
    [InlineData(123 * 60 + 45, "123 h 45 min")]
    [InlineData(-5, "0 min")]
    public void Duration_formats_as_hours_and_minutes(int minutes, string expected)
        => Assert.Equal(expected, PlaytimeService.Format(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Display_time_is_local_twelve_hour_with_am_pm()
    {
        var utc = new DateTime(2026, 11, 7, 18, 5, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        var expectedTime = $"{(local.Hour % 12 == 0 ? 12 : local.Hour % 12)}:05 {(local.Hour < 12 ? "AM" : "PM")}";
        Assert.Equal(expectedTime, DisplayTime.Time(utc));
        Assert.Equal(local.ToString("d", CultureInfo.InvariantCulture) + ", " + expectedTime, DisplayTime.DateTimeText(utc, CultureInfo.InvariantCulture));
        // Midnight and noon read 12, never 0.
        Assert.Equal("12:00 AM", DisplayTime.Time(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
        Assert.Equal("12:30 PM", DisplayTime.Time(new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Local)));
        Assert.Equal("1:41 AM", DisplayTime.Time(new DateTime(2026, 11, 7, 1, 41, 0, DateTimeKind.Local)));
    }
}
