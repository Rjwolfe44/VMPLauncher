using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VmpLauncher.Core.Private;
using VmpLauncher.Publishing.Publish;

namespace VmpLauncher.Cli.Commands;

/// <summary>
/// <c>vmpl private set-token</c>, <c>vmpl private check</c>, <c>vmpl private invite</c>: the host side of private mods.
/// Secrets never appear on the command line or in the output: the token is read from the console without echo,
/// and the invite code goes straight to the clipboard.
/// </summary>
internal static class PrivateCommands
{
    private static string DefaultKeyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMPLauncher", "publisher");

    public static Command Build()
    {
        var repo = new Option<string>("--repo") { Description = "Private mods repository (owner/name).", Required = true };
        var keyDir = new Option<string?>("--key-dir") { Description = "Key directory (default %APPDATA%\\VMPLauncher\\publisher)." };

        var setToken = new Command("set-token", "Store the read-only fine-grained token friends' invite codes carry (asked for without echo)") { repo, keyDir };
        setToken.SetAction(async (parse, ct) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return 2;
            }
            Console.Write("Fine-grained token (Contents: read-only, this repository only): ");
            var token = ReadHidden();
            if (HostAccessStore.WhyUnsafe(token) is { } why)
            {
                Console.Error.WriteLine(why);
                return 2;
            }
            using var gh = new GitHubPrivateReleases();
            var access = await gh.CheckAccessAsync(parse.GetValue(repo)!, token, ct).ConfigureAwait(false);
            Console.WriteLine(access.Message);
            if (access.State != GitHubPrivateReleases.AccessState.Ok)
            {
                return 3;
            }
            HostAccessStore.Set(parse.GetValue(keyDir) ?? DefaultKeyDir, parse.GetValue(repo)!, token);
            Console.WriteLine("Saved (encrypted for your Windows account).");
            return 0;
        });

        var check = new Command("check", "Check that the stored token can read the private repository") { repo, keyDir };
        check.SetAction(async (parse, ct) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return 2;
            }
            var token = HostAccessStore.TryGet(parse.GetValue(keyDir) ?? DefaultKeyDir, parse.GetValue(repo)!);
            if (token is null)
            {
                Console.Error.WriteLine("No token stored for that repository. Run: vmpl private set-token --repo ...");
                return 2;
            }
            using var gh = new GitHubPrivateReleases();
            var access = await gh.CheckAccessAsync(parse.GetValue(repo)!, token, ct).ConfigureAwait(false);
            Console.WriteLine(access.Message);
            return access.State == GitHubPrivateReleases.AccessState.Ok ? 0 : 3;
        });

        var packId = new Option<string?>("--pack-id") { Description = "Pack id (to find the publish configuration and its private connection file)." };
        var noPassword = new Option<bool>("--no-password") { Description = "Leave the server password out of the invite." };
        var inviteRepo = new Option<string?>("--repo") { Description = "Private mods repository to grant (omit for a password-only invite)." };
        var invite = new Command("invite", "Copy an invite code (private mods access + server password) to the clipboard") { inviteRepo, packId, noPassword, keyDir };
        invite.SetAction(parse =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return 2;
            }
            var kdir = parse.GetValue(keyDir) ?? DefaultKeyDir;
            var r = parse.GetValue(inviteRepo);
            string? token = null;
            if (r is not null)
            {
                token = HostAccessStore.TryGet(kdir, r);
                if (token is null)
                {
                    Console.Error.WriteLine($"No token stored for {r}. Run: vmpl private set-token --repo {r}");
                    return 2;
                }
            }
            string? password = null;
            if (!parse.GetValue(noPassword))
            {
                var cfgPath = parse.GetValue(packId) is { } id
                    ? PublishConfig.DefaultPath(kdir, id)
                    : Directory.EnumerateFiles(kdir, "publish.*.json").FirstOrDefault();
                var cfg = cfgPath is null ? null : PublishConfig.TryLoad(cfgPath);
                if (cfg?.PrivateConnectionFile is { } file && File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    password = doc.RootElement.TryGetProperty("Password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                }
                if (password is null)
                {
                    Console.Error.WriteLine("No server password found in the publish configuration's private connection file (use --no-password to skip it).");
                    return 2;
                }
            }
            if (token is null && password is null)
            {
                Console.Error.WriteLine("Nothing to put in the invite.");
                return 2;
            }
            var code = InviteCode.Encode(new Invite { From = "host", Repo = r, Token = token, ServerPassword = password });
            CopyToClipboard(code);
            Console.WriteLine($"Invite code copied to the clipboard ({code.Length} characters). Paste it to your friend in a private message; it works like a key.");
            return 0;
        });

        return new Command("private", "Private mods repository and invite codes (host)") { setToken, check, invite };
    }

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected)
        {
            return (Console.ReadLine() ?? string.Empty).Trim();
        }
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString().Trim();
            }
            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
                continue;
            }
            if (!char.IsControl(k.KeyChar))
            {
                sb.Append(k.KeyChar);
            }
        }
    }

    private static void CopyToClipboard(string text)
    {
        var psi = new ProcessStartInfo("clip.exe") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("clip.exe is not available.");
        p.StandardInput.Write(text);
        p.StandardInput.Close();
        p.WaitForExit(5000);
    }
}
