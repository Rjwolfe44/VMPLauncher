using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using VmpLauncher.Core.Diagnostics;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class LogBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-logbundle-" + Guid.NewGuid().ToString("N")[..8]);

    public LogBundleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Write(string rel, string content)
    {
        var path = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ReadEntry(string zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"{name} is not in the zip: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string[] EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    // ---- redaction ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Users\aboykin\AppData\Local\x.cfg", @"C:\Users\<user>\AppData\Local\x.cfg")]
    [InlineData("C:/Users/aboykin/AppData/Local/x.cfg", "C:/Users/<user>/AppData/Local/x.cfg")]
    [InlineData(@"c:\users\ABOYKIN\x", @"c:\users\<user>\x")]
    [InlineData(@"C:\USERS\AboyKin", @"C:\USERS\<user>")]
    [InlineData(@"{""path"":""C:\\Users\\aboykin\\AppData\\x""}", @"{""path"":""C:\\Users\\<user>\\AppData\\x""}")]
    [InlineData("file:///C:/Users/aboykin/Desktop/a.png", "file:///C:/Users/<user>/Desktop/a.png")]
    [InlineData(@"Cannot create config from file 'D:\Users\somebody\x.cfg'.", @"Cannot create config from file 'D:\Users\<user>\x.cfg'.")]
    [InlineData(@"two: C:\Users\aboykin\a and C:/Users/Aboykin/b", @"two: C:\Users\<user>\a and C:/Users/<user>/b")]
    public void User_directory_is_hidden_in_every_path_style(string input, string expected)
        => Assert.Equal(expected, LogRedactor.Redact(input));

    [Fact]
    public void User_directory_rule_works_without_knowing_the_name_and_leaves_other_paths_alone()
    {
        const string Line = @"[LOG 01:18:27.170] Load C:\VmpTest\train-full\game\GameData\Users\readme.txt and C:\Program Files\Steam";
        Assert.Same(Line, LogRedactor.Redact(Line)); // nothing to change: the very same string comes back
        Assert.Equal(string.Empty, LogRedactor.Redact(string.Empty));
        Assert.Equal(string.Empty, LogRedactor.Redact(null));
    }

    [Fact]
    public void Current_user_name_is_hidden_as_a_whole_word_but_never_inside_other_words()
    {
        var redactor = new LogRedactor(["bob"]);
        Assert.Equal("Hello <user>, Bobsled, bobcat, rebob, bob2, <user>.", redactor.Redact("Hello bob, Bobsled, bobcat, rebob, bob2, BOB."));
        Assert.Equal(@"C:\Users\<user>\Desktop and <user>-PC", redactor.Redact(@"C:\Users\Bob\Desktop and bob-PC"));
        Assert.Equal(4, redactor.UserNameReplacements);
    }

    [Fact]
    public void User_names_with_spaces_and_odd_forms_are_covered()
    {
        var redactor = new LogRedactor(["John Smith", "jsmith"]);
        Assert.Equal(@"C:\Users\<user>\AppData\x", redactor.Redact(@"C:\Users\John Smith\AppData\x"));
        Assert.Equal("user <user> logged in", redactor.Redact("user jsmith logged in"));
        Assert.Equal(@"{""p"":""C:\\Users\\<user>\\x""}", redactor.Redact(@"{""p"":""C:\\Users\\John Smith\\x""}"));
    }

    [Fact]
    public void Very_short_names_are_not_replaced_as_words_but_their_paths_still_are()
    {
        var redactor = new LogRedactor(["al"]);
        Assert.Equal(@"Al and al went to C:\Users\<user>\x", redactor.Redact(@"Al and al went to C:\Users\al\x"));
    }

    [Fact]
    public void Redaction_is_idempotent_even_for_a_user_called_user()
    {
        var redactor = new LogRedactor(["user", "bob"]);
        var once = redactor.Redact(@"C:\Users\bob\x user bob");
        Assert.Equal(@"C:\Users\<user>\x <user> <user>", once);
        Assert.Equal(once, redactor.Redact(once));
        Assert.Equal(once, LogRedactor.Redact(once));
    }

    [Fact]
    public void Secrets_and_token_shapes_are_removed()
    {
        var redactor = new LogRedactor(secrets: ["hunter2-pass", "ab"]);
        var line = "password=hunter2-pass; invite VMPINVITE1-AAAAAAAAAAAAAAAAAAAA; token github_pat_11ABCDEFG0123456789_abcdefghijklmnop; ghp_0123456789abcdefghijABCDEFGHIJ0123 ab";
        var result = redactor.Redact(line);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("VMPINVITE1-AAAA", result, StringComparison.Ordinal);
        Assert.DoesNotContain("github_pat_", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", result, StringComparison.Ordinal);
        Assert.EndsWith(" ab", result, StringComparison.Ordinal); // a 2 character "secret" is too short to be safe to replace everywhere
        Assert.Equal(4, redactor.SecretReplacements);
    }

    [Fact]
    public void Bounded_line_reader_handles_line_endings_and_cuts_runaway_lines()
    {
        var text = "one\r\ntwo\n\r\nlast without newline";
        var reader = new BoundedLineReader(new StringReader(text));
        Assert.Equal("one", reader.ReadLine());
        Assert.Equal(LineEnding.CrLf, reader.LastLineEnding);
        Assert.Equal("two", reader.ReadLine());
        Assert.Equal(LineEnding.Lf, reader.LastLineEnding);
        Assert.Equal(string.Empty, reader.ReadLine());
        Assert.Equal(LineEnding.CrLf, reader.LastLineEnding);
        Assert.Equal("last without newline", reader.ReadLine());
        Assert.Equal(LineEnding.None, reader.LastLineEnding);
        Assert.Null(reader.ReadLine());

        var big = new string('x', 100_000) + "\r\nnext\n";
        var cutting = new BoundedLineReader(new StringReader(big), maxLineChars: 1000);
        var line = cutting.ReadLine()!;
        Assert.True(cutting.LastLineCut);
        Assert.Equal(LineEnding.CrLf, cutting.LastLineEnding);
        Assert.Equal(1000 + BoundedLineReader.CutMarker.Length, line.Length);
        Assert.Equal("next", cutting.ReadLine());
        Assert.False(cutting.LastLineCut);
        Assert.Equal(LineEnding.Lf, cutting.LastLineEnding);
        Assert.Null(cutting.ReadLine());
    }

    [Fact]
    public void Lines_are_copied_with_the_line_endings_they_had()
    {
        // KSP.log mixes CRLF lines with lone LF lines inside multi-line messages; the copy must not turn one into the other.
        const string Original = "[LOG 00:00:01.000] first\r\nsecond line of the same message\n\r\n[LOG 00:00:02.000] last line has no line break";
        var game = Write("pack/game/KSP.log", Original);

        var result = LogBundle.Build(Options() with { GameLogPath = game });

        Assert.Equal(Original, ReadEntry(result.Path, "KSP.log"));
    }

    [Fact]
    public void Explorer_is_asked_to_select_the_zip_with_the_path_quoted()
        => Assert.Equal("/select,\"C:\\Users\\Some One\\Desktop\\VMP-logs-Vlad,Test.zip\"", VmpLauncher.App.Services.RecoveryActions.ExplorerSelectArguments(@"C:\Users\Some One\Desktop\VMP-logs-Vlad,Test.zip"));

    // ---- incident extract --------------------------------------------------------------------------------------

    private static string[] Lines(string text) => text.Split(["\r\n", "\n"], StringSplitOptions.None);

    [Fact]
    public void Extract_collapses_repeats_and_keeps_context_before_an_explosion()
    {
        var log = new List<string>();
        for (var i = 1; i <= 8; i++)
        {
            log.Add($"[LOG 01:42:30.{i:000}] ordinary line number {i}");
        }
        log.Add("[LOG 01:42:36.250] RCSBlock.v2 Exploded!! - blast awesomeness: 0.5");
        log.Add("[ERR 01:42:36.251] [VMP]: Error destroying vessel: System.NullReferenceException: Object reference not set");
        log.Add("  at LmpClient.Systems.VesselRemoveSys.KillVessel (Vessel v) [0x00073]");
        log.Add(string.Empty);
        log.Add("[LOG 01:42:36.252] noise between the explosions");
        log.Add("[LOG 01:42:36.480] RCSBlock.v2 Exploded!! - blast awesomeness: 0.5");
        log.Add("[ERR 01:42:36.481] [VMP]: Error destroying vessel: System.NullReferenceException: Object reference not set");
        log.Add("  at LmpClient.Systems.VesselRemoveSys.KillVessel (Vessel v) [0x00073]");
        log.Add(string.Empty);
        log.Add("[LOG 01:42:37.000] RCSBlock.v2 Exploded!! - blast awesomeness: 0.5");
        log.Add("[LOG 01:42:50.000] a different explosion: solarPanels5 Exploded!! - blast awesomeness: 0.5");

        var lines = Lines(IncidentExtractor.Build(log));

        // Five lines of context before the first explosion, with their own timestamps, indented; older lines are left out.
        Assert.Contains("[LOG 01:42:36.250] RCSBlock.v2 Exploded!! - blast awesomeness: 0.5", lines);
        for (var i = 4; i <= 8; i++)
        {
            Assert.Contains($"    [LOG 01:42:30.{i:000}] ordinary line number {i}", lines);
        }
        Assert.DoesNotContain(lines, l => l.Contains("ordinary line number 3", StringComparison.Ordinal));
        // The same explosion again, then once more right after (the repeated error between them is not a kept line): one line with a count,
        // keeping the first copy's timestamp and the lines that led up to it.
        Assert.Contains("[LOG 01:42:36.480] RCSBlock.v2 Exploded!! - blast awesomeness: 0.5 (x 2)", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("[LOG 01:42:37.000]", StringComparison.Ordinal));
        Assert.Contains("    [LOG 01:42:36.252] noise between the explosions", lines);
        // The error is kept once, with the top of its stack, and counted in the summary.
        Assert.Single(lines, l => l.StartsWith("[ERR 01:42:36.251]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("[ERR 01:42:36.481]", StringComparison.Ordinal));
        Assert.Contains("  at LmpClient.Systems.VesselRemoveSys.KillVessel (Vessel v) [0x00073]", lines);
        Assert.Contains(lines, l => l.StartsWith("x2  01:42:36.251 to 01:42:36.481  [ERR] [VMP]: Error destroying vessel", StringComparison.Ordinal));
        // A different explosion is its own entry.
        Assert.Contains(lines, l => l.StartsWith("[LOG 01:42:50.000] a different explosion", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_keeps_the_instant_crash_sequence_and_treats_frame_counters_as_the_same_message()
    {
        var log = new[]
        {
            "[LOG 02:19:19.384] [ApexGuidance][NODE_STATUS] (UT=3020991.76s) State=WARPALIGN NodeDV=933.39m/s",
            "[LOG 02:19:20.330] [VMP]: Locked to subspace -1, time: 192803.3833701",
            "[LOG 02:19:20.359] Packing mun lander for orbit",
            "[WRN 02:19:20.378] [F: 267821]: Vessel mun lander crashed through terrain on Kerbin.",
            "[WRN 02:19:20.379] [F: 267822]: Vessel mun lander crashed through terrain on Kerbin.",
            "[LOG 02:19:20.766] Added -15.26858 (-10) reputation: 'VesselLoss'.",
            "[LOG 02:19:21.000] [ApexGuidance][NODE_EXEC] (UT=1s) State IDLE -> WARPALIGN",
            "[LOG 02:25:47.443] [VMP]: Locked to subspace -1, time: 193190.4993612",
            "[LOG 02:25:47.470] Packing mun lander for orbit",
            "[WRN 02:25:47.488] [F: 280027]: Vessel mun lander crashed through terrain on Kerbin.",
        };

        var lines = Lines(IncidentExtractor.Build(log));

        Assert.Contains("[LOG 02:19:20.330] [VMP]: Locked to subspace -1, time: 192803.3833701", lines);
        Assert.Contains("[LOG 02:19:20.359] Packing mun lander for orbit", lines);
        Assert.Contains("[WRN 02:19:20.378] [F: 267821]: Vessel mun lander crashed through terrain on Kerbin. (x 2)", lines);
        Assert.Contains("[LOG 02:19:20.766] Added -15.26858 (-10) reputation: 'VesselLoss'.", lines);
        Assert.Contains(lines, l => l.Contains("[ApexGuidance][NODE_EXEC]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("NODE_STATUS", StringComparison.Ordinal));
        // The second crash is a new incident: it is not swallowed as a repeat of the first one.
        var second = Array.FindIndex(lines, l => l.StartsWith("[LOG 02:25:47.443]", StringComparison.Ordinal));
        Assert.True(second > 0);
        Assert.StartsWith("[LOG 02:25:47.470] Packing mun lander for orbit", lines[second + 1], StringComparison.Ordinal);
        Assert.StartsWith("[WRN 02:25:47.488]", lines[second + 2], StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_shows_each_warning_once_counts_the_rest_and_skips_game_noise_and_asset_names()
    {
        var log = new[]
        {
            "[LOG 00:00:01.000] Load Audio: Squad/Sounds/ksp1_strunts_disconnect_v3_pitched2",
            "[LOG 00:00:02.000] Cannot assign AudioClip 'x_disconnect.wav' to AudioFX",
            "[LOG 00:00:03.000] [VMP]: Disconnected, reason: Connection lost",
            "[WRN 00:00:04.000] Config in file 'a.cfg' contains no data.",
            "[LOG 00:00:05.000] just a log line that does not matter",
            "[WRN 00:00:06.000] Config in file 'a.cfg' contains no data.",
            "[WRN 00:00:07.000] Config in file 'b.cfg' contains no data.",
            "[WRN 00:00:08.000] Config in file 'a.cfg' contains no data.",
            "[EXC 00:00:09.000] NullReferenceException: Object reference not set",
            "  at Foo.Bar () [0x00001]",
            "  at Foo.Baz () [0x00002]",
        };

        var text = IncidentExtractor.Build(log);
        var lines = Lines(text);

        Assert.Contains("[LOG 00:00:03.000] [VMP]: Disconnected, reason: Connection lost", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Load Audio", StringComparison.Ordinal) || l.Contains("AudioClip", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("does not matter", StringComparison.Ordinal));
        Assert.Single(lines, l => l.StartsWith("[WRN 00:00:04.000]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("[WRN 00:00:06.000]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[WRN 00:00:07.000]", StringComparison.Ordinal));
        Assert.Contains("x3  00:00:04.000 to 00:00:08.000  [WRN] Config in file 'a.cfg' contains no data.", lines);
        Assert.Contains("[EXC 00:00:09.000] NullReferenceException: Object reference not set", lines);
        Assert.Contains("  at Foo.Baz () [0x00002]", lines);
        Assert.Contains("In the whole log: 0 errors, 4 warnings, 1 exceptions.", lines);
    }

    [Fact]
    public void Extract_never_grows_past_its_cap_and_keeps_the_newest_lines()
    {
        const int Cap = 20_000;
        var log = Enumerable.Range(0, 5000).Select(i => $"[WRN 01:{i / 60 % 60:00}:{i % 60:00}.000] distinct warning number {i} with some padding to make it longer");

        var text = IncidentExtractor.Build(log, maxBytes: Cap);

        Assert.True(Encoding.UTF8.GetByteCount(text) <= Cap, $"{Encoding.UTF8.GetByteCount(text)} bytes");
        Assert.Contains("distinct warning number 4999", text, StringComparison.Ordinal);
        Assert.DoesNotContain("distinct warning number 3 ", text, StringComparison.Ordinal);
        Assert.Contains("were left out to stay under", text, StringComparison.Ordinal);
    }

    // ---- the bundle --------------------------------------------------------------------------------------------

    private LogBundleOptions Options(string name = "out.zip") => new()
    {
        OutputPath = P(name),
        Now = new DateTimeOffset(2026, 10, 24, 4, 12, 33, TimeSpan.FromHours(-4)),
        UserNames = ["aboykin"],
        Secrets = ["s3cret-pass"],
        Facts = [new("Launcher", "0.4.0"), new("Pack", "2026.10.24.1"), new("VMP client", "0.2.0-train.20261023.3")],
    };

    [Fact]
    public void Bundle_holds_every_part_and_none_of_the_user_name_or_secrets()
    {
        var log = new StringBuilder();
        log.AppendLine("[LOG 01:18:27.170] ******* Log Initiated for Kerbal Space Program *******");
        log.AppendLine(@"[WRN 01:18:29.204] Cannot create config from file 'C:\Users\aboykin\AppData\Local\VmpLauncherData\profiles\x\game\a.cfg'.");
        log.AppendLine(@"[LOG 01:18:30.000] [VMP]: server password is s3cret-pass for C:/Users/Aboykin/x");
        log.AppendLine("[LOG 02:19:20.330] [VMP]: Locked to subspace -1, time: 1");
        log.AppendLine("[LOG 02:19:20.359] Packing mun lander for orbit");
        log.AppendLine("[WRN 02:19:20.378] [F: 267821]: Vessel mun lander crashed through terrain on Kerbin.");
        var game = Write("pack/game/KSP.log", log.ToString());
        var player = Write("locallow/Player.log", @"Mono path[0] = 'C:/Users/aboykin/KSP/KSP_x64_Data/Managed'" + "\nPlayer line 2\n");
        Write("data/logs/launcher-20261022.log", "old launcher log\n");
        Write("data/logs/launcher-20261023.log", "mid launcher log, user aboykin\n");
        Write("data/logs/launcher-20261024.log", "new launcher log\n");
        File.SetLastWriteTimeUtc(P("data/logs/launcher-20261022.log"), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(P("data/logs/launcher-20261023.log"), DateTime.UtcNow.AddDays(-1));
        Write("data/logs/crash-20261024-010101.log", "launcher crashed at C:\\Users\\aboykin\\x\n");
        Write("pack/game/GameData/VladMultiplayer/Traces/sync-20261024-040000.jsonl", "{\"kind\":\"apply\",\"path\":\"C:\\\\Users\\\\aboykin\\\\x\"}\n");
        Write("pack/game/GameData/VladMultiplayer/logs/vmp.log", "vmp log line\n");
        Write("pack/game/GameData/VladMultiplayer/Traces/not-text.bin", "binary stuff");

        var result = LogBundle.Build(Options() with
        {
            GameLogPath = game,
            PlayerLogPath = player,
            LauncherLogsDir = P("data/logs"),
            TraceDirs = [P("pack/game/GameData/VladMultiplayer/Traces"), P("pack/game/GameData/VladMultiplayer/logs")],
            ReportText = "VMP Launcher report\nPassword is s3cret-pass and the profile is C:\\Users\\aboykin\\x\n",
        });

        Assert.Equal(P("out.zip"), result.Path);
        Assert.False(File.Exists(P("out.zip.tmp")));
        Assert.Empty(result.Missing);
        var names = EntryNames(result.Path);
        string[] expected =
        [
            "README.txt", "report.txt", "KSP.log", "incident-extract.txt", "Player.log",
            "launcher/launcher-20261024.log", "launcher/launcher-20261023.log", "launcher/crash-20261024-010101.log",
            "vmp-traces/sync-20261024-040000.jsonl", "vmp-logs/vmp.log",
        ];
        foreach (var name in expected)
        {
            Assert.Contains(name, names);
        }
        Assert.DoesNotContain("launcher/launcher-20261022.log", names); // only the newest two launcher logs
        Assert.DoesNotContain("vmp-traces/not-text.bin", names);
        Assert.Equal(names.Length, result.Entries.Count);

        foreach (var name in names)
        {
            var text = ReadEntry(result.Path, name);
            Assert.DoesNotContain("aboykin", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("s3cret-pass", text, StringComparison.Ordinal);
        }
        Assert.Contains(@"'C:\Users\<user>\AppData\Local\VmpLauncherData\profiles\x\game\a.cfg'", ReadEntry(result.Path, "KSP.log"), StringComparison.Ordinal);
        Assert.Contains("C:/Users/<user>/x", ReadEntry(result.Path, "KSP.log"), StringComparison.Ordinal);
        Assert.Contains("[redacted]", ReadEntry(result.Path, "report.txt"), StringComparison.Ordinal);
        var extract = ReadEntry(result.Path, "incident-extract.txt");
        Assert.Contains("[LOG 02:19:20.359] Packing mun lander for orbit", extract, StringComparison.Ordinal);
        var readme = ReadEntry(result.Path, "README.txt");
        Assert.Contains("Launcher: 0.4.0", readme, StringComparison.Ordinal);
        Assert.Contains("Pack: 2026.10.24.1", readme, StringComparison.Ordinal);
        Assert.Contains("VMP client: 0.2.0-train.20261023.3", readme, StringComparison.Ordinal);
        Assert.Contains("2026-10-24 04:12:33 -04:00", readme, StringComparison.Ordinal);
        Assert.Contains("<user>", readme, StringComparison.Ordinal);
        Assert.Contains("Nothing was missing.", readme, StringComparison.Ordinal);
        Assert.True(result.UserNameReplacements >= 5);
        Assert.Equal(result.ZipBytes, new FileInfo(result.Path).Length);
    }

    [Fact]
    public void A_missing_game_log_still_makes_a_bundle_and_says_what_is_missing()
    {
        Write("data/logs/launcher-20261024.log", "launcher log\n");

        var result = LogBundle.Build(Options() with
        {
            GameLogPath = P("pack/game/KSP.log"),
            PlayerLogPath = P("nowhere/Player.log"),
            LauncherLogsDir = P("data/logs"),
            ReportText = "report\n",
        });

        var names = EntryNames(result.Path);
        Assert.Contains("README.txt", names);
        Assert.Contains("report.txt", names);
        Assert.Contains("launcher/launcher-20261024.log", names);
        Assert.DoesNotContain("KSP.log", names);
        Assert.DoesNotContain("incident-extract.txt", names);
        var missing = Assert.Single(result.Missing);
        Assert.StartsWith("KSP.log:", missing, StringComparison.Ordinal);
        Assert.Contains("KSP.log: the game log was not found", ReadEntry(result.Path, "README.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_held_open_for_writing_by_another_process_can_still_be_read()
    {
        foreach (var share in new[] { FileShare.ReadWrite | FileShare.Delete, FileShare.Read })
        {
            var path = P($"locked-{(int)share}/KSP.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var game = new FileStream(path, FileMode.Create, FileAccess.Write, share);
            var bytes = Encoding.UTF8.GetBytes("[LOG 00:00:01.000] written while KSP is running\r\n[WRN 00:00:02.000] second line, still being written");
            game.Write(bytes);
            game.Flush();

            var result = LogBundle.Build(Options($"locked-{(int)share}.zip") with { GameLogPath = path });

            Assert.Empty(result.Missing);
            var text = ReadEntry(result.Path, "KSP.log");
            Assert.Contains("written while KSP is running", text, StringComparison.Ordinal);
            Assert.Contains("second line, still being written", text, StringComparison.Ordinal); // a last line without a line break is kept
        }
    }

    [Fact]
    public void A_log_bigger_than_the_cap_keeps_its_tail_starting_at_a_line_boundary_and_says_so()
    {
        // 24 byte lines: 22 characters plus CRLF.
        string Line(int i) => $"line {i:0000000} .........";
        var game = Write("pack/game/KSP.log", string.Concat(Enumerable.Range(0, 5000).Select(i => Line(i) + "\r\n")));
        Assert.Equal(5000 * 24, new FileInfo(game).Length);

        // A cap that lands in the middle of a line: the partial line is dropped, the rest kept whole.
        var result = LogBundle.Build(Options() with { GameLogPath = game, GameLogCapBytes = 24 * 100 + 7 });
        var kept = ReadEntry(result.Path, "KSP.log").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Line(4999), kept[^1]);
        Assert.Equal(100, kept.Length);
        Assert.Equal(Line(4900), kept[0]);
        Assert.Matches(@"^line \d{7} \.{9}$", kept[0]);
        var readme = ReadEntry(result.Path, "README.txt");
        Assert.Contains("only its last", readme, StringComparison.Ordinal);
        Assert.Contains("KSP.log is 117.2 KB", readme, StringComparison.Ordinal);
        Assert.Contains("only the last", ReadEntry(result.Path, "incident-extract.txt"), StringComparison.Ordinal);

        // A cap that lands exactly on a line start loses nothing.
        var exact = LogBundle.Build(Options("exact.zip") with { GameLogPath = game, GameLogCapBytes = 24 * 100 });
        var keptExact = ReadEntry(exact.Path, "KSP.log").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(100, keptExact.Length);
        Assert.Equal(Line(4900), keptExact[0]);

        // Under the cap: whole file, no note.
        var whole = LogBundle.Build(Options("whole.zip") with { GameLogPath = game, GameLogCapBytes = 10_000_000 });
        Assert.Equal(5000, ReadEntry(whole.Path, "KSP.log").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("only its last", ReadEntry(whole.Path, "README.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_files_are_included_newest_first_until_the_cap_and_the_rest_is_listed()
    {
        var dir = P("traces");
        foreach (var (name, days) in new[] { ("new.jsonl", 0), ("mid.jsonl", 1), ("old.jsonl", 2) })
        {
            Write($"traces/{name}", new string('x', 1000) + "\n");
            File.SetLastWriteTimeUtc(Path.Combine(dir, name), DateTime.UtcNow.AddDays(-days));
        }

        var result = LogBundle.Build(Options() with { TraceDirs = [dir], TraceCapBytes = 2500 });

        var names = EntryNames(result.Path);
        Assert.Contains("vmp-traces/new.jsonl", names);
        Assert.Contains("vmp-traces/mid.jsonl", names);
        Assert.DoesNotContain("vmp-traces/old.jsonl", names);
        Assert.Contains(result.Notes, n => n.Contains("1 older trace file", StringComparison.Ordinal));
    }

    [Fact]
    public void An_output_file_is_never_left_half_made_when_the_build_is_cancelled()
    {
        var game = Write("pack/game/KSP.log", "[LOG 00:00:01.000] hi\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => LogBundle.Build(Options() with { GameLogPath = game }, ct: cts.Token));

        Assert.False(File.Exists(P("out.zip")));
        Assert.False(File.Exists(P("out.zip.tmp")));
    }

    [Fact]
    public void Building_over_an_older_bundle_replaces_it()
    {
        Write("out.zip", "not a zip");
        var result = LogBundle.Build(Options() with { ReportText = "r" });
        Assert.Contains("report.txt", EntryNames(result.Path));
    }

    [Fact]
    public void A_big_log_is_streamed_in_a_few_seconds_without_loading_it_into_memory()
    {
        // About 40 MB of KSP-looking lines, a few percent of them paths with a user name, some warnings, one crash.
        var game = P("pack/game/KSP.log");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        long written = 0;
        using (var writer = new StreamWriter(game, false, new UTF8Encoding(false), 1 << 20) { NewLine = "\r\n" })
        {
            var i = 0;
            while (written < 40L * 1024 * 1024)
            {
                var second = i / 700 % 60;
                var line = i % 9000 == 0
                    ? $"[WRN 01:{i / 42000 % 60:00}:{second:00}.{i % 1000:000}] [F: {i}]: Vessel mun lander crashed through terrain on Kerbin."
                    : i % 25 == 0
                        ? $@"[WRN 01:{i / 42000 % 60:00}:{second:00}.{i % 1000:000}] Config in file 'C:\Users\aboykin\AppData\Local\VmpLauncherData\profiles\vmp-train-career\game\GameData\Mod{i % 300}\part{i}.cfg' contains no data."
                        : $"[LOG 01:{i / 42000 % 60:00}:{second:00}.{i % 1000:000}] [Scatterer][Debug] Added custom sun flare for body number {i} with some more padding text here";
                writer.WriteLine(line);
                written += line.Length + 2;
                i++;
            }
        }
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = baseline;
        var sampler = new InlineProgress(_ => peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false)));
        var clock = Stopwatch.StartNew();

        var result = LogBundle.Build(Options() with { GameLogPath = game }, sampler);

        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        var growth = peak - baseline;
        Assert.True(growth < 100L * 1024 * 1024, $"managed memory grew by {growth / (1024 * 1024)} MB while handling a 40 MB log");
        Assert.True(result.ZipBytes < 20L * 1024 * 1024, $"zip is {result.ZipBytes} bytes");
        Assert.Equal(100, sampler.Last?.Percent);
        var text = ReadEntry(result.Path, "KSP.log");
        Assert.DoesNotContain("aboykin", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Length > 38 * 1024 * 1024);
        Assert.Contains("crashed through terrain", ReadEntry(result.Path, "incident-extract.txt"), StringComparison.Ordinal);
    }

    private sealed class InlineProgress(Action<LogBundleProgress> onReport) : IProgress<LogBundleProgress>
    {
        public LogBundleProgress? Last { get; private set; }

        public void Report(LogBundleProgress value)
        {
            Last = value;
            onReport(value);
        }
    }
}
