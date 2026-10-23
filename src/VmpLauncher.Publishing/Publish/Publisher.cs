using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Policy;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Publishing.Policy;

namespace VmpLauncher.Publishing.Publish;

/// <summary>
/// One-command release: policy → export → verify → stage → (external) release assets, git push, server policy update.
/// Dry run by default: everything up to staging happens in a scratch mirror and every external command is printed
/// verbatim; <c>--execute</c> stages into the real repository and runs those commands.
/// </summary>
public sealed class Publisher
{
    public sealed record Options(
        PublishConfig Config,
        string? Version,
        string? Notes,
        string? GeneratedPolicyFile,
        bool AllowPatchedPolicyStructure,
        bool Execute,
        bool SkipServer,
        bool SkipAssets,
        bool AcceptOverrides,
        bool AllowDirty);

    /// <summary>An external side effect: shown in the plan, run on --execute.</summary>
    public sealed record ExternalAction(string Title, IReadOnlyList<string> Commands, Action<ExternalTools> Run);

    public sealed record Report(
        string Version,
        string ExportVersionDir,
        string StagedRepoDir,
        ModControlPatcher.Result Policy,
        PackStager.Result Staged,
        IReadOnlyList<ExternalAction> Actions,
        IReadOnlyList<string> SideloadFiles,
        IReadOnlyList<string> Warnings,
        bool Executed,
        string? PushedCommit,
        TimeSpan Elapsed);

    private static readonly string[] RegenerateHints =
    {
        "Start KSP, generate KSPModControl + SHA from the VMP options, close KSP and re-run with --policy-generated <file>.",
        "Or pass --allow-patched-policy if no parts changed (DLL-only mods).",
    };

    private readonly TextWriter _log;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;

