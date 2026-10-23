using System.Diagnostics;
using System.Text;
using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.Publishing.Publish;

/// <summary>Thin wrappers over git, gh, scp and ssh. Every call is logged verbatim so the dry run shows exactly what --execute will run.</summary>
public sealed class ExternalTools
{
    private readonly TextWriter _log;

    public ExternalTools(TextWriter log)
    {
        _log = log;
    }

    public sealed record RunResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static string Describe(string exe, IEnumerable<string> args) => exe + " " + string.Join(" ", args.Select(Quote));

    public RunResult Run(string exe, IReadOnlyList<string> args, string? workingDir = null, string? stdin = null, bool throwOnError = true, TimeSpan? timeout = null)
    {
        _log.WriteLine($"  $ {Describe(exe, args)}");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new LauncherException(ErrorCode.Unknown, $"Could not start {exe}.", RecoveryHint.None);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); _log.WriteLine("    " + e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); _log.WriteLine("    " + e.Data); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(10)).TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new LauncherException(ErrorCode.Unknown, $"{exe} did not finish within {timeout ?? TimeSpan.FromMinutes(10)}.", RecoveryHint.None);
        }
        p.WaitForExit();
        var result = new RunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
        if (throwOnError && !result.Ok)
        {
            throw new LauncherException(ErrorCode.Unknown, $"{exe} {(args.Count > 0 ? args[0] : string.Empty)} failed with exit code {p.ExitCode}.", RecoveryHint.None, details: new[] { result.StdErr.Trim(), result.StdOut.Trim() }.Where(s => s.Length > 0).ToList());
        }
        return result;
    }

    public RunResult Git(string repoDir, params string[] args) => Run("git", new[] { "-C", repoDir }.Concat(args).ToList());

    /// <summary>Run a PowerShell script on an SSH host as an encoded command (no quoting hazards, no forwardings).</summary>
    public RunResult SshPowerShell(string host, string script, bool throwOnError = true)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return Run("ssh", new[] { "-T", "-o", "BatchMode=yes", "-o", "ClearAllForwardings=yes", host, "powershell.exe", "-NoProfile", "-EncodedCommand", encoded }, throwOnError: throwOnError, timeout: TimeSpan.FromMinutes(5));
    }

    public RunResult Scp(string localFile, string host, string remotePath)
        => Run("scp", new[] { "-o", "BatchMode=yes", "-o", "ClearAllForwardings=yes", localFile, $"{host}:{remotePath}" }, timeout: TimeSpan.FromMinutes(5));

    private static string Quote(string a) => a.Length > 0 && a.All(c => !char.IsWhiteSpace(c) && c != '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
