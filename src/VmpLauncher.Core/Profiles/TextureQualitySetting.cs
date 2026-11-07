using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VmpLauncher.Core.Profiles;

/// <summary>
/// What the launcher does with KSP's <c>TEXTURE_QUALITY</c> (0 Full, 1 Half, 2 Quarter, 3 Eighth resolution) in a
/// profile's <c>settings.cfg</c>. Lower texture quality is the biggest single memory saving KSP has.
/// </summary>
public enum TextureQualityPolicy
{
    /// <summary>Make Half the default once per profile (Full becomes Half the first time); never touch it again afterwards.</summary>
    Auto,
    /// <summary>Do not touch settings.cfg.</summary>
    Leave,
    Full,
    Half,
    Quarter,
}

public enum TextureQualityStatus
{
    /// <summary>The file was rewritten with the new value.</summary>
    Changed,
    /// <summary>The file already had what the policy wants.</summary>
    Unchanged,
    /// <summary>The policy asked for nothing (Leave, or Auto that already ran for this profile).</summary>
    Left,
    /// <summary>There is no settings.cfg yet (KSP writes it on its first exit); applied at the next launch.</summary>
    NoFile,
    KspRunning,
    /// <summary>The file is not one the launcher edits (unusual encoding, odd value, no place for the key).</summary>
    Unsupported,
    ReadOnly,
    Failed,
}

/// <param name="RecordAutoDone">Auto looked at a real settings.cfg and decided; the caller records it so Auto never runs again for this profile.</param>
/// <param name="Message">One log line saying what was read and what was written.</param>
public sealed record TextureQualityOutcome(TextureQualityStatus Status, int? Before, int? After, bool RecordAutoDone, string Message)
{
    public bool Changed => Status == TextureQualityStatus.Changed;
}

public static class TextureQualitySetting
{
    public const string Key = "TEXTURE_QUALITY";
    public const string FileName = "settings.cfg";
    public const string BackupSuffix = ".vmpl-backup";
    public const string TempSuffix = ".vmpl-tmp";

