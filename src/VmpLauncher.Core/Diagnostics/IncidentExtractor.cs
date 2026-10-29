using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VmpLauncher.Core.Diagnostics;

/// <summary>
/// Reads a KSP.log line by line and keeps only what the host needs to see what happened: errors, exceptions and
/// warnings (each distinct one once), explosions and crashes with the lines just before them, vessel removals,
/// time-sync and subspace events, packing, ApexGuidance node and planner lines, and disconnects. Every kept line keeps its own
/// timestamp prefix. The same message several times in a row becomes one line followed by <c>(x N)</c>, a warning or error that
/// shows up again later is counted in the summary at the end instead of being repeated, and the result never grows past
/// <c>maxBytes</c> (the oldest lines are dropped first). Memory stays small however big the log is.
/// </summary>
public sealed partial class IncidentExtractor
{
    public const long DefaultMaxBytes = 5L * 1024 * 1024;
    public const int DefaultContextLines = 5;

    private const int MaxAttachedLines = 4;
    private const int MaxTrackedMessages = 500_000;
    private const int MaxRepeatInfos = 20_000;
    private const int MaxSummaryLines = 60;
    private const int SummaryTextChars = 300;
    private const string NewLine = "\r\n";

    private static readonly SearchValues<string> IncidentMarkers =
        SearchValues.Create(["Exploded!!", "crashed through terrain", "VesselLoss"], StringComparison.Ordinal);

    private static readonly SearchValues<string> KeyMarkers =
        SearchValues.Create(
            ["Removing vessel", "Detected stuck", "FORCING a time sync", "Locked to subspace", "Packing ", "Unpacking ", "[ApexGuidance][NODE_EXEC", "[ApexGuidance][PLANNER"],
            StringComparison.Ordinal);

    private static readonly SearchValues<string> DisconnectHints =
        SearchValues.Create(["isconnect", "onnection", "timed out", "imed out"], StringComparison.OrdinalIgnoreCase);

    private enum Level { Log, Warning, Error, Exception }

    private enum Kind { Ignore, Incident, Key, Repeating }

    private struct Seen
    {
        public int Count;
        public int FirstMs;
    }

    private sealed class RepeatInfo(string text, int count, int firstMs, int lastMs)
    {
        public string Text { get; } = text;
        public int Count { get; set; } = count;
        public int FirstMs { get; } = firstMs;
        public int LastMs { get; set; } = lastMs;
    }

    private readonly long _maxBytes;
    private readonly int _contextLines;

    // The entry being read: a "[LOG hh:mm:ss.fff] text" line, plus (for errors and exceptions) the stack lines under it.
    private bool _hasCurrent;
    private string _curHeader = string.Empty;
    private Level _curLevel;
    private int _curMsgStart;
    private int _curMs;
    private List<string>? _curAttached;
    private bool _curAttachOpen;

    // The last few entries (their first lines) so an explosion can show what led up to it.
    private readonly string[] _ring;
    private readonly long[] _ringSeq;
    private long _seq;
    private long _consumedSeq; // entries up to here are already in the output (as a match or as context)

    // The newest kept line, held back until the next one shows whether it repeats.
    private string? _pendingHeader;
    private string? _pendingTail;
    private string? _pendingSig;
    private int _pendingCount;

    private readonly Queue<string> _chunks = new();
    private long _chunkBytes;
    private long _droppedChunks;

    private readonly Dictionary<ulong, Seen> _seen = new();
    private readonly Dictionary<ulong, RepeatInfo> _repeats = new();

    private string? _firstStamp;
    private string? _lastHeader;

    public IncidentExtractor(long maxBytes = DefaultMaxBytes, int contextLines = DefaultContextLines)
    {
        _maxBytes = Math.Max(1024, maxBytes);
        _contextLines = Math.Max(0, contextLines);
        _ring = new string[_contextLines + 1];
        _ringSeq = new long[_contextLines + 1];
    }

    public long LinesRead { get; private set; }

    public long Entries { get; private set; }

    public long ErrorEntries { get; private set; }

    public long WarningEntries { get; private set; }

    public long ExceptionEntries { get; private set; }

    /// <summary>Convenience for tests and small inputs: feed every line and return the finished text.</summary>
    public static string Build(IEnumerable<string> lines, long maxBytes = DefaultMaxBytes, int contextLines = DefaultContextLines, string? sourceNote = null)
    {
        var extractor = new IncidentExtractor(maxBytes, contextLines);
        foreach (var line in lines)
        {
            extractor.AddLine(line);
        }
        return extractor.Finish(sourceNote);
    }

