using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VmpLauncher.Core.ModInfo;

/// <summary>A display block produced by <see cref="MarkdownLite.Parse"/>.</summary>
public abstract record MdBlock;

/// <summary>A heading; <paramref name="Level"/> is 1 to 6.</summary>
public sealed record MdHeading(int Level, string Text) : MdBlock;

/// <summary>A paragraph of plain text (inline formatting already flattened).</summary>
public sealed record MdParagraph(string Text) : MdBlock;

/// <summary>One bullet or numbered list item (nesting is flattened).</summary>
public sealed record MdListItem(string Text) : MdBlock;

/// <summary>An image on its own line; <paramref name="Url"/> is always https.</summary>
public sealed record MdImage(Uri Url, string? Alt) : MdBlock;

/// <summary>A horizontal rule.</summary>
public sealed record MdRule : MdBlock;

/// <summary>
/// A tiny, dependency-free reader for the Markdown (plus a little raw HTML) that SpaceDock authors write. It keeps only
/// what a native page needs: headings, paragraphs, list items, images and rules. Inline formatting is flattened to plain
/// text, HTML tags are dropped, and only https images survive. Input and output sizes are capped so hostile text cannot
/// stall the UI.
/// </summary>
public static partial class MarkdownLite
{
    private const int MaxBlocks = 200;
    private const int MaxInput = 200_000;
    private const int MaxBlockText = 20_000;

    // Private-use characters stand in for stashed spans while the inline passes run, so a code span or an escaped "*"
    // can never be mistaken for emphasis. Real input never contains them (they are stripped first).
    private const char ImageOpen = '\uE000';
    private const char ImageClose = '\uE001';
    private const char StashOpen = '\uE002';
    private const char StashClose = '\uE003';

    /// <summary>Splits <paramref name="markdown"/> into display blocks (at most 200). Null or blank input gives an empty list.</summary>
    public static IReadOnlyList<MdBlock> Parse(string? markdown) => ParseCore(markdown, MaxBlocks);

