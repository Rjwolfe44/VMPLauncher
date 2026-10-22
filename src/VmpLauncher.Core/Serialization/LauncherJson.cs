using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmpLauncher.Core.Serialization;

/// <summary>Shared JSON settings for every launcher-authored file (pack, lock, inventory, state).</summary>
public static class LauncherJson
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Compact = new(Pretty) { WriteIndented = false };

    /// <summary>Maximum accepted size of an untrusted lock or pack file.</summary>
    public const long MaxLockBytes = 32L * 1024 * 1024;

    /// <summary>Maximum accepted decompressed size of an untrusted inventory.</summary>
    public const long MaxInventoryBytes = 64L * 1024 * 1024;

    public static T ReadFile<T>(string path, long maxBytes = MaxLockBytes)
    {
        var fi = new FileInfo(path);
        if (fi.Length > maxBytes)
        {
            throw new InvalidDataException($"{path} is {fi.Length} bytes; the limit is {maxBytes}.");
        }
        using var fs = fi.OpenRead();
        return JsonSerializer.Deserialize<T>(fs, Pretty) ?? throw new InvalidDataException($"{path} deserialized to null.");
    }

    /// <summary>Writes atomically (temp file + replace) with LF line endings and no BOM.</summary>
    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = JsonSerializer.Serialize(value, Pretty).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static byte[] SerializeBytes<T>(T value)
        => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Pretty).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
}