    /// <summary>Feeds the next line of the log (without its line ending).</summary>
    public void AddLine(string line)
    {
        LinesRead++;
        if (TryParseHeader(line, out var level, out var msgStart, out var ms))
        {
            FlushCurrent();
            _hasCurrent = true;
            _curHeader = line;
            _curLevel = level;
            _curMsgStart = msgStart;
            _curMs = ms;
            _curAttached?.Clear();
            _curAttachOpen = level is Level.Error or Level.Exception;
        }
        else if (_hasCurrent && _curAttachOpen)
        {
            if (line.Length == 0 || (_curAttached?.Count ?? 0) >= MaxAttachedLines)
            {
                _curAttachOpen = false;
            }
            else
            {
                (_curAttached ??= new List<string>()).Add(line);
            }
        }
    }

    /// <summary>Ends the log and returns the digest: a short header, the kept lines, and what repeated.</summary>
    public string Finish(string? sourceNote = null)
    {
        FlushCurrent();
        FlushPending();

        var header = new StringBuilder();
        header.Append("VMP incident digest: the lines of the game log worth reading first (full log: KSP.log).").Append(NewLine);
        header.Append(sourceNote is { Length: > 0 } ? sourceNote : "KSP.log, whole file").Append(": ")
            .Append(LinesRead.ToString("N0", CultureInfo.InvariantCulture)).Append(" lines");
        if (_firstStamp is not null)
        {
            header.Append(", ").Append(_firstStamp).Append(" to ").Append(Stamp(_lastHeader ?? string.Empty));
        }
        header.Append(NewLine);
        header.Append(string.Create(CultureInfo.InvariantCulture, $"In the whole log: {ErrorEntries:N0} errors, {WarningEntries:N0} warnings, {ExceptionEntries:N0} exceptions.")).Append(NewLine);
        header.Append("Kept: errors, exceptions and warnings (each different message once; later copies are counted at the end), explosions and crashes, ")
            .Append("vessel removals, subspace/time-sync lines, packing, ApexGuidance node and planner lines, disconnects.").Append(NewLine);
        header.Append("Indented lines are context from just before an explosion or crash. \"(x N)\" means the same message N times in a row.").Append(NewLine);

        var summary = BuildSummary();
        var dropped = _droppedChunks > 0;
        var droppedNote = string.Empty;

        // Fit header + notice + body + summary under the cap: the oldest body lines go first.
        var fixedBytes = Encoding.UTF8.GetByteCount(header.ToString()) + Encoding.UTF8.GetByteCount(summary) + 160;
        if (fixedBytes > _maxBytes / 2)
        {
            summary = TrimSummary(summary, Math.Max(0, (int)(_maxBytes / 2) - Encoding.UTF8.GetByteCount(header.ToString()) - 160));
            fixedBytes = Encoding.UTF8.GetByteCount(header.ToString()) + Encoding.UTF8.GetByteCount(summary) + 160;
        }
        var budget = _maxBytes - fixedBytes;
        while (_chunks.Count > 0 && _chunkBytes > budget)
        {
            _chunkBytes -= ChunkBytes(_chunks.Dequeue());
            _droppedChunks++;
            dropped = true;
        }
        if (dropped)
        {
            droppedNote = string.Create(CultureInfo.InvariantCulture, $"(The oldest {_droppedChunks:N0} kept lines were left out to stay under {FormatSize(_maxBytes)}; the newest are below.){NewLine}");
        }

        var sb = new StringBuilder(header.Length + (int)Math.Min(_chunkBytes, int.MaxValue / 2) + summary.Length + 256);
        sb.Append(header).Append(droppedNote).Append(NewLine);
        if (_chunks.Count == 0)
        {
            sb.Append("(no matching lines)").Append(NewLine);
        }
        foreach (var chunk in _chunks)
        {
            sb.Append(chunk).Append(NewLine);
        }
        sb.Append(summary);
        return sb.ToString();
    }

    // ---- reading entries ---------------------------------------------------------------------------------------

    private void FlushCurrent()
    {
        if (!_hasCurrent)
        {
            return;
        }
        _hasCurrent = false;
        _seq++;
        Entries++;
        var slot = (int)(_seq % _ring.Length);
        _ring[slot] = _curHeader;
        _ringSeq[slot] = _seq;
        _firstStamp ??= Stamp(_curHeader);
        _lastHeader = _curHeader;
        switch (_curLevel)
        {
            case Level.Error:
                ErrorEntries++;
                break;
            case Level.Warning:
                WarningEntries++;
                break;
            case Level.Exception:
                ExceptionEntries++;
                break;
        }

        var message = _curHeader.AsSpan(_curMsgStart);
        var kind = message.ContainsAny(IncidentMarkers) ? Kind.Incident
                 : message.ContainsAny(KeyMarkers) || IsDisconnect(message) ? Kind.Key
                 : _curLevel != Level.Log || message.Contains("NullReferenceException", StringComparison.Ordinal) ? Kind.Repeating
                 : Kind.Ignore;
        if (kind == Kind.Ignore)
        {
            return;
        }

        var signature = Signature(message);
        if (kind == Kind.Repeating)
        {
            AddRepeating(signature, message);
            return;
        }
        if (_pendingSig is not null && string.Equals(signature, _pendingSig, StringComparison.Ordinal))
        {
            _pendingCount++;
            _consumedSeq = _seq;
            return;
        }
        Emit(signature, withContext: kind == Kind.Incident);
    }

