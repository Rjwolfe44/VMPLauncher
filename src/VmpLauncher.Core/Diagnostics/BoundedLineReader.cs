using System.Text;

namespace VmpLauncher.Core.Diagnostics;

/// <summary>How a line ended in its source.</summary>
internal enum LineEnding
{
    /// <summary>The last line of a file that does not end with a line break.</summary>
    None,
    Lf,
    CrLf,
}

/// <summary>
/// Reads lines from a huge text stream without ever holding more than one (capped) line: a runaway line with no line break
/// for megabytes is cut at <paramref name="maxLineChars"/> instead of filling memory. Handles CRLF and LF, and remembers
/// which one each line had (<see cref="LastLineEnding"/>) so a copy can keep the lines exactly as they were.
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maxLineChars = BoundedLineReader.DefaultMaxLineChars)
{
    public const int DefaultMaxLineChars = 32 * 1024;
    public const string CutMarker = " [line cut by the log bundle]";

    private readonly char[] _buffer = new char[16 * 1024];
    private readonly StringBuilder _line = new();
    private int _pos;
    private int _len;
    private char _lastChar;

    /// <summary>True when the last line returned was longer than the cap and was cut.</summary>
    public bool LastLineCut { get; private set; }

    /// <summary>How the last line returned ended in the source.</summary>
    public LineEnding LastLineEnding { get; private set; }

    /// <summary>The next line without its line ending, or null at the end of the stream.</summary>
    public string? ReadLine()
    {
        _line.Clear();
        LastLineCut = false;
        LastLineEnding = LineEnding.None;
        _lastChar = '\0';
        var any = false;
        while (true)
        {
            if (_pos >= _len)
            {
                _len = reader.Read(_buffer, 0, _buffer.Length);
                _pos = 0;
                if (_len <= 0)
                {
                    _len = 0;
                    return any ? Finish(null) : null;
                }
            }
            var chunk = _buffer.AsSpan(_pos, _len - _pos);
            var newline = chunk.IndexOf('\n');
            var take = newline < 0 ? chunk.Length : newline;
            any = true;
            if (newline >= 0 && _line.Length == 0 && take <= maxLineChars)
            {
                // Common case: the whole line is inside this buffer.
                _pos += newline + 1;
                var span = chunk[..take];
                LastLineEnding = LineEnding.Lf;
                if (span.Length > 0 && span[^1] == '\r')
                {
                    span = span[..^1];
                    LastLineEnding = LineEnding.CrLf;
                }
                return new string(span);
            }
            var crBeforeNewline = newline > 0 ? chunk[newline - 1] == '\r' : newline == 0 && _lastChar == '\r';
            var room = maxLineChars - _line.Length;
            if (take > room)
            {
                LastLineCut = true;
                take = Math.Max(0, room);
            }
            _line.Append(chunk[..take]);
            if (newline >= 0)
            {
                _pos += newline + 1;
                return Finish(crBeforeNewline);
            }
            _lastChar = chunk[^1];
            _pos = _len;
        }
    }

    /// <summary>Ends the line: <paramref name="crBeforeNewline"/> is null at the end of the stream (no line break).</summary>
    private string Finish(bool? crBeforeNewline)
    {
        var endsWithCr = crBeforeNewline == true;
        if (crBeforeNewline is not null && !LastLineCut && _line.Length > 0 && _line[^1] == '\r')
        {
            _line.Length--;
            endsWithCr = true;
        }
        LastLineEnding = crBeforeNewline is null ? LineEnding.None : endsWithCr ? LineEnding.CrLf : LineEnding.Lf;
        if (LastLineCut)
        {
            _line.Append(CutMarker);
        }
        return _line.ToString();
    }
}
