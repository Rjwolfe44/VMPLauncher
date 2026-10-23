using System.Runtime.InteropServices;

namespace VmpLauncher.App.Views;

/// <summary>Dev helper for <c>--background</c>: puts a window fully inside the first non-primary monitor's work area.</summary>
internal static class SecondaryMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static void MoveTo(IntPtr hwnd)
    {
        Rect? work = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(m, ref info) && (info.Flags & 1) == 0)
            {
                work = info.Work;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (work is not { } w || !GetWindowRect(hwnd, out var r))
        {
            return;
        }
        var width = Math.Min(r.Right - r.Left, w.Right - w.Left - 32);
        var height = Math.Min(r.Bottom - r.Top, w.Bottom - w.Top - 32);
        SetWindowPos(hwnd, IntPtr.Zero, w.Left + 16, w.Top + 16, width, height, SwpNoZOrder | SwpNoActivate);
    }
}