    /// <summary>An error, exception or warning: shown the first time, then only counted (and "(x N)" when it repeats on the very next kept line).</summary>
    private void AddRepeating(string signature, ReadOnlySpan<char> message)
    {
        var hash = Fnv1a(signature);
        if (_seen.Count >= MaxTrackedMessages && !_seen.ContainsKey(hash))
        {
            Emit(signature, withContext: false); // too many different messages to track: keep this one
            return;
        }
        ref var seen = ref CollectionsMarshal.GetValueRefOrAddDefault(_seen, hash, out var exists);
        if (!exists)
        {
            seen.Count = 1;
            seen.FirstMs = _curMs;
            Emit(signature, withContext: false);
            return;
        }
        seen.Count++;
        var count = seen.Count;
        var firstMs = seen.FirstMs;
        if (_pendingSig is not null && string.Equals(signature, _pendingSig, StringComparison.Ordinal))
        {
            _pendingCount++;
            _consumedSeq = _seq;
        }
        if (count == 2)
        {
            if (_repeats.Count < MaxRepeatInfos)
            {
                _repeats[hash] = new RepeatInfo(Describe(message), 2, firstMs, _curMs);
            }
        }
        else if (_repeats.TryGetValue(hash, out var info))
        {
            info.Count = count;
            info.LastMs = _curMs;
        }
    }

    /// <summary>Puts the current entry in the output (after the previous kept line), with the lines before it when it is an incident.</summary>
    private void Emit(string signature, bool withContext)
    {
        FlushPending();
        if (withContext && _contextLines > 0)
        {
            var any = false;
            for (var back = _contextLines; back >= 1; back--)
            {
                var seq = _seq - back;
                if (seq <= _consumedSeq || seq < 1)
                {
                    continue;
                }
                var slot = (int)(seq % _ring.Length);
                if (_ringSeq[slot] != seq)
                {
                    continue;
                }
                if (!any && (_chunks.Count > 0))
                {
                    AddChunk(string.Empty);
                }
                any = true;
                AddChunk("    " + _ring[slot]);
            }
        }
        _consumedSeq = _seq;
        _pendingHeader = _curHeader;
        _pendingTail = _curAttached is { Count: > 0 } ? string.Join(NewLine, _curAttached) : null;
        _pendingSig = signature;
        _pendingCount = 1;
    }

    private void FlushPending()
    {
        if (_pendingHeader is null)
        {
            return;
        }
        var text = _pendingCount > 1 ? $"{_pendingHeader} (x {_pendingCount})" : _pendingHeader;
        AddChunk(_pendingTail is null ? text : text + NewLine + _pendingTail);
        _pendingHeader = null;
        _pendingTail = null;
        _pendingSig = null;
        _pendingCount = 0;
    }

    private void AddChunk(string text)
    {
        _chunks.Enqueue(text);
        _chunkBytes += ChunkBytes(text);
        while (_chunkBytes > _maxBytes && _chunks.Count > 1)
        {
            _chunkBytes -= ChunkBytes(_chunks.Dequeue());
            _droppedChunks++;
        }
    }

    private static long ChunkBytes(string text) => Encoding.UTF8.GetByteCount(text) + NewLine.Length;

    // ---- summary -----------------------------------------------------------------------------------------------

