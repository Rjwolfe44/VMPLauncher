using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VmpLauncher.Core;

namespace VmpLauncher.App.Services;

/// <summary>
/// Per-user secrets (server passwords) encrypted with DPAPI for the current Windows user. Values never appear in
/// settings.json, logs or diagnostics. The VMP mod itself still needs the password in its own settings.xml.
/// </summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string? value);
    bool Has(string key);
}

public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VmpLauncher.secrets.v1");
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _values;

    public DpapiSecretStore(LauncherPaths paths)
    {
        _path = paths.SecretsFile;
        _values = Load();
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) ? v : null;
        }
    }

    public bool Has(string key) => !string.IsNullOrEmpty(Get(key));

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(value))
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path) || !OperatingSystem.IsWindows())
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var plain = JsonSerializer.SerializeToUtf8Bytes(_values);
        var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, _path, overwrite: true);
    }
}
