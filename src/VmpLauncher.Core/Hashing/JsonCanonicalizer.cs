using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VmpLauncher.Core.Hashing;

/// <summary>
/// RFC 8785 (JCS) style canonical JSON: object members sorted by UTF-16 code units, no insignificant whitespace,
/// shortest round-trip number formatting, minimal string escaping. Used so a module's metadata hash is stable
/// regardless of which serializer produced the text.
/// </summary>
public static class JsonCanonicalizer
{
    public static string Canonicalize(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip });
        var sb = new StringBuilder(json.Length);
        Write(doc.RootElement, sb);
        return sb.ToString();
    }

    public static string Canonicalize(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(element, sb);
        return sb.ToString();
    }

    public static string Sha256(string json) => Sha256Util.HashUtf8(Canonicalize(json));

    private static void Write(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var prop in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteString(prop.Name, sb);
                    sb.Append(':');
                    Write(prop.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }
                    firstItem = false;
                    Write(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(e.GetString()!, sb);
                break;
            case JsonValueKind.Number:
                WriteNumber(e, sb);
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteNumber(JsonElement e, StringBuilder sb)
    {
        var raw = e.GetRawText();
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            sb.Append(l.ToString(CultureInfo.InvariantCulture));
            return;
        }
        var d = e.GetDouble();
        if (double.IsFinite(d) && Math.Abs(d) < 1e21 && d == Math.Floor(d) && Math.Abs(d) < 9.007199254740992e15)
        {
            sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            return;
        }
        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