    private string BuildSummary()
    {
        var top = _repeats.Values.OrderByDescending(r => r.Count).ThenBy(r => r.FirstMs).Take(MaxSummaryLines).ToList();
        if (top.Count == 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        sb.Append(NewLine).Append("=== Warnings and errors that came back (each is listed once above; counts include every copy) ===").Append(NewLine);
        foreach (var r in top)
        {
            sb.Append('x').Append(r.Count.ToString(CultureInfo.InvariantCulture)).Append("  ")
                .Append(FormatMs(r.FirstMs)).Append(" to ").Append(FormatMs(r.LastMs)).Append("  ").Append(r.Text).Append(NewLine);
        }
        var more = _repeats.Count - top.Count;
        if (more > 0)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"... and {more:N0} more messages that repeated less often.")).Append(NewLine);
        }
        return sb.ToString();
    }

    private static string TrimSummary(string summary, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }
        var lines = summary.Split(NewLine);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (Encoding.UTF8.GetByteCount(sb.ToString()) + line.Length + NewLine.Length > maxBytes)
            {
                break;
            }
            sb.Append(line).Append(NewLine);
        }
        return sb.ToString();
    }

    private string Describe(ReadOnlySpan<char> message)
    {
        var tag = _curLevel switch { Level.Error => "[ERR] ", Level.Warning => "[WRN] ", Level.Exception => "[EXC] ", _ => "[LOG] " };
        var text = message.Length > SummaryTextChars ? string.Concat(message[..SummaryTextChars], "...") : message.ToString();
        return tag + text;
    }

    // ---- parsing helpers ---------------------------------------------------------------------------------------

    /// <summary>"[LOG 01:18:27.170] text": level tag, time of day, and where the text starts.</summary>
    private static bool TryParseHeader(string line, out Level level, out int messageStart, out int milliseconds)
    {
        level = Level.Log;
        messageStart = 0;
        milliseconds = 0;
        if (line.Length < 15 || line[0] != '[' || line[4] != ' ')
        {
            return false;
        }
        switch (line[1], line[2], line[3])
        {
            case ('L', 'O', 'G'):
                level = Level.Log;
                break;
            case ('W', 'R', 'N'):
                level = Level.Warning;
                break;
            case ('E', 'R', 'R'):
                level = Level.Error;
                break;
            case ('E', 'X', 'C'):
                level = Level.Exception;
                break;
            default:
                return false;
        }
        if (!TwoDigits(line, 5, out var hh) || line[7] != ':' || !TwoDigits(line, 8, out var mm) || line[10] != ':' || !TwoDigits(line, 11, out var ss))
        {
            return false;
        }
        var i = 13;
        var frac = 0;
        if (i < line.Length && line[i] == '.')
        {
            i++;
            var digits = 0;
            while (i < line.Length && char.IsAsciiDigit(line[i]))
            {
                if (digits < 3)
                {
                    frac = (frac * 10) + (line[i] - '0');
                    digits++;
                }
                i++;
            }
            while (digits++ < 3)
            {
                frac *= 10;
            }
        }
        if (i >= line.Length || line[i] != ']')
        {
            return false;
        }
        milliseconds = (((hh * 60) + mm) * 60 + ss) * 1000 + frac;
        messageStart = i + 1 < line.Length && line[i + 1] == ' ' ? i + 2 : i + 1;
        return true;
    }

    private static bool TwoDigits(string s, int at, out int value)
    {
        value = 0;
        if (at + 1 >= s.Length || !char.IsAsciiDigit(s[at]) || !char.IsAsciiDigit(s[at + 1]))
        {
            return false;
        }
        value = ((s[at] - '0') * 10) + (s[at + 1] - '0');
        return true;
    }

    private static string Stamp(string header)
    {
        var end = header.IndexOf(']');
        return end > 5 ? header[5..end] : string.Empty;
    }

    private static string FormatMs(int ms) =>
        string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB") : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB");

    /// <summary>The message without the "[F: 267821]:" frame counter KSP puts in front of some warnings (it would make every copy look different), plus the stack lines under it.</summary>
    private string Signature(ReadOnlySpan<char> message)
    {
        var text = message;
        if (text.StartsWith("[F: ", StringComparison.Ordinal))
        {
            var close = text.IndexOf(']');
            if (close > 4 && text[4..close].IndexOfAnyExceptInRange('0', '9') < 0)
            {
                text = text[(close + 1)..];
                if (text.Length > 0 && text[0] == ':')
                {
                    text = text[1..];
                }
                text = text.TrimStart();
            }
        }
        if (_curAttached is not { Count: > 0 })
        {
            return text.ToString();
        }
        return string.Concat(text.ToString(), "\n", string.Join('\n', _curAttached));
    }

    private static bool IsDisconnect(ReadOnlySpan<char> message)
    {
        if (!message.ContainsAny(DisconnectHints))
        {
            return false;
        }
        // Asset names and texture loads mention "disconnect" (a sound, an icon): not an event.
        if (message.StartsWith("Load ", StringComparison.Ordinal) || message.StartsWith("Cannot assign", StringComparison.Ordinal))
        {
            return false;
        }
        return DisconnectRegex().IsMatch(message);
    }

    private static ulong Fnv1a(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }
        return hash;
    }

    [GeneratedRegex(@"\b(?:disconnect(?:ed|ing|s)?|connection\s+(?:was\s+|has\s+been\s+)?(?:lost|closed|reset|dropped|timed\s+out)|lost\s+(?:the\s+)?connection|connection\s+to\s+(?:the\s+)?server|timed\s+out)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DisconnectRegex();
}