    public static string ValueName(int value) => value switch
    {
        0 => "Full",
        1 => "Half",
        2 => "Quarter",
        3 => "Eighth",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    public static string ToId(TextureQualityPolicy policy) => policy.ToString().ToLowerInvariant();

    /// <summary>Reads the stored id; anything unknown is Auto.</summary>
    public static TextureQualityPolicy FromId(string? id)
        => Enum.TryParse<TextureQualityPolicy>(id, ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : TextureQualityPolicy.Auto;

    private static int? Explicit(TextureQualityPolicy p) => p switch
    {
        TextureQualityPolicy.Full => 0,
        TextureQualityPolicy.Half => 1,
        TextureQualityPolicy.Quarter => 2,
        _ => null,
    };

    /// <summary>
    /// Applies <paramref name="policy"/> to the settings.cfg in <paramref name="gameDir"/>. Never throws: every failure comes
    /// back as a status and a log line, so the game still starts. Does nothing while KSP runs from that folder, because
    /// KSP rewrites the whole file when it exits.
    /// </summary>
    /// <param name="autoAlreadyDone">Auto already ran for this profile.</param>
    /// <param name="isRunning">True when KSP runs from the folder (default: the real process check).</param>
    public static TextureQualityOutcome Apply(string gameDir, TextureQualityPolicy policy, bool autoAlreadyDone, Func<string, bool>? isRunning = null)
    {
        try
        {
            if (policy == TextureQualityPolicy.Leave)
            {
                return new(TextureQualityStatus.Left, null, null, false, "Texture quality: policy is \"Leave as it is\"; settings.cfg not touched.");
            }
            if (policy == TextureQualityPolicy.Auto && autoAlreadyDone)
            {
                return new(TextureQualityStatus.Left, null, null, false, "Texture quality: Auto already ran for this profile; settings.cfg not touched.");
            }
            var path = Path.Combine(gameDir, FileName);
            if (!File.Exists(path))
            {
                return new(TextureQualityStatus.NoFile, null, null, false, "Texture quality: no settings.cfg yet (KSP writes it when it first exits); it is applied at the next launch.");
            }
            if (IsRunning(gameDir, isRunning))
            {
                return new(TextureQualityStatus.KspRunning, null, null, false, "Texture quality: KSP is running from this profile, so settings.cfg is left alone (KSP rewrites it on exit).");
            }

            var bytes = File.ReadAllBytes(path);
            if (!TryParse(bytes, out var doc))
            {
                return new(TextureQualityStatus.Unsupported, null, null, false, "Texture quality: settings.cfg is in an encoding the launcher does not edit; left alone.");
            }
            var current = doc.Values;
            var summary = current.Count == 0 ? "missing (KSP default: Full)" : string.Join("/", current.Select(v => v is { } n ? $"{n} ({ValueName(n)})" : "unreadable"));
            if (current.Any(v => v is null))
            {
                return new(TextureQualityStatus.Unsupported, null, null, false, $"Texture quality: read {Key} = {summary}; not a value the launcher edits, left alone.");
            }
            var before = current.Count == 0 ? 0 : current[0];

            int target;
            if (policy == TextureQualityPolicy.Auto)
            {
                // Full (or no key, which KSP reads as Full) becomes Half. Anything the player already lowered stays.
                if (current.Count > 0 && current.Any(v => v != 0))
                {
                    return new(TextureQualityStatus.Unchanged, before, before, true, $"Texture quality: read {Key} = {summary}; Auto leaves a lowered value as it is.");
                }
                target = 1;
            }
            else
            {
                target = Explicit(policy)!.Value;
                if (current.Count > 0 && current.All(v => v == target))
                {
                    return new(TextureQualityStatus.Unchanged, before, target, false, $"Texture quality: read {Key} = {summary}; already what the setting asks for.");
                }
            }

            var edited = doc.WithValue(target);
            if (edited is null)
            {
                return new(TextureQualityStatus.Unsupported, before, null, false, $"Texture quality: read {Key} = {summary}; settings.cfg has no place the launcher can safely add the key; left alone.");
            }
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                return new(TextureQualityStatus.ReadOnly, before, null, false, $"Texture quality: read {Key} = {summary}; settings.cfg is read-only, so it was not changed.");
            }
            WriteAtomically(path, edited);
            return new(TextureQualityStatus.Changed, before, target, policy == TextureQualityPolicy.Auto,
                $"Texture quality: read {Key} = {summary}; wrote {target} ({ValueName(target)}) to settings.cfg (policy {ToId(policy)}). Takes effect at the next game start.");
        }
        catch (Exception ex)
        {
            return new(TextureQualityStatus.Failed, null, null, false, $"Texture quality: could not update settings.cfg ({ex.GetType().Name}: {ex.Message}); the game starts with it as it is.");
        }
    }

    private static bool IsRunning(string gameDir, Func<string, bool>? isRunning)
    {
        if (isRunning is not null)
        {
            return isRunning(gameDir);
        }
        return OperatingSystem.IsWindows() && GameProcessGuard.FindRunning(gameDir).Count > 0;
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        var backup = path + BackupSuffix;
        if (!File.Exists(backup))
        {
            File.Copy(path, backup);
        }
        var temp = path + TempSuffix;
        try
        {
            File.WriteAllBytes(temp, content);
            File.Replace(temp, path, null);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception)
            {
                // The original is intact; a stray temp file is overwritten next time.
            }
            throw;
        }
    }

    // ---- parsing and editing (bytes are mapped 1:1 to chars, so every byte outside the edited line survives) ----

