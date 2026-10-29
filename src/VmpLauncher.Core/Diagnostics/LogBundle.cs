using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace VmpLauncher.Core.Diagnostics;

public sealed record LogBundleProgress(string Message, int Percent);

/// <summary>What goes into the bundle and where it comes from. Every cap is a parameter so tests can use small ones.</summary>
public sealed record LogBundleOptions
{
    /// <summary>The zip to write (written to a temporary name first, so a half-made zip never sits at this path).</summary>
    public required string OutputPath { get; init; }

    /// <summary>The pack copy's <c>KSP.log</c>: held open by KSP while it runs, read with sharing, redacted and streamed.</summary>
    public string? GameLogPath { get; init; }

    /// <summary>Unity's <c>Player.log</c> (LocalLow\Squad\Kerbal Space Program), if present.</summary>
    public string? PlayerLogPath { get; init; }

    /// <summary>The launcher's own <c>logs</c> folder: the newest <see cref="LauncherLogCount"/> launcher logs (and crash logs) go in.</summary>
    public string? LauncherLogsDir { get; init; }

    /// <summary>VMP's sync trace folders (<c>GameData\VladMultiplayer\Traces</c>, <c>...\logs</c>); included while they stay under <see cref="TraceCapBytes"/>.</summary>
    public IReadOnlyList<string> TraceDirs { get; init; } = [];

    /// <summary>The already redacted launcher report (versions, pack state, recent log); goes in as <c>report.txt</c>.</summary>
    public string? ReportText { get; init; }

    /// <summary>The Windows account names to hide as whole words (the <c>C:\Users\&lt;name&gt;</c> rule works without this).</summary>
    public IReadOnlyList<string> UserNames { get; init; } = [];

    /// <summary>Secrets (server password, private-repo tokens) to remove from everything.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];

    /// <summary>Lines for the README's version section, e.g. ("Launcher", "0.4.0").</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Facts { get; init; } = [];

    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    public long GameLogCapBytes { get; init; } = 300L * 1024 * 1024;

    public long PlayerLogCapBytes { get; init; } = 100L * 1024 * 1024;

    public long TraceCapBytes { get; init; } = 20L * 1024 * 1024;

    public long ExtractCapBytes { get; init; } = IncidentExtractor.DefaultMaxBytes;

    public long LauncherLogCapBytes { get; init; } = 10L * 1024 * 1024;

    public int LauncherLogCount { get; init; } = 2;

    public CompressionLevel Compression { get; init; } = CompressionLevel.Optimal;
}

/// <summary>One file in the finished zip: <see cref="Bytes"/> is its size before compression.</summary>
public sealed record LogBundleEntry(string Name, long Bytes, string? Note);

public sealed record LogBundleResult(string Path, long ZipBytes, IReadOnlyList<LogBundleEntry> Entries, IReadOnlyList<string> Missing, IReadOnlyList<string> Notes, long UserNameReplacements, long SecretReplacements);

