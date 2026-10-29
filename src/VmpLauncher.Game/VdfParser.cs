using System.Text;

namespace VmpLauncher.Game;

/// <summary>Minimal parser for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf).</summary>
public static class VdfParser
{
    public sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Value { get; set; }

        public Node? this[string key] => Children.TryGetValue(key, out var n) ? n : null;
        public string? GetString(params string[] path)
        {
            var n = this;
            foreach (var p in path)
            {
                n = n?[p];
            }
            return n?.Value;
        }
    }

    public static Node Parse(string text)
    {
        var root = new Node();
        var stack = new Stack<Node>();
        stack.Push(root);
        var i = 0;
        string? pendingKey = null;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (c == '{')
            {
                var child = new Node();
                if (pendingKey is not null)
                {
                    stack.Peek().Children[pendingKey] = child;
                    pendingKey = null;
                }
                stack.Push(child);
                i++;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 1)
                {
                    stack.Pop();
                }
                i++;
                continue;
            }
            var token = ReadToken(text, ref i);
            if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Children[pendingKey] = new Node { Value = token };
                pendingKey = null;
            }
        }
        return root;
    }

    private static string ReadToken(string text, ref int i)
    {
        var sb = new StringBuilder();
        if (text[i] == '"')
        {
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    i++;
                    sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', var o => o });
                }
                else
                {
                    sb.Append(text[i]);
                }
                i++;
            }
            i++; // closing quote
            return sb.ToString();
        }
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}')
        {
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }
}
