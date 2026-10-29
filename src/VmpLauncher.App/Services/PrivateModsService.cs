using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Private;

namespace VmpLauncher.App.Services;

/// <summary>One private package the current pack needs, and whether this PC can get it without help.</summary>
public sealed record PrivateNeed(LockedModule Module, ArtifactRef Artifact, PrivateNeedState State, string Detail)
{
    public string Name => Module.Metadata.TryGetProperty("name", out var n) ? n.GetString() ?? Module.Identifier : Module.Identifier;
}

public enum PrivateNeedState
{
    /// <summary>The same bytes are already installed in the profile; nothing to fetch for this version.</summary>
    Installed,
    /// <summary>A verified copy is already in the private cache.</summary>
    Ready,
    /// <summary>Not cached yet, but this PC holds access to the private repository: it downloads automatically.</summary>
    WillDownload,
    /// <summary>The package lives in a private repository and this PC has no invite yet.</summary>
    NeedsInvite,
    /// <summary>Hand-over only: the host must send the file.</summary>
    NeedsFile,
}

/// <summary>
/// Private packages (ApexGuidance): fetched from the host's private GitHub repository with the read-only token an
/// invite code carries, or accepted as a dropped zip. Every copy is verified against the signed lock and kept in
/// <c>cache\private\&lt;sha256&gt;.zip</c>, so an unchanged package is never fetched or asked for twice.
/// </summary>
public sealed partial class PrivateModsService : ObservableObject
{
    private readonly LauncherPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly AppSettingsStore _settings;
    private readonly ILogger<PrivateModsService> _log;

    public PrivateModsService(LauncherPaths paths, ISecretStore secrets, AppSettingsStore settings, ILogger<PrivateModsService> log)
    {
        _paths = paths;
        _secrets = secrets;
        _settings = settings;
        _log = log;
    }

    /// <summary>Raised after an invite is applied or a file is accepted, so pages can refresh.</summary>
    public event Action? Changed;

    [ObservableProperty] private string _accessSummary = "No invite code yet.";

    public static string TokenKey(string repo) => $"private-repo-token:{repo.ToLowerInvariant()}";

    public bool HasAccess(string repo) => _secrets.Has(TokenKey(repo));

    public IReadOnlyList<string> KnownRepos => _settings.Current.PrivateRepos;

    public string CachedPath(ArtifactRef a) => Path.Combine(_paths.PrivateCacheDir, a.Sha256.ToLowerInvariant() + ".zip");

    /// <param name="installed">The lock the profile was last installed from; a private package whose bytes are unchanged needs nothing.</param>
    public IReadOnlyList<PrivateNeed> Needs(PackLock lockDoc, PackLock? installed = null)
    {
        var list = new List<PrivateNeed>();
        foreach (var m in lockDoc.Ckan.Modules)
        {
            if (m.Artifact is not { Distribution: "private-sideload" } a)
            {
                continue;
            }
            var already = installed?.Ckan.Modules.FirstOrDefault(x => x.Identifier == m.Identifier);
            if (already?.Artifact is { } ia && Sha256Util.HexEquals(ia.Sha256, a.Sha256))
            {
                list.Add(new PrivateNeed(m, a, PrivateNeedState.Installed, "installed"));
            }
            else if (IsCachedAndValid(a))
            {
                list.Add(new PrivateNeed(m, a, PrivateNeedState.Ready, $"{a.FileName} is ready"));
            }
            else if (a.Private is { } src && HasAccess(src.Repo))
            {
                list.Add(new PrivateNeed(m, a, PrivateNeedState.WillDownload, $"downloads from {src.Repo}"));
            }
            else if (a.Private is { } src2)
            {
                list.Add(new PrivateNeed(m, a, PrivateNeedState.NeedsInvite, $"needs an invite code for {src2.Repo}"));
            }
            else
            {
                list.Add(new PrivateNeed(m, a, PrivateNeedState.NeedsFile, $"ask the host for {a.FileName}"));
            }
        }
        return list;
    }

