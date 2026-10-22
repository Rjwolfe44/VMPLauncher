using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.Core.Profiles;

/// <summary>
/// Exclusive mutation lock for one profile: an open file with FileShare.None and DeleteOnClose.
/// Holds {pid, operation, utc} so a second launcher/CLI can explain who owns it.
/// </summary>
public sealed class ProfileLock : IDisposable
{
    private readonly FileStream _stream;

    private ProfileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ProfileLock Acquire(ProfilePaths profile, string operation)
    {
        Directory.CreateDirectory(profile.StateDir);
        try
        {
            var fs = new FileStream(profile.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            var info = JsonSerializer.Serialize(new { pid = Environment.ProcessId, process = Process.GetCurrentProcess().ProcessName, operation, utc = DateTimeOffset.UtcNow });
            var bytes = Encoding.UTF8.GetBytes(info);
            fs.SetLength(0);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush();
            return new ProfileLock(fs);
        }
        catch (IOException ex)
        {
            throw new LauncherException(ErrorCode.ProfileBusy,
                "Another operation is already working on this profile" + DescribeOwner(profile.LockFile) + ".",
                RecoveryHint.Retry, ex);
        }
    }

    private static string DescribeOwner(string lockFile)
    {
        try
        {
            using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var json = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var op = doc.RootElement.TryGetProperty("operation", out var o) ? o.GetString() : null;
            var pid = doc.RootElement.TryGetProperty("pid", out var p) ? p.GetInt32() : 0;
            return op is null ? string.Empty : $" ({op}, process {pid})";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public void Dispose() => _stream.Dispose();
}
