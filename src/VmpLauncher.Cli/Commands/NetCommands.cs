using System.CommandLine;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Game;
using VmpLauncher.Net.Directory;
using VmpLauncher.Net.Doctor;
using VmpLauncher.Net.Probing;

namespace VmpLauncher.Cli.Commands;

internal static class NetCommands
{
    public static Command Build()
    {
        var net = new Command("net", "VMP server probes and the Connection Doctor");
        net.Subcommands.Add(BuildProbe());
        net.Subcommands.Add(BuildDoctor());
        return net;
    }

    private static Option<string> PasswordEnvOption() => new("--password-env")
    {
        Description = "Environment variable holding the server password (only used for --level 2/3 and the doctor). Never pass the password itself.",
        DefaultValueFactory = _ => "VMPL_PASSWORD",
    };

    private static Command BuildProbe()
    {
        var host = new Option<string>("--host") { Description = "Server host name or IP.", Required = true };
        var port = new Option<int>("--port") { Description = "Server UDP port.", DefaultValueFactory = _ => 8800 };
        var passwordEnv = PasswordEnvOption();
        var level = new Option<int>("--level") { Description = "1 = reachability (wrong-password probe, safe), 2 = saved password, 3 = server build.", DefaultValueFactory = _ => 1 };
        level.AcceptOnlyFromAmong("1", "2", "3");
        var timeout = new Option<double>("--timeout") { Description = "Overall timeout in seconds.", DefaultValueFactory = _ => 10 };
        var probe = new Command("probe", "Probe a VMP server over Lidgren/UDP") { host, port, passwordEnv, level, timeout };
        probe.SetAction(async (parse, ct) =>
        {
            var lvl = (ProbeLevel)parse.GetValue(level);
            string? password = null;
            if (lvl != ProbeLevel.Reachability)
            {
                var envName = parse.GetValue(passwordEnv)!;
                password = Environment.GetEnvironmentVariable(envName);
                if (string.IsNullOrEmpty(password))
                {
                    Console.Error.WriteLine($"Level {(int)lvl} needs the password in environment variable {envName}.");
                    return 2;
                }
            }
            var request = new ProbeRequest(parse.GetValue(host)!, parse.GetValue(port), lvl)
            {
                Password = password,
                Timeout = TimeSpan.FromSeconds(Math.Clamp(parse.GetValue(timeout), 1, 120)),
            };
            Console.WriteLine($"Probing {request} ...");
            var r = await new VmpServerProbe().ProbeAsync(request, ct).ConfigureAwait(false);
            string Clean(string? s) => ReportRedactor.Redact(s, [password]);
            Console.WriteLine($"outcome:     {r.Outcome}");
            Console.WriteLine($"address:     {r.RemoteAddress ?? "-"}");
            Console.WriteLine($"rtt:         {(r.RoundTrip is { } t ? $"{t.TotalMilliseconds:0} ms" : "-")}");
            if (r.ServerBuild is not null) Console.WriteLine($"serverBuild: {r.ServerBuild}");
            if (r.Reason is not null) Console.WriteLine($"reason:      {Clean(r.Reason)}");
            if (r.Error is not null) Console.WriteLine($"error:       {Clean(r.Error)}");
            return r.Outcome is ProbeOutcome.Online or ProbeOutcome.OnlineNoPassword or ProbeOutcome.PasswordOk or ProbeOutcome.BuildReported ? 0 : 1;
        });
        return probe;
    }

    private static Command BuildDoctor()
    {
        var servers = new Option<string>("--servers") { Description = "servers.json path or http(s) URL (diagnostic use: the signature is not checked here).", Required = true };
        var passwordEnv = PasswordEnvOption();
        var game = new Option<string?>("--game") { Description = "KSP folder, to compare the installed VMP client build with the server." };
        var report = new Option<string?>("--report") { Description = "Also write the redacted Markdown report to this file." };
        var doctor = new Command("doctor", "Run the Connection Doctor against a server list") { servers, passwordEnv, game, report };
        doctor.SetAction(async (parse, ct) =>
        {
            var source = parse.GetValue(servers)!;
            var envName = parse.GetValue(passwordEnv)!;
            var gameDir = parse.GetValue(game);
            Console.Error.WriteLine("note: CLI doctor loads the server list WITHOUT signature verification.");
            var d = new ConnectionDoctor(new ConnectionDoctorOptions
            {
                LoadServers = c => LoadUnverifiedAsync(source, c),
                PasswordProvider = _ => ValueTask.FromResult(Environment.GetEnvironmentVariable(envName) is { Length: > 0 } p ? p : null),
                InstalledBuild = gameDir is null ? null : () => LmpClientInspector.Inspect(gameDir)?.ExactBuild,
            });
            await foreach (var step in d.RunAsync(ct).ConfigureAwait(false))
            {
                if (step.Status == DoctorStatus.Running)
                {
                    continue;
                }
                Console.WriteLine($"[{step.Status,-7}] {step.Title}: {step.Summary}{(step.Fix is { } f ? $"  (fix: {f})" : string.Empty)}");
            }
            var markdown = d.BuildReport();
            Console.WriteLine();
            Console.WriteLine(markdown);
            if (parse.GetValue(report) is { } path)
            {
                File.WriteAllText(path, markdown);
                Console.Error.WriteLine($"Report written to {path}");
            }
            return d.Results.Any(s => s.Status == DoctorStatus.Fail) ? 1 : 0;
        });
        return doctor;
    }

    private static async Task<ServerDirectoryResult> LoadUnverifiedAsync(string source, CancellationToken ct)
    {
        byte[] bytes;
        DateTimeOffset fetched;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            fetched = DateTimeOffset.UtcNow;
        }
        else
        {
            var fi = new FileInfo(source);
            if (!fi.Exists)
            {
                throw new LauncherException(ErrorCode.ArtifactUnavailable, $"{source} does not exist.");
            }
            bytes = await File.ReadAllBytesAsync(fi.FullName, ct).ConfigureAwait(false);
            fetched = fi.LastWriteTimeUtc;
        }
        var doc = ServerDirectory.Parse(bytes);
        return new ServerDirectoryResult(doc, false, fetched, DateTimeOffset.UtcNow - fetched, KeyId: null, Warning: "Loaded without signature verification (CLI).");
    }
}