    public Publisher(TextWriter log, TimeProvider? clock = null, HttpClient? http = null)
    {
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public Report Run(Options o, PublisherKey key, LauncherPaths paths, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var c = o.Config;
        var warnings = new List<string>();
        var pack = LauncherJson.ReadFile<PackDefinition>(c.PackFile);
        var packsDir = Path.Combine(c.PacksRepoDir, "packs", pack.Id);
        var version = o.Version ?? NextVersion(packsDir, _clock.GetLocalNow());
        _log.WriteLine($"== {pack.Id} {version} ({(o.Execute ? "EXECUTE" : "dry run")}) ==");

        // 1. Policy: patch from the host GameData, or normalize a freshly generated in-game file.
        var workDir = Path.Combine(c.ExportRoot, pack.Id, "policy-work");
        var workPolicy = Path.Combine(workDir, "KSPModControl.xml");
        ModControlPatcher.Result policy;
        if (o.GeneratedPolicyFile is not null)
        {
            policy = ModControlPatcher.Normalize(o.GeneratedPolicyFile, workPolicy);
            _log.WriteLine($"Policy: normalized in-game file ({policy.MandatoryDlls} mandatory DLLs).");
        }
        else
        {
            if (!File.Exists(c.PolicyFile))
            {
                throw new LauncherException(ErrorCode.InvalidPack, $"No current policy at {c.PolicyFile}. Generate KSPModControl + SHA in-game once and pass it with --policy-generated.", RecoveryHint.None);
            }
            policy = ModControlPatcher.Patch(c.PolicyFile, c.HostKspDir, workPolicy, ct);
            _log.WriteLine($"Policy: {policy.Summary}.");
            foreach (var ch in policy.Changes)
            {
                _log.WriteLine($"  {ch.Kind,-8} {ch.Path}");
            }
            if (policy.StructureChanged && !o.AllowPatchedPolicyStructure)
            {
                throw new LauncherException(ErrorCode.InvalidPack,
                    "DLLs were added or removed since the policy was generated, so the part list is probably stale too.",
                    RecoveryHint.None,
                    details: RegenerateHints);
            }
        }
        var check = ModControlChecker.Check(c.HostKspDir, ModControlPolicy.Parse(workPolicy), cache: null, ct);
        if (!check.Passes)
        {
            throw new LauncherException(ErrorCode.VerifyFailed, $"The host GameData does not satisfy the new policy ({check.Problems} problem(s)).", RecoveryHint.None,
                details: check.ShaMismatch.Concat(check.MissingMandatory).Concat(check.NonListed).Concat(check.Forbidden).Select(m => $"{m.Kind} {m.Path}").Concat(check.MissingDlc.Select(d => "missing DLC " + d)).ToList());
        }
        _log.WriteLine($"Policy gate: host GameData passes ({check.Checked} DLLs).");

        // 2. Export.
        var export = new PackExporter(_log).Export(new PackExporter.Request(c.PackFile, c.HostKspDir, c.HostCacheDir, c.ExportRoot, paths,
            o.AcceptOverrides, o.AllowDirty, workPolicy, c.PrivateConnectionFile, key, version) { ExtraSecrets = HostTokens(c) }, ct);
        warnings.AddRange(export.Warnings);

        // 3. Verify the export before anything leaves this machine.
        var verified = ExportVerifier.Verify(export.VersionDir);
        if (!verified.Ok)
        {
            throw new LauncherException(ErrorCode.VerifyFailed, "The export did not verify; nothing was staged.", RecoveryHint.None, details: verified.Problems);
        }
        _log.WriteLine($"Export verified: {verified.Lock.Ckan.Modules.Count} modules, signature {SignatureVerifier.DescribeTrust(verified.Trust)} ({verified.KeyId}).");
        var lk = verified.Lock;

        // 4. Stage: into the real repository on --execute, into a scratch mirror otherwise.
        var repoDir = c.PacksRepoDir;
        if (!o.Execute)
        {
            repoDir = Path.Combine(c.ExportRoot, pack.Id, "staging");
            MirrorPackDir(c.PacksRepoDir, repoDir, pack.Id);
        }
        else
        {
            EnsureCleanRepo(c, warnings);
        }
        var now = _clock.GetLocalNow(); // changelog dates in the host's day; servers.json issuedAt is converted to UTC
        var catalogUrl = c.CatalogUrl ?? DeriveCatalogUrl(pack);
        var staged = new PackStager().Stage(new PackStager.Request(repoDir, export.VersionDir, Path.Combine(c.ExportRoot, pack.Id, "channel.json"), lk, o.Notes, policy.Summary, key, catalogUrl, now));
        _log.WriteLine($"Staged {staged.Written.Count} file(s) under {repoDir}{(o.Execute ? "" : " (scratch mirror)")}.");
        if (staged.Diff is { } d)
        {
            _log.WriteLine($"Changes vs {staged.PreviousLock!.Pack.Version}: {d.Summary()}.");
        }
        var stagedCheck = ExportVerifier.Verify(staged.VersionDir, assetSearchDirs: new[] { export.VersionDir });
        if (!stagedCheck.Ok)
        {
            throw new LauncherException(ErrorCode.VerifyFailed, "The staged version folder did not verify.", RecoveryHint.None, details: stagedCheck.Problems);
        }

        // 5. External actions.
        var actions = new List<ExternalAction>();
        var assetActions = PlanReleaseAssets(pack, lk, export, ct).Concat(PlanPrivateAssets(pack, lk, export, warnings)).ToList();
        if (!o.SkipAssets)
        {
            actions.AddRange(assetActions);
        }
        else if (assetActions.Count > 0)
        {
            // Skipping uploads is fine only when everything is already online; otherwise friends get a lock that 404s.
            throw new LauncherException(ErrorCode.ArtifactUnavailable, "--skip-assets was given, but some downloads the new lock needs are not online yet.", RecoveryHint.None,
                details: assetActions.Select(a => a.Title).ToList());
        }
        var firstLine = o.Notes?.Split('\n')[0].Trim();
        var commitMessage = $"{lk.Pack.Name} {version}" + (string.IsNullOrEmpty(firstLine) ? "" : ": " + firstLine);
        actions.Add(new ExternalAction("Publish the pack (git)",
            new[] { ExternalTools.Describe("git", new[] { "-C", c.PacksRepoDir, "add", "-A" }), ExternalTools.Describe("git", new[] { "-C", c.PacksRepoDir, "commit", "-m", commitMessage }), ExternalTools.Describe("git", new[] { "-C", c.PacksRepoDir, "push", "origin", c.PacksRepoBranch }) },
            tools =>
            {
                tools.Git(c.PacksRepoDir, "add", "-A");
                tools.Git(c.PacksRepoDir, "-c", "core.safecrlf=false", "commit", "-q", "-m", commitMessage);
                tools.Git(c.PacksRepoDir, "push", "origin", c.PacksRepoBranch);
            }));
        // Compare with what the server actually runs (c.PolicyFile is only updated after a successful server step), so a
        // failed or skipped deployment is retried next time instead of being mistaken for "unchanged".
        var serverNeedsPolicy = !File.Exists(c.PolicyFile) || !Sha256Util.HexEquals(Sha256Util.HashFile(c.PolicyFile, ct), Sha256Util.HashFile(workPolicy, ct));
        var serverStepPlanned = false;
        if (serverNeedsPolicy)
        {
            if (c.ServerHost is not null && c.ServerRoot is not null && !o.SkipServer)
            {
                actions.Add(PlanServerUpdate(c, workPolicy));
                serverStepPlanned = true;
            }
            else
            {
                warnings.Add("The server needs the new KSPModControl.xml but no server update is configured (serverHost/serverRoot) or --skip-server was given. Deploy it yourself or players will be rejected; the next publish will offer it again.");
            }
        }
        else
        {
            _log.WriteLine("Server policy matches what the server runs: no server restart needed.");
        }

        // 6. Show the plan.
        _log.WriteLine();
        _log.WriteLine(o.Execute ? "External actions:" : "External actions that --execute would run:");
        foreach (var a in actions)
        {
            _log.WriteLine($"- {a.Title}");
            foreach (var cmd in a.Commands)
            {
                _log.WriteLine($"    {cmd}");
            }
        }
        var handOver = HandOverOnly(pack, lk, export);
        if (handOver.Count > 0)
        {
            _log.WriteLine("- Hand out privately (never uploaded):");
            foreach (var s in handOver)
            {
                _log.WriteLine($"    {s}");
            }
        }

        // 7. Execute.
        string? pushed = null;
        if (o.Execute)
        {
            var tools = new ExternalTools(_log);
            foreach (var a in actions)
            {
                _log.WriteLine();
                _log.WriteLine($">> {a.Title}");
                a.Run(tools);
            }
            pushed = tools.Git(c.PacksRepoDir, "rev-parse", "HEAD").StdOut.Trim();
            if (serverStepPlanned)
            {
                // The server now runs this policy (the step throws otherwise): record it as deployed.
                File.Copy(workPolicy, c.PolicyFile, overwrite: true);
                if (c.LocalServerConfigDir is not null && Directory.Exists(c.LocalServerConfigDir))
                {
                    File.Copy(workPolicy, Path.Combine(c.LocalServerConfigDir, "KSPModControl.xml"), overwrite: true);
                }
            }
        }
        return new Report(version, export.VersionDir, repoDir, policy, staged, actions, handOver, warnings, o.Execute, pushed, sw.Elapsed);
    }

    /// <summary>yyyy.MM.dd.N, N = next free number for today in the pack's version folders.</summary>
    public static string NextVersion(string packDir, DateTimeOffset localNow)
    {
        var prefix = localNow.ToString("yyyy.MM.dd.", CultureInfo.InvariantCulture);
        var n = 0;
        if (Directory.Exists(packDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(packDir))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var k))
                {
                    n = Math.Max(n, k);
                }
            }
        }
        return prefix + (n + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string? DeriveCatalogUrl(PackDefinition pack)
    {
        // https://raw.githubusercontent.com/<owner>/<repo>/<branch>/packs/<id>/KSPModControl.xml -> .../<branch>/catalog.json
        var url = pack.Multiplayer.PolicyUrl ?? pack.Multiplayer.ServerDirectoryUrl;
        if (url is null)
        {
            return null;
        }
        var marker = "/packs/" + pack.Id + "/";
        var i = url.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? null : url[..i] + "/catalog.json";
    }

    private static void MirrorPackDir(string repoDir, string mirrorDir, string packId)
    {
        if (Directory.Exists(mirrorDir))
        {
            Directory.Delete(mirrorDir, recursive: true);
        }
        Directory.CreateDirectory(mirrorDir);
        foreach (var name in new[] { "catalog.json", ".gitattributes" })
        {
            var src = Path.Combine(repoDir, name);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(mirrorDir, name));
            }
        }
        var src2 = Path.Combine(repoDir, "packs", packId);
        if (Directory.Exists(src2))
        {
            foreach (var file in Directory.EnumerateFiles(src2, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src2, file);
                var dst = Path.Combine(mirrorDir, "packs", packId, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst);
            }
        }
    }

    private void EnsureCleanRepo(PublishConfig c, List<string> warnings)
    {
        var tools = new ExternalTools(TextWriter.Null);
        var status = tools.Git(c.PacksRepoDir, "status", "--porcelain");
        if (status.StdOut.Trim().Length > 0)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The packs repository at {c.PacksRepoDir} has uncommitted changes; commit or discard them first.", RecoveryHint.None, details: status.StdOut.Trim().Split('\n'));
        }
        var branch = tools.Git(c.PacksRepoDir, "branch", "--show-current").StdOut.Trim();
        if (branch != c.PacksRepoBranch)
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The packs repository is on branch '{branch}', expected '{c.PacksRepoBranch}'.", RecoveryHint.None);
        }
        var fetch = tools.Run("git", new[] { "-C", c.PacksRepoDir, "fetch", "-q", "origin", c.PacksRepoBranch }, throwOnError: false);
        if (!fetch.Ok)
        {
            warnings.Add("Could not fetch the packs repository; pushing may be rejected if the remote moved.");
            return;
        }
        var behind = tools.Git(c.PacksRepoDir, "rev-list", "--count", $"HEAD..origin/{c.PacksRepoBranch}").StdOut.Trim();
        if (behind != "0")
        {
            throw new LauncherException(ErrorCode.InvalidPack, $"The packs repository is {behind} commit(s) behind origin/{c.PacksRepoBranch}; pull first.", RecoveryHint.None);
        }
    }

    private IEnumerable<ExternalAction> PlanReleaseAssets(PackDefinition pack, PackLock lk, PackExporter.Report export, CancellationToken ct)
    {
        foreach (var src in pack.CustomPackages.Where(s => s.Distribution.Kind == "github-release"))
        {
            var module = lk.Ckan.Modules.FirstOrDefault(m => m.Identifier == src.Id);
            if (module?.Artifact is null || module.Artifact.Urls.Count == 0)
            {
                continue;
            }
            var url = module.Artifact.Urls[0];
            var zip = export.ReleaseAssets.FirstOrDefault(p => string.Equals(Path.GetFileName(p), module.Artifact.FileName, StringComparison.OrdinalIgnoreCase));
            if (zip is null)
            {
                continue;
            }
            switch (OnlineAssetState(url, module.Artifact.Sha256, ct))
            {
                case AssetState.Matches:
                    _log.WriteLine($"Release asset present and identical: {url}");
                    continue;
                case AssetState.Differs:
                    throw new LauncherException(ErrorCode.InvalidPack,
                        $"{url} already exists with different content. {src.Name ?? src.Id} was rebuilt without a new version: bump its version and publish again.", RecoveryHint.None);
            }
            var repo = src.Distribution.Repo ?? throw new LauncherException(ErrorCode.InvalidPack, $"{src.Id}: github-release distribution without repo.", RecoveryHint.None);
            var tag = (src.Distribution.TagPrefix ?? string.Empty) + module.Version;
            var title = $"{src.Name ?? src.Id} {module.Version}";
            var notes = $"Built from the host install for {lk.Pack.Name} {lk.Pack.Version}. SHA-256 {module.Artifact.Sha256}.";
            var createArgs = new[] { "release", "create", tag, zip, "--repo", repo, "--title", title, "--notes", notes };
            var uploadArgs = new[] { "release", "upload", tag, zip, "--repo", repo };
            yield return new ExternalAction($"Release asset for {src.Id} {module.Version} (missing at {url})",
                new[] { ExternalTools.Describe("gh", createArgs) + "   # or, if the tag exists:", ExternalTools.Describe("gh", uploadArgs) },
                tools =>
                {
                    var view = tools.Run("gh", new[] { "release", "view", tag, "--repo", repo }, throwOnError: false);
                    tools.Run("gh", view.Ok ? uploadArgs : createArgs);
                    // Re-download and compare: the lock promises this exact hash.
                    var tmp = Path.Combine(Path.GetTempPath(), "vmpl-asset-" + Guid.NewGuid().ToString("N") + ".zip");
                    try
                    {
                        for (var attempt = 0; attempt < 6; attempt++)
                        {
                            Thread.Sleep(attempt == 0 ? 2000 : 5000);
                            try
                            {
                                using var s = _http.GetStreamAsync(url, ct).GetAwaiter().GetResult();
                                using var f = File.Create(tmp);
                                s.CopyTo(f);
                                break;
                            }
                            catch (Exception ex) when (attempt < 5 && (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))) { }
                        }
                        if (!File.Exists(tmp) || !Sha256Util.HexEquals(Sha256Util.HashFile(tmp), module.Artifact.Sha256))
                        {
                            throw new LauncherException(ErrorCode.Tampered, $"The uploaded asset at {url} does not hash to what the lock promises.", RecoveryHint.None);
                        }
                        _log.WriteLine($"  asset verified: {url}");
                    }
                    finally
                    {
                        if (File.Exists(tmp)) { File.Delete(tmp); }
                    }
                });
        }
    }

    /// <summary>Private zips that have no private repository to live in (distribution "private-sideload").</summary>
    private static List<string> HandOverOnly(PackDefinition pack, PackLock lk, PackExporter.Report export)
    {
        var uploaded = lk.Ckan.Modules.Where(m => m.Artifact?.Private is not null).Select(m => m.Artifact!.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return export.SideloadAssets.Where(p => !uploaded.Contains(Path.GetFileName(p))).ToList();
    }

    /// <summary>
    /// Private packages with a private GitHub home: upload the zip as a release asset of the private repository (never
    /// anywhere public: the upload refuses unless GitHub reports the repository as private), then download it back
    /// through gh and compare hashes. Read-only checks run in the dry run too, so the plan says what is missing.
    /// </summary>
    private IEnumerable<ExternalAction> PlanPrivateAssets(PackDefinition pack, PackLock lk, PackExporter.Report export, List<string> warnings)
    {
        foreach (var src in pack.CustomPackages.Where(s => s.Distribution.Kind == "private-github"))
        {
            var module = lk.Ckan.Modules.FirstOrDefault(m => m.Identifier == src.Id);
            var artifact = module?.Artifact;
            var priv = artifact?.Private;
            if (module is null || artifact is null || priv is null)
            {
                continue;
            }
            var zip = export.SideloadAssets.FirstOrDefault(p => string.Equals(Path.GetFileName(p), priv.Asset, StringComparison.OrdinalIgnoreCase));
            if (zip is null)
            {
                warnings.Add($"{src.Id}: the private zip {priv.Asset} was not produced by the export.");
                continue;
            }
            var state = PrivateAssetState(priv);
            if (state == PrivateState.Present)
            {
                if (!PrivateAssetMatches(priv, artifact.Sha256))
                {
                    throw new LauncherException(ErrorCode.InvalidPack,
                        $"{priv.Asset} already exists in {priv.Repo} with different content. {src.Name ?? src.Id} was rebuilt without a new version: commit the change (the version comes from git describe) and publish again.", RecoveryHint.None);
                }
                _log.WriteLine($"Private asset present and identical: {priv.Repo} {priv.Tag}/{priv.Asset}");
                continue;
            }
            if (state == PrivateState.RepoUnreachable)
            {
                warnings.Add($"The private repository {priv.Repo} is not reachable with your gh login. Create it first: gh repo create {priv.Repo} --private");
            }
            var repo = priv.Repo;
            var tag = priv.Tag;
            var title = $"{src.Name ?? src.Id} {module.Version}";
            var notes = $"Private package for {lk.Pack.Name} {lk.Pack.Version}. SHA-256 {artifact.Sha256}. Do not make this repository public.";
            var createArgs = new[] { "release", "create", tag, zip, "--repo", repo, "--title", title, "--notes", notes };
            var uploadArgs = new[] { "release", "upload", tag, zip, "--repo", repo };
            var privacyArgs = new[] { "repo", "view", repo, "--json", "isPrivate", "--jq", ".isPrivate" };
            yield return new ExternalAction($"Private asset for {src.Id} {module.Version} (to private repo {repo})",
                new[] { ExternalTools.Describe("gh", privacyArgs) + "   # must print true", ExternalTools.Describe("gh", createArgs) + "   # or, if the tag exists:", ExternalTools.Describe("gh", uploadArgs) },
                tools =>
                {
                    var isPrivate = tools.Run("gh", privacyArgs, throwOnError: false);
                    if (!isPrivate.Ok || !string.Equals(isPrivate.StdOut.Trim(), "true", StringComparison.Ordinal))
                    {
                        throw new LauncherException(ErrorCode.InvalidPack,
                            $"Refusing to upload {priv.Asset}: {repo} is not a private repository (or does not exist). Create it with: gh repo create {repo} --private",
                            RecoveryHint.None);
                    }
                    var view = tools.Run("gh", new[] { "release", "view", tag, "--repo", repo }, throwOnError: false);
                    tools.Run("gh", view.Ok ? uploadArgs : createArgs);
                    var tmpDir = Path.Combine(Path.GetTempPath(), "vmpl-private-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmpDir);
                    try
                    {
                        tools.Run("gh", new[] { "release", "download", tag, "--repo", repo, "--pattern", priv.Asset, "--dir", tmpDir });
                        var got = Path.Combine(tmpDir, priv.Asset);
                        if (!File.Exists(got) || !Sha256Util.HexEquals(Sha256Util.HashFile(got), artifact.Sha256))
                        {
                            throw new LauncherException(ErrorCode.Tampered, $"The private asset in {repo} does not hash to what the lock promises.", RecoveryHint.None);
                        }
                        _log.WriteLine($"  private asset verified: {repo} {tag}/{priv.Asset}");
                    }
                    finally
                    {
                        Directory.Delete(tmpDir, recursive: true);
                    }
                });
        }
    }

    private enum PrivateState
    {
        Present,
        Missing,
        RepoUnreachable,
    }

    private static bool PrivateAssetMatches(PrivateSourceRef priv, string sha256)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "vmpl-private-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var quiet = new ExternalTools(TextWriter.Null);
            var r = quiet.Run("gh", new[] { "release", "download", priv.Tag, "--repo", priv.Repo, "--pattern", priv.Asset, "--dir", tmpDir }, throwOnError: false, timeout: TimeSpan.FromMinutes(5));
            var got = Path.Combine(tmpDir, priv.Asset);
            return r.Ok && File.Exists(got) && Sha256Util.HexEquals(Sha256Util.HashFile(got), sha256);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static PrivateState PrivateAssetState(PrivateSourceRef priv)
    {
        try
        {
            var quiet = new ExternalTools(TextWriter.Null);
            var release = quiet.Run("gh", new[] { "api", $"repos/{priv.Repo}/releases/tags/{Uri.EscapeDataString(priv.Tag)}", "--jq", "[.assets[].name]" }, throwOnError: false, timeout: TimeSpan.FromSeconds(30));
            if (release.Ok && release.StdOut.Contains($"\"{priv.Asset}\"", StringComparison.Ordinal))
            {
                return PrivateState.Present;
            }
            var repo = quiet.Run("gh", new[] { "api", $"repos/{priv.Repo}", "--jq", ".private" }, throwOnError: false, timeout: TimeSpan.FromSeconds(30));
            return repo.Ok && repo.StdOut.Trim() == "true" ? PrivateState.Missing : PrivateState.RepoUnreachable;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return PrivateState.RepoUnreachable;
        }
    }

    private enum AssetState
    {
        Missing,
        Matches,
        Differs,
    }

    /// <summary>Missing, or present with/without the bytes the lock promises (downloads it to compare).</summary>
    private AssetState OnlineAssetState(string url, string sha256, CancellationToken ct)
    {
        if (!AssetExists(url, ct))
        {
            return AssetState.Missing;
        }
        var tmp = Path.Combine(Path.GetTempPath(), "vmpl-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var s = _http.GetStreamAsync(url, ct).GetAwaiter().GetResult())
            using (var f = File.Create(tmp))
            {
                s.CopyTo(f);
            }
            return Sha256Util.HexEquals(Sha256Util.HashFile(tmp, ct), sha256) ? AssetState.Matches : AssetState.Differs;
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    /// <summary>The private-repository tokens stored on this PC: the export's secret scan must never find them.</summary>
    private static IReadOnlyList<string> HostTokens(PublishConfig c)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }
        var dir = c.KeyDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMPLauncher", "publisher");
        return Core.Private.HostAccessStore.LoadAll(dir).Values.ToList();
    }

    private bool AssetExists(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private ExternalAction PlanServerUpdate(PublishConfig c, string policyPath)
    {
        var hash = Sha256Util.HashFile(policyPath).ToUpperInvariant();
        var upload = "VMP-mod-control-upload.xml";
        var script = ServerUpdateScript(c.ServerRoot!, hash);
        var shown = new[]
        {
            ExternalTools.Describe("scp", new[] { "-o", "BatchMode=yes", policyPath, $"{c.ServerHost}:{upload}" }),
            $"ssh {c.ServerHost} powershell -EncodedCommand <script>   # verify sha256 {hash[..16]}…, Manage-VmpServer Backup, Stop, replace Config\\KSPModControl.xml, Start, confirm running",
        };
        return new ExternalAction($"Update the server policy on {c.ServerHost} (restarts the server)", shown, tools =>
        {
            tools.Scp(policyPath, c.ServerHost!, upload);
            // Backup compresses the whole universe on the server: allow it time rather than killing ssh mid-way.
            var r = tools.SshPowerShell(c.ServerHost!, script, timeout: TimeSpan.FromMinutes(25));
            if (!r.StdOut.Contains("remote: server running", StringComparison.Ordinal))
            {
                throw new LauncherException(ErrorCode.Unknown, "The server did not report running after the policy update; check it by hand.", RecoveryHint.None);
            }
        });
    }

    /// <summary>Same steps as VMP\update-mod-control.ps1's remote block, plus a post-start check.</summary>
    internal static string ServerUpdateScript(string serverRoot, string expectedHashUpper)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine("$ProgressPreference='SilentlyContinue'");
        sb.AppendLine($"$root='{serverRoot.Replace("'", "''")}'");
        sb.AppendLine("$upload=Join-Path $env:USERPROFILE 'VMP-mod-control-upload.xml'");
        sb.AppendLine($"if ((Get-FileHash -LiteralPath $upload -Algorithm SHA256).Hash -ne '{expectedHashUpper}') {{ throw 'Uploaded mod control checksum mismatch' }}");
        sb.AppendLine("'remote: upload checksum OK'");
        sb.AppendLine("$manage=Join-Path $root 'Manage-VmpServer.ps1'");
        sb.AppendLine("try {");
        sb.AppendLine("    & $manage -Action Backup -ServerRoot $root | Out-String | Write-Output");
        sb.AppendLine("    & $manage -Action Stop -ServerRoot $root | Out-String | Write-Output");
        sb.AppendLine("    Copy-Item -LiteralPath $upload -Destination (Join-Path $root 'Config\\KSPModControl.xml') -Force");
        sb.AppendLine("    $generalPath=Join-Path $root 'Config\\GeneralSettings.xml'");
        sb.AppendLine("    [xml]$general=Get-Content -LiteralPath $generalPath -Raw");
        sb.AppendLine("    $general.DocumentElement.ModControl='true'");
        sb.AppendLine("    $general.Save($generalPath)");
        sb.AppendLine("    'remote: policy replaced'");
        sb.AppendLine("} finally {");
        sb.AppendLine("    if (Test-Path -LiteralPath $upload) { Remove-Item -LiteralPath $upload -Force }");
        sb.AppendLine("    & $manage -Action Start -ServerRoot $root | Out-String | Write-Output");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Seconds 5");
        sb.AppendLine("$p = Get-CimInstance Win32_Process -Filter \"Name='Server.exe'\" | Where-Object { $_.ExecutablePath -like \"$root*\" } | Select-Object -First 1");
        sb.AppendLine("if ($null -eq $p) { throw 'Server.exe is not running after start' }");
        sb.AppendLine("'remote: server running since ' + $p.CreationDate + ' pid ' + $p.ProcessId");
        sb.AppendLine("'remote: installed policy sha256 ' + (Get-FileHash -LiteralPath (Join-Path $root 'Config\\KSPModControl.xml') -Algorithm SHA256).Hash");
        return sb.ToString();
    }
}