    /// <summary>Decodes an invite, stores its token (and the server password when present) with DPAPI.</summary>
    public Invite ApplyInvite(string text, string passwordKey)
    {
        var invite = InviteCode.Decode(text);
        if (invite.Repo is not null && invite.Token is not null)
        {
            _secrets.Set(TokenKey(invite.Repo), invite.Token);
            _settings.Update(s => s with { PrivateRepos = s.PrivateRepos.Append(invite.Repo).Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
        }
        if (!string.IsNullOrEmpty(invite.ServerPassword))
        {
            _secrets.Set(passwordKey, invite.ServerPassword);
        }
        _log.LogInformation("Invite applied: repo {Repo}, password {HasPassword}", invite.Repo ?? "(none)", invite.ServerPassword is not null);
        AccessSummary = invite.Repo is null ? "Invite applied (server password only)." : $"Private mods: {invite.Repo}";
        Changed?.Invoke();
        return invite;
    }

    public void Forget(string repo)
    {
        _secrets.Set(TokenKey(repo), null);
        _settings.Update(s => s with { PrivateRepos = s.PrivateRepos.Where(r => !string.Equals(r, repo, StringComparison.OrdinalIgnoreCase)).ToList() });
        Changed?.Invoke();
    }

    public async Task<GitHubPrivateReleases.AccessResult> CheckAccessAsync(string repo, CancellationToken ct)
    {
        var token = _secrets.Get(TokenKey(repo));
        if (token is null)
        {
            return new GitHubPrivateReleases.AccessResult(GitHubPrivateReleases.AccessState.NoAccess, "No invite code for this repository on this PC.");
        }
        using var gh = new GitHubPrivateReleases();
        var r = await gh.CheckAccessAsync(repo, token, ct).ConfigureAwait(false);
        AccessSummary = r.Message;
        return r;
    }

    /// <summary>
    /// A zip the user dropped or picked. Returns the matching module, or throws a user-facing error. Accepted files
    /// are copied into the private cache so the drop survives restarts and later updates of other mods.
    /// </summary>
    public LockedModule AcceptFile(PackLock lockDoc, string path)
    {
        var name = Path.GetFileName(path);
        var candidates = lockDoc.Ckan.Modules.Where(m => m.Artifact?.Distribution == "private-sideload").ToList();
        if (candidates.Count == 0)
        {
            throw new LauncherException(ErrorCode.PrivateArtifactNeeded, "This pack does not need any private files.", RecoveryHint.None);
        }
        var size = new FileInfo(path).Length;
        var sha = Sha256Util.HashFile(path);
        var match = candidates.FirstOrDefault(m => Sha256Util.HexEquals(m.Artifact!.Sha256, sha) && m.Artifact.Size == size);
        if (match is null)
        {
            var sameName = candidates.FirstOrDefault(m => string.Equals(m.Artifact!.FileName, name, StringComparison.OrdinalIgnoreCase));
            throw sameName is null
                ? new LauncherException(ErrorCode.PrivateArtifactNeeded, $"{name} is not one of the private files this pack needs.", RecoveryHint.ProvideFile,
                    details: candidates.Select(c => "needs " + c.Artifact!.FileName).ToList())
                : new LauncherException(ErrorCode.ArtifactCorrupt, $"{name} is a different version than this pack expects. Ask the host for the current one.", RecoveryHint.ProvideFile,
                    details: new[] { $"expected sha256 {sameName.Artifact!.Sha256}", $"actual sha256 {sha}" });
        }
        Directory.CreateDirectory(_paths.PrivateCacheDir);
        var dest = CachedPath(match.Artifact!);
        File.Copy(path, dest + ".tmp", overwrite: true);
        File.Move(dest + ".tmp", dest, overwrite: true);
        Changed?.Invoke();
        return match;
    }

    /// <summary>
    /// Makes every private package of <paramref name="lockDoc"/> available locally: cached copies first, then
    /// downloads with the stored invite token. Returns identifier → verified zip path for the install. Packages that
    /// cannot be obtained are left out; the install then stops with a clear "private package needed" error.
    /// </summary>
    public async Task<Dictionary<string, string>> PrepareAsync(PackLock lockDoc, PackLock? installed, IProgress<CkanProgress>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var gh = new GitHubPrivateReleases();
        foreach (var need in Needs(lockDoc, installed))
        {
            ct.ThrowIfCancellationRequested();
            var a = need.Artifact;
            var dest = CachedPath(a);
            if (need.State == PrivateNeedState.Ready || (need.State == PrivateNeedState.Installed && IsCachedAndValid(a)))
            {
                result[need.Module.Identifier] = dest;
                continue;
            }
            if (need.State != PrivateNeedState.WillDownload || a.Private is not { } src)
            {
                continue;
            }
            var token = _secrets.Get(TokenKey(src.Repo));
            if (token is null)
            {
                continue;
            }
            progress?.Report(new CkanProgress($"Downloading private mod {need.Name} from {src.Repo}…", 0, null));
            var started = DateTime.UtcNow;
            var sink = new Progress<(long Done, long Total)>(p =>
            {
                var pct = p.Total > 0 ? (int)(p.Done * 100 / p.Total) : -1;
                progress?.Report(new CkanProgress($"Downloading private mod {need.Name}: {Format(p.Done)} of {Format(p.Total)}", pct, null));
            });
            await gh.DownloadAsync(src, a, token, dest, sink, ct).ConfigureAwait(false);
            _log.LogInformation("Private package {Id} downloaded from {Repo} in {Seconds:0.0}s", need.Module.Identifier, src.Repo, (DateTime.UtcNow - started).TotalSeconds);
            result[need.Module.Identifier] = dest;
        }
        Prune(lockDoc);
        return result;
    }

    private bool IsCachedAndValid(ArtifactRef a)
    {
        var path = CachedPath(a);
        if (!File.Exists(path))
        {
            return false;
        }
        var fi = new FileInfo(path);
        if (fi.Length != a.Size)
        {
            return false;
        }
        // The file name is the hash; re-hash only when it was written after our last check (cheap and tamper-evident).
        var stamp = path + ".ok";
        if (File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) >= fi.LastWriteTimeUtc)
        {
            return true;
        }
        if (!Sha256Util.HexEquals(Sha256Util.HashFile(path), a.Sha256))
        {
            File.Delete(path);
            return false;
        }
        File.WriteAllText(stamp, string.Empty);
        return true;
    }

    /// <summary>Keeps the private cache small: only the zips the current lock references survive.</summary>
    private void Prune(PackLock lockDoc)
    {
        try
        {
            var keep = lockDoc.Ckan.Modules.Where(m => m.Artifact?.Distribution == "private-sideload").Select(m => m.Artifact!.Sha256.ToLowerInvariant() + ".zip").ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(_paths.PrivateCacheDir))
            {
                return;
            }
            foreach (var f in Directory.EnumerateFiles(_paths.PrivateCacheDir, "*.zip"))
            {
                if (!keep.Contains(Path.GetFileName(f)) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30))
                {
                    File.Delete(f);
                    File.Delete(f + ".ok");
                }
            }
        }
        catch (IOException ex)
        {
            _log.LogDebug(ex, "Private cache prune skipped");
        }
    }

    private static string Format(long bytes) => ViewModels.Format.Bytes(bytes);
}