    /// <summary>Every https image URL in the text, in order, without duplicates.</summary>
    public static IReadOnlyList<Uri> Images(string? markdown)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var urls = new List<Uri>();
        foreach (var block in ParseCore(markdown, int.MaxValue))
        {
            if (block is MdImage image && seen.Add(image.Url.AbsoluteUri))
            {
                urls.Add(image.Url);
            }
        }
        return urls;
    }

    /// <summary>
    /// The text on one line for short summaries: headings, paragraphs and list items joined by spaces, images dropped.
    /// Longer text is cut (at a word when possible) and ends with an ellipsis, so the result never exceeds <paramref name="maxLength"/>.
    /// </summary>
    public static string ToPlainText(string? markdown, int maxLength = int.MaxValue)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var block in ParseCore(markdown, MaxBlocks))
        {
            var text = block switch
            {
                MdHeading h => h.Text,
                MdParagraph p => p.Text,
                MdListItem l => l.Text,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(text);
            if (sb.Length > maxLength)
            {
                break;
            }
        }
        return Truncate(Whitespace().Replace(sb.ToString(), " ").Trim(), maxLength);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }
        if (maxLength == 1)
        {
            return "\u2026";
        }
        var cut = maxLength - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }
        var head = text[..cut];
        var space = head.LastIndexOf(' ');
        if (space > cut / 2 && !char.IsWhiteSpace(text[cut]))
        {
            head = head[..space];
        }
        return head.TrimEnd() + "\u2026";
    }

    private static List<MdBlock> ParseCore(string? markdown, int maxBlocks)
    {
        if (string.IsNullOrWhiteSpace(markdown) || maxBlocks <= 0)
        {
            return new List<MdBlock>();
        }
        var text = markdown.Length > MaxInput ? markdown[..MaxInput] : markdown;
        if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
        {
            text = text[..^1];
        }
        text = Sentinels().Replace(text, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return new Builder(maxBlocks).Run(text);
    }

    /// <summary>Line-by-line block parser. One pending paragraph or list item at a time is accumulated and flushed on a boundary.</summary>
    private sealed class Builder(int maxBlocks)
    {
        private enum Kind { None, Paragraph, ListItem }

        private readonly List<MdBlock> _blocks = new();
        private readonly StringBuilder _pending = new();
        private Kind _kind;

        private bool Full => _blocks.Count >= maxBlocks;

        public List<MdBlock> Run(string text)
        {
            string? fence = null;
            var code = new StringBuilder();
            var firstCodeLine = true;
            foreach (var raw in text.Split('\n'))
            {
                if (Full)
                {
                    break;
                }
                if (fence is not null)
                {
                    if (raw.AsSpan().TrimStart().StartsWith(fence, StringComparison.Ordinal))
                    {
                        EmitCode(code);
                        fence = null;
                    }
                    else if (code.Length < MaxBlockText)
                    {
                        if (!firstCodeLine)
                        {
                            code.Append('\n');
                        }
                        code.Append(raw);
                        firstCodeLine = false;
                    }
                    continue;
                }
                if (TryOpenFence(raw.Trim(), out var marker))
                {
                    Flush();
                    fence = marker;
                    firstCodeLine = true;
                    code.Clear();
                    continue;
                }
                foreach (var line in ExpandHtml(raw).Split('\n'))
                {
                    if (Full)
                    {
                        break;
                    }
                    Line(line);
                }
            }
            if (fence is not null)
            {
                EmitCode(code);
            }
            Flush();
            return _blocks;
        }

        private static bool TryOpenFence(string trimmed, out string marker)
        {
            marker = string.Empty;
            if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
            {
                return false;
            }
            var run = 0;
            while (run < trimmed.Length && trimmed[run] == trimmed[0])
            {
                run++;
            }
            // "```code```" on one line is an inline code span, not a fence (a backtick fence's info string has no backticks).
            if (run < 3 || (trimmed[0] == '`' && trimmed.IndexOf('`', run) >= 0))
            {
                return false;
            }
            marker = new string(trimmed[0], run);
            return true;
        }

        private void EmitCode(StringBuilder code)
        {
            var text = code.ToString().TrimEnd();
            code.Clear();
            if (text.Length > 0)
            {
                Add(new MdParagraph(text));
            }
        }

        private void Line(string line)
        {
            var s = line.Trim();
            while (s.Length > 0 && s[0] == '>')
            {
                s = s[1..].TrimStart();
            }
            if (s.Length == 0)
            {
                Flush();
                return;
            }
            if (LinkDefinition().IsMatch(s))
            {
                return;
            }
            if (s[0] == '#')
            {
                Flush();
                var level = 0;
                while (level < s.Length && s[level] == '#')
                {
                    level++;
                }
                var heading = Math.Min(level, 6);
                Emit(t => new MdHeading(heading, t), HeadingClose().Replace(s[level..], string.Empty).Trim());
                return;
            }
            if (Rule().IsMatch(s))
            {
                Flush();
                Add(new MdRule());
                return;
            }
            if (_kind == Kind.Paragraph && s.Length >= 3 && s.All(c => c == '='))
            {
                // "Title" over "=====" is a level-1 heading.
                var title = _pending.ToString();
                _pending.Clear();
                _kind = Kind.None;
                Emit(t => new MdHeading(1, t), title);
                return;
            }
            var item = ListItem().Match(s);
            if (item.Success)
            {
                Flush();
                _kind = Kind.ListItem;
                Append(item.Groups["t"].Value);
                return;
            }
            if (_kind == Kind.None)
            {
                _kind = Kind.Paragraph;
            }
            Append(s);
        }

        private void Append(string text)
        {
            var room = MaxBlockText - _pending.Length;
            if (room <= 0)
            {
                return;
            }
            if (_pending.Length > 0)
            {
                _pending.Append(' ');
            }
            _pending.Append(text.Length > room ? text[..room] : text);
        }

        private void Flush()
        {
            if (_kind == Kind.None)
            {
                return;
            }
            var text = _pending.ToString();
            var kind = _kind;
            _pending.Clear();
            _kind = Kind.None;
            if (kind == Kind.ListItem)
            {
                Emit(t => new MdListItem(t), text);
            }
            else
            {
                Emit(t => new MdParagraph(t), text);
            }
        }

        /// <summary>Flattens one chunk of inline text; images split it, each part becoming its own block in order.</summary>
        private void Emit(Func<string, MdBlock> make, string raw)
        {
            foreach (var segment in Segments(raw.Length > MaxBlockText ? raw[..MaxBlockText] : raw))
            {
                if (segment.Url is not null)
                {
                    Add(new MdImage(segment.Url, segment.Alt));
                }
                else if (segment.Text is { Length: > 0 } text)
                {
                    Add(make(text));
                }
            }
        }

        private void Add(MdBlock block)
        {
            if (!Full)
            {
                _blocks.Add(block);
            }
        }
    }

    private readonly record struct Segment(string? Text, Uri? Url, string? Alt);

    /// <summary>Raw HTML block tags become line breaks first so "&lt;p&gt;a&lt;/p&gt;&lt;p&gt;b&lt;/p&gt;" reads as two paragraphs.</summary>
    private static string ExpandHtml(string line)
    {
        // Lines holding a backtick may be quoting a tag in a code span; leave those alone.
        if (line.IndexOf('<', StringComparison.Ordinal) < 0 || line.Contains('`', StringComparison.Ordinal))
        {
            return line;
        }
        var s = HtmlHeadingOpen().Replace(line, m => "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " ");
        s = HtmlBlockTag().Replace(s, "\n\n");
        s = HtmlBreak().Replace(s, "\n");
        s = HtmlRule().Replace(s, "\n\n---\n\n");
        s = HtmlListOpen().Replace(s, "\n- ");
        return HtmlListClose().Replace(s, "\n");
    }

    private static List<Segment> Segments(string raw)
    {
        var stash = new List<string>();
        var images = new List<(Uri Url, string? Alt)>();

        string Stash(string literal)
        {
            stash.Add(literal);
            return StashOpen + (stash.Count - 1).ToString(CultureInfo.InvariantCulture) + StashClose;
        }

        string Restore(string text) => StashMarker().Replace(text, m =>
            int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < stash.Count ? stash[i] : string.Empty);

        string? Alt(string? alt)
        {
            if (string.IsNullOrWhiteSpace(alt))
            {
                return null;
            }
            var clean = Whitespace().Replace(WebUtility.HtmlDecode(Restore(HtmlTag().Replace(alt, string.Empty))), " ").Trim();
            return clean.Length == 0 ? null : clean;
        }

        var s = raw;
        try
        {
            s = CodeSpan().Replace(s, m => Stash(m.Groups["code"].Value.Trim()));
            s = Escape().Replace(s, m => Stash(m.Groups[1].Value));
            s = ImageOrImgTag().Replace(s, m =>
            {
                string? url;
                string? alt;
                if (m.Groups["tag"].Success)
                {
                    var tag = m.Groups["tag"].Value;
                    url = SrcAttribute().Match(tag) is { Success: true } src ? src.Groups["v"].Value : null;
                    alt = AltAttribute().Match(tag) is { Success: true } a ? a.Groups["v"].Value : null;
                }
                else
                {
                    url = m.Groups["url"].Value;
                    alt = m.Groups["alt"].Value;
                }
                if (url is null || !TryHttpsImage(url, out var uri))
                {
                    return string.Empty;
                }
                images.Add((uri, Alt(alt)));
                return ImageOpen + (images.Count - 1).ToString(CultureInfo.InvariantCulture) + ImageClose;
            });
            s = AutoLink().Replace(s, "$1");
            s = Link().Replace(s, "${t}");
            s = ReferenceLink().Replace(s, "${t}");
            s = HtmlTag().Replace(s, string.Empty);
            s = Strike().Replace(s, "${t}");
            s = BoldStar().Replace(s, "${t}");
            s = BoldUnderscore().Replace(s, "${t}");
            s = ItalicStar().Replace(s, "${t}");
            s = ItalicUnderscore().Replace(s, "${t}");
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: fall through with whatever the passes managed, still stripped of markers below.
        }
        s = WebUtility.HtmlDecode(s);

        var segments = new List<Segment>();
        void AddText(string piece)
        {
            var text = Whitespace().Replace(Restore(piece), " ").Trim();
            if (text.Length > 0)
            {
                segments.Add(new Segment(text, null, null));
            }
        }

        var pos = 0;
        foreach (Match m in ImageMarker().Matches(s))
        {
            AddText(s[pos..m.Index]);
            if (int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < images.Count)
            {
                segments.Add(new Segment(null, images[i].Url, images[i].Alt));
            }
            pos = m.Index + m.Length;
        }
        AddText(s[pos..]);
        return segments;
    }

    private static bool TryHttpsImage(string raw, out Uri uri)
    {
        var s = WebUtility.HtmlDecode(raw).Trim();
        if (s.Length > 2 && s[0] == '<' && s[^1] == '>')
        {
            s = s[1..^1].Trim();
        }
        if (s.StartsWith("//", StringComparison.Ordinal))
        {
            s = "https:" + s;
        }
        if (Uri.TryCreate(s, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && parsed.Host.Length > 0
            && parsed.UserInfo.Length == 0)
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    [GeneratedRegex("[\uE000-\uE003]", RegexOptions.CultureInvariant)]
    private static partial Regex Sentinels();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Whitespace();

    // --- block level ---

    [GeneratedRegex("""^(?:(?:-\s*){3,}|(?:\*\s*){3,}|(?:_\s*){3,})$""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Rule();

    [GeneratedRegex("""^(?:[-*+]|\d{1,9}[.)])\s+(?<t>.*)$""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ListItem();

    [GeneratedRegex("""\s+#+\s*$""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HeadingClose();

    // "[ref]: https://example.com "title"" — a reference-link definition, never shown.
    [GeneratedRegex("""^\[[^\]\n]+\]:\s*<?(?:https?://|//|/|#|mailto:)\S*>?(?:\s+(?:"[^"]*"|'[^']*'|\([^)]*\)))?$""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex LinkDefinition();

    [GeneratedRegex("""<h([1-6])\b[^<>]*>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlHeadingOpen();

    [GeneratedRegex("""</?(?:p|div|ul|ol|table|thead|tbody|tr|blockquote|section|article|pre|center|details|summary|h[1-6])\b[^<>]*>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlBlockTag();

    [GeneratedRegex("""<br\b[^<>]*>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex("""<hr\b[^<>]*>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlRule();

    [GeneratedRegex("""<li\b[^<>]*>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlListOpen();

    [GeneratedRegex("""</li\s*>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlListClose();

    // --- inline ---

    // `code` / ``code with ` inside``
    [GeneratedRegex("""(?<!`)(`+)(?!`)(?<code>[^\n]{1,1000}?)(?<!`)\1(?!`)""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CodeSpan();

    // A backslash before ASCII punctuation keeps the punctuation literal.
    [GeneratedRegex("""\\([!-/:-@\[-`{-~])""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Escape();

    // ![alt](url "title") or <img ... src="url" ...>
    [GeneratedRegex("""!\[(?<alt>[^\]\n]{0,500})\]\(\s*(?<url><[^>\n]{1,2048}>|(?:[^()\s]|\([^()\s]*\)){1,2048})(?:\s+(?:"[^"\n]*"|'[^'\n]*'))?\s*\)|<img\b(?<tag>[^>]{0,4000})>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ImageOrImgTag();

    [GeneratedRegex("""(?<![\w:-])src\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s"'>]+))""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SrcAttribute();

    [GeneratedRegex("""(?<![\w:-])alt\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s"'>]+))""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AltAttribute();

    [GeneratedRegex("""<((?:https?|ftp)://[^>\s]+|mailto:[^>\s]+)>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AutoLink();

    [GeneratedRegex("""\[(?<t>[^\]\n]{0,1000})\]\(\s*(?:<[^>\n]*>|(?:[^()\s]|\([^()\s]*\))*)(?:\s+(?:"[^"\n]*"|'[^'\n]*'))?\s*\)""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Link();

    [GeneratedRegex("""\[(?<t>[^\]\n]{1,1000})\]\[[^\]\n]*\]""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ReferenceLink();

    [GeneratedRegex("""<!--[\s\S]*?-->|</?[A-Za-z][^<>]{0,2000}>""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HtmlTag();

    [GeneratedRegex("""~~(?=\S)(?<t>[^\n]{1,1000}?)(?<=\S)~~""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Strike();

    [GeneratedRegex("""\*\*(?=\S)(?<t>[^\n]{1,1000}?)(?<=\S)\*\*""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BoldStar();

    // Underscore emphasis never fires inside a word, so snake_case_names survive.
    [GeneratedRegex("""(?<![\p{L}\p{N}_])__(?=\S)(?<t>[^\n]{1,1000}?)(?<=\S)__(?![\p{L}\p{N}_])""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BoldUnderscore();

    [GeneratedRegex("""\*(?=[^\s*])(?<t>[^\n*]{1,1000}?)(?<=[^\s*])\*""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ItalicStar();

    [GeneratedRegex("""(?<![\p{L}\p{N}_])_(?=[^\s_])(?<t>[^\n_]{1,1000}?)(?<=[^\s_])_(?![\p{L}\p{N}_])""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ItalicUnderscore();

    [GeneratedRegex("\uE000(\\d+)\uE001", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ImageMarker();

    [GeneratedRegex("\uE002(\\d+)\uE003", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StashMarker();
}
