using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VmpLauncher.App.Services;

/// <summary>
/// Per-user registration of the <c>vmpack://</c> URL protocol and the <c>.vmpack</c> file type
/// (never <c>.vmpack.json</c>: Windows keys on the last extension and that would hijack .json).
/// Runs from Velopack's install/update hooks, so it has to be fast and must never throw.
/// </summary>
public static class ShellRegistration
{
    private const string ProgId = "VmpLauncher.Pack";

    public static void TryRegister()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is null)
            {
                return;
            }
            // Prefer Velopack's stable root stub (…\VmpLauncher\VmpLauncher.exe) so links survive updates.
            var current = Path.GetDirectoryName(exe)!;
            var parent = Path.GetDirectoryName(current);
            if (parent is not null && Path.GetFileName(current).StartsWith("current", StringComparison.OrdinalIgnoreCase))
            {
                var stub = Path.Combine(parent, Path.GetFileName(exe));
                if (File.Exists(stub))
                {
                    exe = stub;
                }
            }
            var command = $"\"{exe}\" \"%1\"";

            using (var proto = Registry.CurrentUser.CreateSubKey(@"Software\Classes\vmpack"))
            {
                proto.SetValue(string.Empty, "URL:VMP Pack");
                proto.SetValue("URL Protocol", string.Empty);
                using var icon = proto.CreateSubKey("DefaultIcon");
                icon.SetValue(string.Empty, $"\"{exe}\",0");
                using var cmd = proto.CreateSubKey(@"shell\open\command");
                cmd.SetValue(string.Empty, command);
            }
            using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.vmpack"))
            {
                ext.SetValue(string.Empty, ProgId);
            }
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue(string.Empty, "VMP Launcher pack");
                using var icon = prog.CreateSubKey("DefaultIcon");
                icon.SetValue(string.Empty, $"\"{exe}\",0");
                using var cmd = prog.CreateSubKey(@"shell\open\command");
                cmd.SetValue(string.Empty, command);
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // Registration is a convenience; never fail install over it.
        }
    }

    public static void TryUnregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\vmpack", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.vmpack", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
