using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.Core.Profiles;

/// <summary>Detects a running KSP that uses a given game directory (Restart Manager first, process scan as fallback).</summary>
[SupportedOSPlatform("windows")]
public static class GameProcessGuard
{
    public static IReadOnlyList<int> FindRunning(string gameDir)
    {
        var exe = Path.Combine(gameDir, "KSP_x64.exe");
        var pids = new HashSet<int>();
        if (File.Exists(exe))
        {
            foreach (var pid in RestartManager.ProcessesUsing(exe))
            {
                // Seeded copies hard-link KSP_x64.exe, so Restart Manager also reports a KSP started from another copy.
                // Count it only when it really runs from this folder (or when its path cannot be read).
                if (ProcessPath(pid) is not { } path || ProfilePathValidator.IsInside(path, gameDir))
                {
                    pids.Add(pid);
                }
            }
        }
        foreach (var name in new[] { "KSP_x64", "KSP" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path is not null && ProfilePathValidator.IsInside(path, gameDir))
                    {
                        pids.Add(p.Id);
                    }
                }
                catch (Exception)
                {
                    // access denied for elevated processes; ignore
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return pids.ToList();
    }

    private static string? ProcessPath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null; // gone, or access denied for an elevated process
        }
    }

    public static void ThrowIfRunning(string gameDir)
    {
        var pids = FindRunning(gameDir);
        if (pids.Count > 0)
        {
            throw new LauncherException(ErrorCode.GameRunning, "Kerbal Space Program is running from this folder. Close it first.", RecoveryHint.CloseGame,
                details: pids.Select(p => $"pid {p}").ToList());
        }
    }

    private static class RestartManager
    {
        private const int CCH_RM_SESSION_KEY = 32;
        private const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, IntPtr rgApplications, uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

        public static IEnumerable<int> ProcessesUsing(string file)
        {
            var key = new string('\0', CCH_RM_SESSION_KEY + 1);
            if (RmStartSession(out var handle, 0, key) != 0)
            {
                yield break;
            }
            try
            {
                if (RmRegisterResources(handle, 1, new[] { file }, 0, IntPtr.Zero, 0, null) != 0)
                {
                    yield break;
                }
                uint needed = 0, count = 0, reasons = 0;
                var res = RmGetList(handle, out needed, ref count, null, ref reasons);
                if (res != ERROR_MORE_DATA || needed == 0)
                {
                    yield break;
                }
                var infos = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(handle, out needed, ref count, infos, ref reasons) != 0)
                {
                    yield break;
                }
                for (var i = 0; i < count; i++)
                {
                    yield return infos[i].Process.dwProcessId;
                }
            }
            finally
            {
                _ = RmEndSession(handle);
            }
        }
    }
}
