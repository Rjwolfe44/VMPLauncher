using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Private;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Net.Probing;
using VmpLauncher.Publishing.Publish;

namespace VmpLauncher.App.Services;

/// <summary>
/// Host mode: shown only on a PC that holds a publisher key and a publish configuration (the host's). Wraps the same
/// <see cref="Publisher"/> the <c>vmpl publish</c> command runs, plus invite codes for friends. Nothing here runs
/// without an explicit click, and publishing for real needs the word PUBLISH typed on the page.
/// </summary>
public sealed class HostToolsService
{
    public static readonly string KeyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMPLauncher", "publisher");

    private readonly LauncherPaths _paths;
    private readonly ILogger<HostToolsService> _log;

    public HostToolsService(LauncherPaths paths, ILogger<HostToolsService> log)
    {
        _paths = paths;
        _log = log;
        try
        {
            ConfigPath = Directory.Exists(KeyDir) ? Directory.EnumerateFiles(KeyDir, "publish.*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
            Config = ConfigPath is null ? null : PublishConfig.TryLoad(ConfigPath);
            if (Config is not null && File.Exists(Config.PackFile))
            {
                Pack = LauncherJson.ReadFile<PackDefinition>(Config.PackFile);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Publish configuration unreadable");
        }
    }

    public string? ConfigPath { get; }
    public PublishConfig? Config { get; }
    public PackDefinition? Pack { get; }

    public bool IsHost => Config is not null && Pack is not null && File.Exists(KeyFile);

    private string KeyFile => Config is null ? string.Empty : Path.Combine(Config.KeyDir ?? KeyDir, Config.KeyId + ".key.json");

    /// <summary>Private repositories the pack definition uploads to (distribution "private-github").</summary>
    public IReadOnlyList<string> PrivateRepos => Pack?.CustomPackages.Where(c => c.Distribution.Kind == "private-github" && c.Distribution.Repo is not null)
        .Select(c => c.Distribution.Repo!).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    public bool HasToken(string repo) => OperatingSystem.IsWindows() && HostAccessStore.TryGet(KeyDir, repo) is not null;

    /// <summary>Validates and stores the read-only token friends' invite codes will carry.</summary>
    public async Task<string> SaveTokenAsync(string repo, string token, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        if (HostAccessStore.WhyUnsafe(token) is { } why)
        {
            throw new LauncherException(ErrorCode.InvalidPack, why, RecoveryHint.None);
        }
        using var gh = new GitHubPrivateReleases();
        var access = await gh.CheckAccessAsync(repo, token.Trim(), ct).ConfigureAwait(false);
        if (access.State != GitHubPrivateReleases.AccessState.Ok)
        {
            throw new LauncherException(ErrorCode.InvalidPack, access.Message, RecoveryHint.None);
        }
        HostAccessStore.Set(KeyDir, repo, token);
        return access.Message;
    }

    /// <summary>Builds an invite code. The server password is read from the private connection file in memory only.</summary>
    public string CreateInvite(string? repo, bool includePassword)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        string? token = null;
        if (repo is not null)
        {
            token = HostAccessStore.TryGet(KeyDir, repo) ?? throw new LauncherException(ErrorCode.InvalidPack, $"Save the read-only token for {repo} first.", RecoveryHint.None);
        }
        string? password = null;
        if (includePassword)
        {
            password = ReadServerPassword() ?? throw new LauncherException(ErrorCode.InvalidPack, "No server password found in the private connection file.", RecoveryHint.None);
        }
        if (token is null && password is null)
        {
            throw new LauncherException(ErrorCode.InvalidPack, "Nothing to put in the invite: add a private repository token or include the password.", RecoveryHint.None);
        }
        return InviteCode.Encode(new Invite { From = Pack?.Author, PackId = Pack?.Id, Repo = repo, Token = token, ServerPassword = password });
    }

    private string? ReadServerPassword()
    {
        var file = Config?.PrivateConnectionFile;
        if (file is null || !File.Exists(file))
        {
            return null;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.TryGetProperty("Password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>Runs the publisher (dry run or for real) on its own CKAN worker, streaming log lines to <paramref name="onLine"/>.</summary>
    public async Task<Publisher.Report> PublishAsync(string? notes, string? generatedPolicy, bool allowPatchedPolicy, bool execute, Action<string> onLine, CancellationToken ct)
    {
        var cfg = Config ?? throw new LauncherException(ErrorCode.InvalidPack, "No publish configuration on this PC.", RecoveryHint.None);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        var pass = Environment.GetEnvironmentVariable("VMPL_KEY_PASSPHRASE") ?? PassphraseStore.TryLoad(cfg.KeyDir ?? KeyDir, cfg.KeyId)
                   ?? throw new LauncherException(ErrorCode.InvalidPack, "The publisher key passphrase is not stored on this PC. Use vmpl publish from a terminal.", RecoveryHint.None);
        var writer = new LineWriter(onLine);
        return await Task.Run(() =>
        {
            using var key = PublisherKey.LoadEncrypted(KeyFile, pass);
            using var worker = new CkanWorker();
            var options = new Publisher.Options(cfg, Version: null, notes, generatedPolicy, allowPatchedPolicy, execute, SkipServer: false, SkipAssets: false, AcceptOverrides: false, AllowDirty: false);
            return worker.Run(() => new Publisher(writer).Run(options, key, _paths, ct));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The library definition (<c>library\vlad.library.json</c>) next to the pack definition, or null when there is none.</summary>
    public string? LibraryDefinitionFile => Config?.PackFile is { } packFile ? VmpLauncher.Publishing.Library.LibraryDefinition.FindNextTo(packFile) : null;

    /// <summary>Builds and signs Vlad's mod library (dry run, or for real), streaming log lines to <paramref name="onLine"/>.</summary>
    public async Task<VmpLauncher.Publishing.Library.LibraryPublisher.Report> PublishLibraryAsync(bool execute, Action<string> onLine, CancellationToken ct)
    {
        var cfg = Config ?? throw new LauncherException(ErrorCode.InvalidPack, "No publish configuration on this PC.", RecoveryHint.None);
        var definition = LibraryDefinitionFile ?? throw new LauncherException(ErrorCode.InvalidPack, "No library definition (library\\vlad.library.json) next to the pack definition.", RecoveryHint.None);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        var pass = Environment.GetEnvironmentVariable("VMPL_KEY_PASSPHRASE") ?? PassphraseStore.TryLoad(cfg.KeyDir ?? KeyDir, cfg.KeyId)
                   ?? throw new LauncherException(ErrorCode.InvalidPack, "The publisher key passphrase is not stored on this PC. Use vmpl library publish from a terminal.", RecoveryHint.None);
        var writer = new LineWriter(onLine);
        return await Task.Run(() =>
        {
            using var key = PublisherKey.LoadEncrypted(KeyFile, pass);
            using var worker = new CkanWorker();
            var options = new VmpLauncher.Publishing.Library.LibraryPublisher.Options(cfg, definition, execute, AllowDirty: false);
            return worker.Run(() => new VmpLauncher.Publishing.Library.LibraryPublisher(writer).Run(options, key, ct));
        }, ct).ConfigureAwait(false);
    }

    public async Task<RemoteVerifier.Result> VerifyRemoteAsync(string expectedVersion, CancellationToken ct)
    {
        var cfg = Config ?? throw new InvalidOperationException("no config");
        var catalog = cfg.CatalogUrl ?? PackCatalogService.DefaultCatalogUrl;
        return await new RemoteVerifier(probe: new VmpServerProbe()).VerifyAsync(catalog, Pack!.Id, expectedVersion, ct).ConfigureAwait(false);
    }

    /// <summary>TextWriter that hands complete lines to a callback.</summary>
    private sealed class LineWriter(Action<string> onLine) : TextWriter
    {
        private readonly StringBuilder _line = new();
        private readonly object _gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            string? done = null;
            lock (_gate)
            {
                if (value == '\n')
                {
                    done = _line.ToString().TrimEnd('\r');
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
            if (done is not null)
            {
                onLine(done);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }
            foreach (var c in value)
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
    }
}