    private static readonly Regex KeyLine = new(@"^(?<pre>(?:ï»¿)?[ \t]*TEXTURE_QUALITY[ \t]*=[ \t]*)(?<val>[^\s/]*)(?<post>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed class Doc
    {
        public required List<(string Text, string Eol)> Lines { get; init; }
        /// <summary>Line indexes of the top-level key, in file order.</summary>
        public required List<int> KeyLines { get; init; }
        /// <summary>Where a missing key goes (after the last top-level setting before the first section), or -1.</summary>
        public required int InsertAfter { get; init; }
        public required string DominantEol { get; init; }
        public required string[] KeyValues { get; init; }

        /// <summary>One entry per occurrence; null when the value is not 0 to 3. Empty when the key is missing.</summary>
        public List<int?> Values => KeyValues.Select(v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 3 ? (int?)n : null).ToList();

        public byte[]? WithValue(int value)
        {
            var lines = new List<(string Text, string Eol)>(Lines);
            var text = value.ToString(CultureInfo.InvariantCulture);
            if (KeyLines.Count > 0)
            {
                foreach (var i in KeyLines)
                {
                    var m = KeyLine.Match(lines[i].Text);
                    var val = m.Groups["val"];
                    lines[i] = (lines[i].Text[..val.Index] + text + lines[i].Text[(val.Index + val.Length)..], lines[i].Eol);
                }
            }
            else
            {
                if (InsertAfter < 0)
                {
                    return null;
                }
                var anchor = lines[InsertAfter];
                var newLine = (Text: $"{Key} = {text}", Eol: anchor.Eol);
                if (anchor.Eol.Length == 0)
                {
                    lines[InsertAfter] = (anchor.Text, DominantEol);
                }
                lines.Insert(InsertAfter + 1, newLine);
            }
            var sb = new StringBuilder();
            foreach (var (t, e) in lines)
            {
                sb.Append(t).Append(e);
            }
            return Encoding.Latin1.GetBytes(sb.ToString());
        }
    }

    internal static bool TryParse(byte[] bytes, out Doc doc)
    {
        doc = null!;
        // UTF-16 files and anything with NULs are not what KSP writes; do not guess.
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return false;
        }
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }
        var s = Encoding.Latin1.GetString(bytes);
        var lines = new List<(string Text, string Eol)>();
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\r' || s[i] == '\n')
            {
                var eolLen = s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n' ? 2 : 1;
                lines.Add((s[start..i], s.Substring(i, eolLen)));
                i += eolLen - 1;
                start = i + 1;
            }
        }
        if (start < s.Length)
        {
            lines.Add((s[start..], string.Empty));
        }

        var keyLines = new List<int>();
        var values = new List<string>();
        var depth = 0;
        var seenSection = false;
        var insertAfter = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            var t = text.Trim().TrimStart('ï', '»', '¿');
            var code = t.Contains("//") ? t[..t.IndexOf("//", StringComparison.Ordinal)].Trim() : t;
            if (depth == 0 && code.Length > 0)
            {
                var m = KeyLine.Match(text);
                if (m.Success)
                {
                    keyLines.Add(i);
                    values.Add(m.Groups["val"].Value);
                }
                if (!seenSection && code.Contains('=') && !code.Contains('{'))
                {
                    insertAfter = i;
                }
                else if (!code.Contains('=') && code != "{" && code != "}")
                {
                    seenSection = true; // a section header such as INPUT_DEVICES
                }
                else if (code.Contains('{'))
                {
                    seenSection = true;
                }
            }
            foreach (var c in code)
            {
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
            }
        }
        var crlf = lines.Count(l => l.Eol == "\r\n");
        var lf = lines.Count(l => l.Eol == "\n");
        doc = new Doc
        {
            Lines = lines,
            KeyLines = keyLines,
            InsertAfter = insertAfter,
            DominantEol = crlf >= lf && crlf > 0 ? "\r\n" : lf > 0 ? "\n" : "\r\n",
            KeyValues = values.ToArray(),
        };
        return true;
    }
}
