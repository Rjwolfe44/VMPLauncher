using System.Diagnostics;

namespace VmpLauncher.Game;

/// <summary>Starts KSP from a profile's game folder with the VMP auto-join argument.</summary>
public static class KspLauncher
{
    public sealed record LaunchSpec(string GameDir, string? JoinServerEntryName, bool BorderlessWindow, IReadOnlyList<string> ExtraArguments);

    public static ProcessStartInfo BuildStartInfo(LaunchSpec spec)
    {
        var exe = Path.Combine(spec.GameDir, "KSP_x64.exe");
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = spec.GameDir,
            UseShellExecute = false,
        };
        if (spec.JoinServerEntryName is not null)
        {
            // Safe on older VMP builds (unknown arguments are ignored). The password never travels here;
            // the mod looks the entry up in its own settings.xml.
            psi.ArgumentList.Add("-vmpjoin");
            psi.ArgumentList.Add(spec.JoinServerEntryName);
        }
        if (spec.BorderlessWindow)
        {
            psi.ArgumentList.Add("-popupwindow");
        }
        foreach (var a in spec.ExtraArguments)
        {
            psi.ArgumentList.Add(a);
        }
        return psi;
    }

    public static Process Launch(LaunchSpec spec)
    {
        var psi = BuildStartInfo(spec);
        if (!File.Exists(psi.FileName))
        {
            throw new FileNotFoundException("KSP_x64.exe not found in the profile.", psi.FileName);
        }
        return Process.Start(psi) ?? throw new InvalidOperationException("Windows did not start KSP.");
    }
}
