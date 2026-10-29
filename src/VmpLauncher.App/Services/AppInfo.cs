using System.Reflection;

namespace VmpLauncher.App.Services;

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static bool IsWindows11 { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
}