/// <summary>
/// Makes the "Send my logs" zip: the game log (redacted, streamed, tail-capped), Player.log, the launcher's recent logs, VMP's
/// sync traces, the launcher report, an incident digest of the game log and a README. Pure Core code (no UI), so tests
/// can run it on temp folders.
/// </summary>
public static class LogBundle
{
    private const int ProgressStepLines = 8192;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".txt", ".json", ".jsonl", ".csv", ".xml", ".cfg", ".trace", ".md",
    };

    public static LogBundleResult Build(LogBundleOptions options, IProgress<LogBundleProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var output = Path.GetFullPath(options.OutputPath);
        var temp = output + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var redactor = new LogRedactor(options.UserNames, options.Secrets);
        var plan = Plan(options);
        var meter = new Meter(progress, plan.TotalBytes);
        var entries = new List<LogBundleEntry>();
        var missing = new List<string>();
        var notes = new List<string>();
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(options.ReportText))
                {
                    AddText(zip, "report.txt", redactor.Redact(options.ReportText), options, entries);
                }

                IncidentExtractor? extractor = null;
                if (plan.GameLog is null)
                {
                    missing.Add("KSP.log: the game log was not found in the pack copy (the game may not have been started from this copy yet).");
                }
                else
                {
                    meter.Phase("Reading the game log");
                    extractor = new IncidentExtractor(options.ExtractCapBytes);
                    var copied = CopyLog(zip, "KSP.log", plan.GameLog, options.GameLogCapBytes, redactor, options, extractor, meter, ct);
                    Record(copied, "KSP.log", entries, missing, notes);
                    if (!copied.Ok)
                    {
                        extractor = null; // an unreadable log has nothing to summarise
                    }
                }

                if (extractor is not null)
                {
                    var note = entries.FirstOrDefault(e => e.Name == "KSP.log")?.Note is { } trimmed ? $"KSP.log, {trimmed}" : null;
                    AddText(zip, "incident-extract.txt", extractor.Finish(note), options, entries);
                }

                if (plan.PlayerLog is not null)
                {
                    meter.Phase("Reading Player.log");
                    var copied = CopyLog(zip, "Player.log", plan.PlayerLog, options.PlayerLogCapBytes, redactor, options, null, meter, ct);
                    Record(copied, "Player.log", entries, missing, notes);
                }

                if (plan.LauncherLogs.Count > 0)
                {
                    meter.Phase("Adding the launcher logs");
                }
                foreach (var log in plan.LauncherLogs)
                {
                    var name = "launcher/" + Path.GetFileName(log);
                    Record(CopyLog(zip, name, log, options.LauncherLogCapBytes, redactor, options, null, meter, ct), name, entries, missing, notes);
                }

                if (plan.Traces.Count > 0)
                {
                    meter.Phase("Adding the VMP traces");
                }
                foreach (var trace in plan.Traces)
                {
                    Record(CopyLog(zip, trace.EntryName, trace.Path, 0, redactor, options, null, meter, ct), trace.EntryName, entries, missing, notes);
                }
                notes.AddRange(plan.TraceNotes);

                meter.Phase("Finishing the zip");
                AddText(zip, "README.txt", BuildReadme(options, entries, missing, notes, redactor), options, entries, first: true);
            }
            File.Move(temp, output, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        meter.Done();
        return new LogBundleResult(output, new FileInfo(output).Length, entries, missing, notes, redactor.UserNameReplacements, redactor.SecretReplacements);
    }

    // ---- planning ----------------------------------------------------------------------------------------------

    private sealed record TracePlan(string EntryName, string Path, long Bytes);

    private sealed record BundlePlan(string? GameLog, string? PlayerLog, IReadOnlyList<string> LauncherLogs, IReadOnlyList<TracePlan> Traces, IReadOnlyList<string> TraceNotes, long TotalBytes);

    private static BundlePlan Plan(LogBundleOptions o)
    {
        var gameLog = ExistingFile(o.GameLogPath);
        var playerLog = ExistingFile(o.PlayerLogPath);
        var launcherLogs = FindLauncherLogs(o);
        var (traces, traceNotes) = FindTraces(o);
        long total = 4096;
        total += Capped(gameLog, o.GameLogCapBytes) + Capped(playerLog, o.PlayerLogCapBytes);
        total += launcherLogs.Sum(f => Capped(f, o.LauncherLogCapBytes));
        total += traces.Sum(t => t.Bytes);
        return new BundlePlan(gameLog, playerLog, launcherLogs, traces, traceNotes, total);
    }

    private static string? ExistingFile(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static long Capped(string? path, long cap)
    {
        if (path is null)
        {
            return 0;
        }
        try
        {
            var length = new FileInfo(path).Length;
            return cap > 0 ? Math.Min(length, cap) : length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static List<string> FindLauncherLogs(LogBundleOptions o)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(o.LauncherLogsDir) || !Directory.Exists(o.LauncherLogsDir) || o.LauncherLogCount <= 0)
        {
            return result;
        }
        try
        {
            var dir = new DirectoryInfo(o.LauncherLogsDir);
            result.AddRange(dir.EnumerateFiles("launcher-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(o.LauncherLogCount).Select(f => f.FullName));
            // A crash log is tiny and says why the launcher itself died: take the newest two as well.
            result.AddRange(dir.EnumerateFiles("crash-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(2).Select(f => f.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable logs folder just means fewer files.
        }
        return result;
    }

    /// <summary>Newest trace files first, while they fit under the cap; files that do not fit are listed in the notes, not silently dropped.</summary>
    private static (List<TracePlan> Traces, List<string> Notes) FindTraces(LogBundleOptions o)
    {
        var traces = new List<TracePlan>();
        var notes = new List<string>();
        var used = 0L;
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in o.TraceDirs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    continue;
                }
                var label = "vmp-" + Path.GetFileName(dir.TrimEnd('\\', '/')).ToLowerInvariant();
                while (!labels.Add(label))
                {
                    label += "-2";
                }
                var root = new DirectoryInfo(dir);
                var files = root.EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f => TextExtensions.Contains(f.Extension))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                var skipped = 0;
                foreach (var f in files)
                {
                    if (used + f.Length > o.TraceCapBytes)
                    {
                        skipped++;
                        continue;
                    }
                    used += f.Length;
                    var rel = Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/');
                    traces.Add(new TracePlan($"{label}/{rel}", f.FullName, f.Length));
                }
                if (skipped > 0)
                {
                    notes.Add($"{Plural(skipped, "older trace file")} in {Path.GetFileName(dir.TrimEnd('\\', '/'))} left out to stay under the {FormatBytes(o.TraceCapBytes)} trace limit.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"The trace folder {Path.GetFileName(dir.TrimEnd('\\', '/'))} could not be read ({ex.Message}).");
            }
        }
        return (traces, notes);
    }

    // ---- copying -----------------------------------------------------------------------------------------------

    private sealed record Copied(bool Ok, long SourceBytes, long WrittenBytes, long SkippedBytes, string? Error);

    /// <summary>
    /// Streams one text file into a zip entry line by line through the redactor. The file may be held open for writing by
    /// another process (KSP does that to KSP.log), so it is opened with full sharing. A file bigger than
    /// <paramref name="capBytes"/> (0 = no cap) keeps its last <paramref name="capBytes"/> bytes, starting at a line boundary.
    /// </summary>
    private static Copied CopyLog(ZipArchive zip, string entryName, string path, long capBytes, LogRedactor redactor, LogBundleOptions options,
        IncidentExtractor? extractor, Meter meter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FileStream source;
        try
        {
            source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Copied(false, 0, 0, 0, ex.Message);
        }
        using (source)
        {
            var length = source.Length;
            var skipped = 0L;
            if (capBytes > 0 && length > capBytes)
            {
                skipped = SkipToLineStart(source, length - capBytes);
            }

            var entry = zip.CreateEntry(entryName, options.Compression);
            entry.LastWriteTime = ZipTime(path);
            string? error = null;
            long written;
            using (var counting = new CountingStream(entry.Open()))
            {
                using (var writer = new StreamWriter(counting, Utf8, 81920, leaveOpen: true))
                using (var text = new StreamReader(source, Utf8, detectEncodingFromByteOrderMarks: true, 81920, leaveOpen: true))
                {
                    var lines = new BoundedLineReader(text);
                    var lastPosition = source.Position;
                    var count = 0;
                    try
                    {
                        while (lines.ReadLine() is { } line)
                        {
                            line = redactor.Redact(line);
                            writer.Write(line);
                            writer.Write(lines.LastLineEnding switch { LineEnding.CrLf => "\r\n", LineEnding.Lf => "\n", _ => string.Empty });
                            extractor?.AddLine(line);
                            if (++count % ProgressStepLines == 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                var position = source.Position;
                                meter.Add(position - lastPosition);
                                lastPosition = position;
                            }
                        }
                    }
                    catch (IOException ex)
                    {
                        error = ex.Message; // the file was truncated or deleted while we read it: keep what we have
                    }
                    meter.Add(Math.Max(0, source.Position - lastPosition));
                }
                written = counting.BytesWritten;
            }
            return new Copied(true, length, written, skipped, error);
        }
    }

    /// <summary>Positions <paramref name="stream"/> at the first line start at or after <paramref name="start"/>; returns how many bytes were left out before it.</summary>
    private static long SkipToLineStart(FileStream stream, long start)
    {
        const int MaxScan = 1024 * 1024;
        // Look at the byte before: if it ends a line we are already on a line start.
        var position = Math.Max(0, start - 1);
        stream.Seek(position, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var scanned = 0;
        while (scanned < MaxScan)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                stream.Seek(position + scanned + newline + 1, SeekOrigin.Begin);
                return stream.Position;
            }
            scanned += read;
        }
        // No line break in a megabyte (not a text log): start where we were asked to.
        stream.Seek(start, SeekOrigin.Begin);
        return start;
    }

    private static void AddText(ZipArchive zip, string name, string content, LogBundleOptions options, List<LogBundleEntry> entries, bool first = false)
    {
        var entry = zip.CreateEntry(name, options.Compression);
        entry.LastWriteTime = options.Now.ToLocalTime();
        var bytes = Utf8.GetBytes(content);
        using (var stream = entry.Open())
        {
            stream.Write(bytes, 0, bytes.Length);
        }
        var item = new LogBundleEntry(name, bytes.Length, null);
        if (first)
        {
            entries.Insert(0, item);
        }
        else
        {
            entries.Add(item);
        }
    }

    private static void Record(Copied copied, string name, List<LogBundleEntry> entries, List<string> missing, List<string> notes)
    {
        if (!copied.Ok)
        {
            missing.Add($"{name}: could not be read ({copied.Error}).");
            return;
        }
        string? note = null;
        if (copied.SkippedBytes > 0)
        {
            note = $"only the last {FormatBytes(copied.SourceBytes - copied.SkippedBytes)} of {FormatBytes(copied.SourceBytes)} kept";
            notes.Add($"{name} is {FormatBytes(copied.SourceBytes)}: only its last {FormatBytes(copied.SourceBytes - copied.SkippedBytes)} are in the bundle (it starts at a line boundary).");
        }
        if (copied.Error is not null)
        {
            note = (note is null ? string.Empty : note + "; ") + $"reading stopped early ({copied.Error})";
            notes.Add($"{name}: reading stopped early ({copied.Error}).");
        }
        entries.Add(new LogBundleEntry(name, copied.WrittenBytes, note));
    }

    // ---- README ------------------------------------------------------------------------------------------------

    private static string BuildReadme(LogBundleOptions o, List<LogBundleEntry> entries, List<string> missing, List<string> notes, LogRedactor redactor)
    {
        var sb = new StringBuilder();
        const string nl = "\r\n";
        sb.Append("VMP logs bundle").Append(nl);
        sb.Append("Made by the VMP Launcher on ").Append(o.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append(nl).Append(nl);

        if (o.Facts.Count > 0)
        {
            sb.Append("Versions and setup").Append(nl);
            foreach (var (key, value) in o.Facts)
            {
                sb.Append("  ").Append(key).Append(": ").Append(redactor.Redact(value)).Append(nl);
            }
            sb.Append(nl);
        }

        sb.Append("What is inside").Append(nl);
        foreach (var e in entries)
        {
            sb.Append("  ").Append(e.Name.PadRight(34)).Append(FormatBytes(e.Bytes).PadLeft(10)).Append("  ").Append(Describe(e)).Append(nl);
        }
        sb.Append(nl);

        sb.Append("Not in this bundle").Append(nl);
        if (missing.Count == 0)
        {
            sb.Append("  Nothing was missing.").Append(nl);
        }
        foreach (var m in missing)
        {
            sb.Append("  ").Append(m).Append(nl);
        }
        sb.Append(nl);

        if (notes.Count > 0)
        {
            sb.Append("Notes").Append(nl);
            foreach (var n in notes)
            {
                sb.Append("  ").Append(n).Append(nl);
            }
            sb.Append(nl);
        }

        sb.Append("What was removed").Append(nl);
        sb.Append("  Your Windows user name is replaced with <user> in every C:\\Users\\<name> path (any slash style, any case) and wherever it stood as a whole word.").Append(nl);
        sb.Append("  The server password and the private-mod tokens are removed everywhere.").Append(nl);
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"  Replaced in this bundle: {redactor.UserNameReplacements:N0} user name places, {redactor.SecretReplacements:N0} secrets (counted before this README was written).")).Append(nl);
        sb.Append("  Everything else is as the game wrote it: your player name, vessel names and the server address are still there.").Append(nl).Append(nl);
        sb.Append("Drop the whole zip in Discord for the host. Nothing was sent anywhere by the launcher.").Append(nl);
        return sb.ToString();
    }

    private static string Describe(LogBundleEntry e)
    {
        var what = e.Name switch
        {
            "README.txt" => "this file",
            "report.txt" => "the launcher report: versions, pack state, the error and the end of the launcher log",
            "incident-extract.txt" => "quick read: errors, crashes, vessel removals, time-sync and subspace lines from KSP.log",
            "KSP.log" => "the game log of the pack copy (VMP writes here too, lines starting [VMP])",
            "Player.log" => "Unity's own log of the last game start",
            _ when e.Name.StartsWith("launcher/", StringComparison.Ordinal) => "the launcher's own log",
            _ when e.Name.StartsWith("vmp-", StringComparison.Ordinal) => "VMP sync trace",
            _ => string.Empty,
        };
        return e.Note is null ? what : $"{what} ({e.Note})";
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static DateTimeOffset ZipTime(string path)
    {
        try
        {
            var time = File.GetLastWriteTime(path);
            return time.Year is >= 1980 and <= 2107 ? new DateTimeOffset(time) : DateTimeOffset.Now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.Now;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp is harmless.
        }
    }

    public static string FormatBytes(long bytes)
    {
        double v = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        var i = 0;
        while (v >= 1000 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : string.Create(CultureInfo.InvariantCulture, $"{v:0.#} {units[i]}");
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    /// <summary>Turns bytes read into a percentage, reported only when it moves (at most 100 callbacks).</summary>
    private sealed class Meter(IProgress<LogBundleProgress>? progress, long total)
    {
        private long _done;
        private int _lastPercent = -1;
        private string _message = "Starting";

        public void Phase(string message)
        {
            _message = message;
            Send(force: true);
        }

        public void Add(long bytes)
        {
            _done += Math.Max(0, bytes);
            Send(force: false);
        }

        public void Done() => progress?.Report(new LogBundleProgress("Done", 100));

        private void Send(bool force)
        {
            if (progress is null)
            {
                return;
            }
            var percent = (int)Math.Clamp(_done * 99 / Math.Max(1, total), 0, 99);
            if (force || percent != _lastPercent)
            {
                _lastPercent = percent;
                progress.Report(new LogBundleProgress(_message, percent));
            }
        }
    }

    /// <summary>Counts what is written through it (the zip entry stream cannot say how big the entry was).</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
