using System.Diagnostics;
using CKAN;
using CKAN.IO;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Security;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Core.Transactions;

namespace VmpLauncher.Ckan.Backend;

/// <summary>
/// Install / update / repair / recover for a profile, each as one journaled transaction:
/// nothing in the profile changes before every artifact is verified, a hard-link snapshot precedes the first
/// write, and any failure or cancellation after that point restores the snapshot (files, CKAN registry, state).
/// </summary>
public sealed class ProfileOperations
{
    public sealed record LockSource(string LockPath, string InventoryPath);

    public sealed record Request(ProfilePaths Profile, LockSource Lock, string CacheDir, bool Offline, IReadOnlyDictionary<string, string>? SideloadFiles = null, bool KeepSnapshot = false);

    public sealed record Report(string Operation, string TxnId, int ModulesInstalled, int ModulesRemoved, int FilesVerified, long Bytes, long BytesDownloaded, TimeSpan Elapsed, IReadOnlyList<string> Warnings);

    public sealed record RepairReport(string? TxnId, int FilesRestored, int OverridesReapplied, int Quarantined, IReadOnlyList<string> Remaining, TimeSpan Elapsed);

    private readonly LauncherPaths _paths;
    private readonly TextWriter _log;
    private readonly IProgress<CkanProgress>? _progress;

    public ProfileOperations(LauncherPaths paths, TextWriter log, IProgress<CkanProgress>? progress = null)
    {
        _paths = paths;
        _log = log;
        _progress = progress;
    }

    /// <summary>Hook for tests: invoked after each CKAN module completes; may throw or cancel.</summary>
    public Action<CkanModule>? FaultInjector { get; set; }

    public static (PackLock Lock, InventoryDocument Inventory, string LockSha256) LoadAndValidate(LockSource src)
    {
        var lockDoc = LauncherJson.ReadFile<PackLock>(src.LockPath);
        if (lockDoc.Schema != "vmpack-lock/1")
        {
            throw new LauncherException(ErrorCode.InvalidLock, $"Unsupported lock schema '{lockDoc.Schema}'.");
        }
        var inventory = InventoryFile.Read(src.InventoryPath, lockDoc.Inventory.Sha256);
        if (inventory.Files.Count != lockDoc.Inventory.FileCount)
        {
            throw new LauncherException(ErrorCode.InvalidLock, "The inventory does not match the lock (file count).");
        }
        var problems = OwnershipValidator.Validate(inventory.Files, lockDoc.Overrides, lockDoc.Ckan.RootInstallOwners);
        if (problems.Count > 0)
        {
            throw new LauncherException(ErrorCode.InvalidLock, "The lock failed ownership validation.", details: problems.Select(p => $"[{p.Code}] {p.Message}").ToList());
        }
        return (lockDoc, inventory, Sha256Util.HashFile(src.LockPath));
    }

    public Report Install(Request r, CancellationToken ct = default)
    {
        if (InstalledState.TryRead(r.Profile) is not null)
        {
            throw new LauncherException(ErrorCode.InstallFailed, "This profile already has a pack installed; use update or repair.");
        }
        return RunTransaction("install", r, (session, _, _) =>
        {
            _log.WriteLine($"Installing {session.Packages.Count} modules with CKAN...");
            session.InstallAll(session.Modules.Where(m => !m.IsDLC).ToList(), WrapProgress(session), ct);
            return (session.Packages.Count, 0);
        }, ct);
    }

    public Report Update(Request r, CancellationToken ct = default)
    {
        var state = InstalledState.TryRead(r.Profile) ?? throw new LauncherException(ErrorCode.InstallFailed, "Nothing is installed in this profile; use install.");
        var previous = LauncherJson.ReadFile<PackLock>(r.Profile.LockCopyFile);
        return RunTransaction("update", r, (session, lockDoc, _) =>
        {
            var diff = LockDiff.Compute(previous, lockDoc);
            _log.WriteLine($"Update plan: {diff.Summary()}");
            var toInstall = diff.ModulesToInstall.Select(lm => session.Modules.First(m => m.identifier == lm.Identifier)).ToList();
            var toRemove = diff.IdentifiersToUninstall.ToList();
            // Overrides whose owner is being replaced must be undone so CKAN finds its own files.
            var affectedOwners = toRemove.ToHashSet(StringComparer.Ordinal);
            OverrideApplier.Unapply(r.Profile, previous.Overrides.Where(o => o.Owner is not null && affectedOwners.Contains(o.Owner)).Concat(diff.OverridesRemoved).Concat(diff.OverridesChanged.Select(c => previous.Overrides.First(p => p.Id == c.Id))));
            if (toRemove.Count > 0)
            {
                _log.WriteLine($"Removing {toRemove.Count} modules...");
                session.Uninstall(toRemove, toInstall, ct);
            }
            if (toInstall.Count > 0)
            {
                _log.WriteLine($"Installing {toInstall.Count} modules...");
                session.InstallAll(toInstall, WrapProgress(session), ct);
            }
            return (toInstall.Count, toRemove.Count);
        }, ct, previousLockSha: state.LockSha256);
    }

    private IProgress<CkanProgress>? WrapProgress(ProfileSession session)
    {
        if (FaultInjector is null)
        {
            return _progress;
        }
        session.ModuleCompleted = FaultInjector;
        return _progress;
    }

    private Report RunTransaction(string operation, Request r, Func<ProfileSession, PackLock, InventoryDocument, (int Installed, int Removed)> apply, CancellationToken ct, string? previousLockSha = null)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var (lockDoc, inventory, lockSha) = LoadAndValidate(r.Lock);
        _log.WriteLine($"Lock {lockDoc.Pack.Id} {lockDoc.Pack.Version}: {lockDoc.Ckan.Modules.Count} modules, {inventory.Files.Count} files, {lockDoc.Inventory.TotalBytes:N0} bytes.");

        r.Profile.EnsureCreated();
        ProfilePathValidator.Validate(r.Profile.Root, lockDoc.Inventory.MaxRelPathLength, null, r.CacheDir);
        if (OperatingSystem.IsWindows())
        {
            GameProcessGuard.ThrowIfRunning(r.Profile.GameDir);
        }
        if (TransactionJournal.FindIncomplete(r.Profile) is { } incomplete)
        {
            throw new LauncherException(ErrorCode.InstallFailed, $"A previous {incomplete.Operation} did not finish (state {incomplete.State}). Restore the profile first.", RecoveryHint.Restore,
                details: new[] { $"transaction {incomplete.TxnId} started {incomplete.StartedUtc:u}" });
        }

        using var profileLock = ProfileLock.Acquire(r.Profile, operation);
        var txnId = TransactionJournal.NewId();
        var journal = new TransactionJournal { TxnId = txnId, Operation = operation, PreviousLockSha256 = previousLockSha, TargetLockSha256 = lockSha, TargetPackVersion = lockDoc.Pack.Version };
        journal.Write(r.Profile);

        var user = new VmpCkanUser(progress: _progress);
        var session = new ProfileSession(_paths, r.Profile, lockDoc, r.CacheDir, user);
        var snapshotTaken = false;
        try
        {
            _log.WriteLine("Resolving...");
            session.AssertExactResolution();

            // Files the user already has (private packages, or archives handed over out of band) go into the cache
            // first, hash-verified, so they are neither downloaded nor reported missing.
            foreach (var (identifier, file) in r.SideloadFiles ?? new Dictionary<string, string>())
            {
                var module = session.Modules.FirstOrDefault(m => m.identifier == identifier);
                var lm = lockDoc.Ckan.Modules.FirstOrDefault(m => m.Identifier == identifier);
                if (module is not null && lm?.Artifact is not null && File.Exists(file) && session.Cache.GetCachedFilename(module) is null)
                {
                    session.Sideload(module, lm.Artifact, file, ct);
                    _log.WriteLine($"  pre-seeded {identifier} from {Path.GetFileName(file)}");
                }
            }
            var plan = session.PlanPrefetch(ct);
            _log.WriteLine($"Artifacts: {plan.Verified.Count} verified in cache, {plan.ToDownload.Count} to download ({plan.BytesToDownload:N0} bytes), {plan.Sideload.Count} private, {plan.Corrupt.Count} corrupt purged.");
            foreach (var c in plan.Corrupt)
            {
                warnings.Add($"Cached archive for {c.Module.identifier} was corrupt (had {c.Actual[..12]}, expected {c.Expected[..12]}) and was removed.");
            }
            HandleSideloads(session, plan, r.SideloadFiles, ct);
            CheckDiskSpace(r, lockDoc, plan);
            if (plan.ToDownload.Count > 0)
            {
                if (r.Offline)
                {
                    throw new LauncherException(ErrorCode.ArtifactUnavailable, $"Offline mode: {plan.ToDownload.Count} archives are not in the cache.", RecoveryHint.Retry,
                        details: plan.ToDownload.Select(m => $"{m.identifier} {m.version}").ToList());
                }
                _log.WriteLine($"Downloading {plan.ToDownload.Count} archives...");
                session.Download(plan.ToDownload, _progress, ct);
            }
            var fetcher = new ArtifactFetcher(r.CacheDir);
            foreach (var o in lockDoc.Overrides.Where(o => o.Content?.Artifact is not null))
            {
                fetcher.Fetch(o.Content!.Artifact!, ct);
            }
            journal = journal.Advance(TxnState.Prefetched);
            journal.Write(r.Profile);

            // ---- from here on the profile changes ----
            journal = journal.Advance(TxnState.Snapshotting) with { SnapshotDir = Path.Combine(r.Profile.SnapshotsDir, txnId) };
            journal.Write(r.Profile);
            var rootFiles = inventory.Files.Where(f => !f.Path.Contains('/')).Select(f => f.Path).ToList();
            var manifest = SnapshotEngine.Create(r.Profile, lockDoc.FileRules, txnId, rootFiles, ct);
            snapshotTaken = true;
            _log.WriteLine($"Snapshot: {manifest.Files.Count} files linked.");
            journal = journal.Advance(TxnState.Snapshotted);
            journal.Write(r.Profile);

            journal = journal.Advance(TxnState.Applying);
            journal.Write(r.Profile);
            var applySw = Stopwatch.StartNew();
            var (installed, removed) = apply(session, lockDoc, inventory);
            _log.WriteLine($"CKAN apply finished in {applySw.Elapsed}.");

            journal = journal.Advance(TxnState.Overriding);
            journal.Write(r.Profile);
            var notes = OverrideApplier.Apply(r.Profile, lockDoc.Overrides, a => fetcher.Fetch(a, ct));
            foreach (var n in notes)
            {
                _log.WriteLine($"  override {n}");
            }
            session.Rescan();

            journal = journal.Advance(TxnState.Verifying);
            journal.Write(r.Profile);
            var result = VerifyAll(session, lockDoc, inventory, ct);

            // ---- commit ----
            new InstalledState { LockSha256 = lockSha, PackId = lockDoc.Pack.Id, PackVersion = lockDoc.Pack.Version, AppliedUtc = DateTimeOffset.UtcNow, Files = result.Records }.Write(r.Profile);
            File.Copy(r.Lock.LockPath, r.Profile.LockCopyFile, overwrite: true);
            File.Copy(r.Lock.InventoryPath, r.Profile.InventoryCopyFile, overwrite: true);
            journal = journal.Advance(TxnState.Committed);
            journal.Write(r.Profile);
            session.Dispose();
            CleanCkanTempDirs(r.Profile);
            if (!r.KeepSnapshot)
            {
                SnapshotEngine.Delete(r.Profile, txnId);
            }
            foreach (var u in result.UnknownFiles.Take(20))
            {
                warnings.Add($"Unknown file in GameData (left alone): {u}");
            }
            return new Report(operation, txnId, installed, removed, result.FilesChecked, lockDoc.Inventory.TotalBytes, plan.BytesToDownload, sw.Elapsed, warnings);
        }
        catch (Exception ex)
        {
            session.Dispose();
            if (!snapshotTaken)
            {
                journal = journal.Advance(TxnState.RolledBack, Describe(ex));
                journal.Write(r.Profile);
                throw;
            }
            _log.WriteLine($"Failure after snapshot ({Describe(ex)}); restoring...");
            journal = journal.Advance(TxnState.RollingBack, Describe(ex));
            journal.Write(r.Profile);
            try
            {
                var rb = RollbackEngine.Restore(r.Profile, journal, lockDoc.FileRules, CancellationToken.None);
                _log.WriteLine($"Restored: {rb.Untouched} untouched, {rb.Restored} restored, {rb.Quarantined} quarantined, {rb.StateFilesRestored} state files in {rb.Elapsed}; {rb.Errors.Count} errors.");
                journal = journal.Advance(rb.Errors.Count == 0 ? TxnState.RolledBack : TxnState.RollbackFailed) with { Notes = rb.Errors };
                journal.Write(r.Profile);
                if (rb.Errors.Count == 0 && !r.KeepSnapshot)
                {
                    SnapshotEngine.Delete(r.Profile, txnId);
                }
            }
            catch (Exception rbEx)
            {
                journal = journal.Advance(TxnState.RollbackFailed, Describe(rbEx));
                journal.Write(r.Profile);
                throw new LauncherException(ErrorCode.RollbackFailed, "The operation failed and the profile could not be restored automatically. Your previous files are kept in the snapshot folder.", RecoveryHint.Restore, rbEx,
                    new[] { Describe(ex), Path.Combine(r.Profile.SnapshotsDir, txnId) });
            }
            throw;
        }
    }

    /// <summary>Restores a profile whose last transaction did not finish (crash, power loss).</summary>
    public RollbackEngine.Result? Recover(ProfilePaths profile, CancellationToken ct = default)
    {
        var journal = TransactionJournal.FindIncomplete(profile);
        if (journal is null)
        {
            return null;
        }
        using var profileLock = ProfileLock.Acquire(profile, "recover");
        var rules = File.Exists(profile.LockCopyFile) ? LauncherJson.ReadFile<PackLock>(profile.LockCopyFile).FileRules : Core.Packs.FileRules.Default;
        RegistryManager.DisposeAll();
        var updated = journal.Advance(TxnState.RollingBack);
        updated.Write(profile);
        var rb = RollbackEngine.Restore(profile, updated, rules, ct);
        updated = updated.Advance(rb.Errors.Count == 0 ? TxnState.RolledBack : TxnState.RollbackFailed) with { Notes = rb.Errors };
        updated.Write(profile);
        if (rb.Errors.Count == 0)
        {
            SnapshotEngine.Delete(profile, journal.TxnId);
        }
        return rb;
    }

    /// <summary>Verifies and, when needed, restores damaged or missing files from the cached archives (registry untouched).</summary>
    public RepairReport Repair(ProfilePaths profile, string cacheDir, bool quarantineUnknownDlls, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var state = InstalledState.TryRead(profile) ?? throw new LauncherException(ErrorCode.InstallFailed, "Nothing is installed in this profile.");
        var lockDoc = LauncherJson.ReadFile<PackLock>(profile.LockCopyFile);
        var inventory = InventoryFile.Read(profile.InventoryCopyFile, lockDoc.Inventory.Sha256);
        if (OperatingSystem.IsWindows())
        {
            GameProcessGuard.ThrowIfRunning(profile.GameDir);
        }
        var verifier = new InstallVerifier(VerifyProgress());
        var before = verifier.Verify(profile, inventory, lockDoc.FileRules, InstallVerifier.Mode.Full, state, ct);
        _log.WriteLine($"Verify: {before.Missing.Count} missing, {before.Modified.Count} modified, {before.MutableDrift.Count} drift, {before.UnknownDlls.Count} unknown DLLs.");
        var overridePaths = lockDoc.Overrides.Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabledTargets = lockDoc.Overrides.Where(o => o.Op == "disable").Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A disabled file is expected to be missing from its original path.
        var toFix = before.Missing.Concat(before.Modified).Where(p => !disabledTargets.Contains(p.Path)).ToList();
        if (toFix.Count == 0 && (!quarantineUnknownDlls || before.UnknownDlls.Count == 0))
        {
            return new RepairReport(null, 0, 0, 0, Array.Empty<string>(), sw.Elapsed);
        }

        using var profileLock = ProfileLock.Acquire(profile, "repair");
        var txnId = TransactionJournal.NewId();
        var journal = new TransactionJournal { TxnId = txnId, Operation = "repair", PreviousLockSha256 = state.LockSha256, TargetLockSha256 = state.LockSha256, TargetPackVersion = state.PackVersion };
        journal = journal.Advance(TxnState.Snapshotting);
        journal.Write(profile);
        var rootFiles = inventory.Files.Where(f => !f.Path.Contains('/')).Select(f => f.Path).ToList();
        SnapshotEngine.Create(profile, lockDoc.FileRules, txnId, rootFiles, ct);
        journal = journal.Advance(TxnState.Applying);
        journal.Write(profile);
        var user = new VmpCkanUser(progress: _progress);
        var session = new ProfileSession(_paths, profile, lockDoc, cacheDir, user);
        var restored = 0; var overridesReapplied = 0; var quarantined = 0;
        try
        {
            var byOwner = toFix.GroupBy(p => p.Owner, StringComparer.Ordinal);
            var invByPath = inventory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var fetcher = new ArtifactFetcher(cacheDir);
            foreach (var group in byOwner)
            {
                ct.ThrowIfCancellationRequested();
                var owner = group.Key;
                var module = session.Modules.FirstOrDefault(m => m.identifier == owner);
                if (module is null)
                {
                    // override-produced files
                    overridesReapplied += group.Count();
                    continue;
                }
                var lm = lockDoc.Ckan.Modules.First(m => m.Identifier == owner);
                var zip = session.Cache.GetCachedFilename(module);
                if (zip is null || !Sha256Util.HexEquals(session.VerifiedIndex.Sha256Of(zip, ct), lm.Artifact!.Sha256))
                {
                    _log.WriteLine($"  fetching archive for {owner}...");
                    session.Download(new[] { module }, _progress, ct);
                    zip = session.Cache.GetCachedFilename(module) ?? throw new LauncherException(ErrorCode.ArtifactUnavailable, $"No archive for {owner}.", RecoveryHint.Retry);
                }
                var entriesByDest = ModuleInstaller.FindInstallableFiles(module, zip, session.Game)
                    .Where(f => !f.source.IsDirectory)
                    .ToDictionary(f => CKANPathUtils.NormalizePath(f.destination), f => f.source.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var problem in group)
                {
                    if (!entriesByDest.TryGetValue(problem.Path, out var entryName))
                    {
                        throw new LauncherException(ErrorCode.VerifyFailed, $"{problem.Path} is owned by {owner} but its archive does not contain it.", RecoveryHint.AskHost);
                    }
                    var inv = invByPath[problem.Path];
                    var dest = SafePath.Resolve(profile.GameDir, problem.Path);
                    SafeZip.ExtractEntryVerified(zip, entryName, dest, inv.Sha256, inv.Size);
                    restored++;
                    _log.WriteLine($"  restored {problem.Path} ({owner})");
                }
            }
            var notes = OverrideApplier.Apply(profile, lockDoc.Overrides, a => fetcher.Fetch(a, ct));
            overridesReapplied += notes.Count(n => !n.EndsWith("already applied", StringComparison.Ordinal) && !n.EndsWith("already disabled", StringComparison.Ordinal));
            if (quarantineUnknownDlls)
            {
                var qdir = Path.Combine(profile.QuarantineDir, txnId);
                foreach (var rel in before.UnknownDlls)
                {
                    var abs = Path.Combine(profile.GameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    var dst = Path.Combine(qdir, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Move(abs, dst, overwrite: true);
                    quarantined++;
                    _log.WriteLine($"  quarantined unknown DLL {rel}");
                }
            }
            journal = journal.Advance(TxnState.Verifying);
            journal.Write(profile);
            var after = verifier.Verify(profile, inventory, lockDoc.FileRules, InstallVerifier.Mode.Full, null, ct);
            var remaining = after.Missing.Concat(after.Modified).Where(p => !disabledTargets.Contains(p.Path)).Select(p => $"{p.Kind} {p.Path} ({p.Owner})").ToList();
            if (remaining.Count > 0)
            {
                throw new LauncherException(ErrorCode.VerifyFailed, "Some files could not be repaired.", RecoveryHint.AskHost, details: remaining);
            }
            new InstalledState { LockSha256 = state.LockSha256, PackId = state.PackId, PackVersion = state.PackVersion, AppliedUtc = DateTimeOffset.UtcNow, Files = after.Records }.Write(profile);
            journal = journal.Advance(TxnState.Committed);
            journal.Write(profile);
            session.Dispose();
            SnapshotEngine.Delete(profile, txnId);
            return new RepairReport(txnId, restored, overridesReapplied, quarantined, Array.Empty<string>(), sw.Elapsed);
        }
        catch (Exception ex)
        {
            session.Dispose();
            journal = journal.Advance(TxnState.RollingBack, Describe(ex));
            journal.Write(profile);
            var rb = RollbackEngine.Restore(profile, journal, lockDoc.FileRules, CancellationToken.None);
            journal = journal.Advance(rb.Errors.Count == 0 ? TxnState.RolledBack : TxnState.RollbackFailed) with { Notes = rb.Errors };
            journal.Write(profile);
            throw;
        }
    }

    private InstallVerifier.Result VerifyAll(ProfileSession session, PackLock lockDoc, InventoryDocument inventory, CancellationToken ct)
    {
        _log.WriteLine("Verifying registry against lock...");
        // CKAN's registry lists what CKAN installed: owned files, plus files an override later replaced or
        // disabled (CKAN still owns those paths). Files added by overrides are not CKAN's.
        var ownedPaths = inventory.Files.Where(f => f.Class is FileClass.Owned or FileClass.MutableOwned or FileClass.Custom).Select(f => f.Path)
            .Concat(lockDoc.Overrides.Where(o => o.Op is "replace" or "disable").Select(o => o.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var regProblems = session.VerifyRegistryAgainstLock(ownedPaths);
        if (regProblems.Count > 0)
        {
            throw new LauncherException(ErrorCode.VerifyFailed, "The CKAN registry does not match the lock after install.", RecoveryHint.Repair, details: regProblems);
        }
        _log.WriteLine("Verifying files (full hash)...");
        var verifier = new InstallVerifier(VerifyProgress());
        var result = verifier.Verify(session.Profile, inventory, lockDoc.FileRules, InstallVerifier.Mode.Full, null, ct);
        _log.WriteLine($"Verified {result.FilesChecked} files ({result.FilesHashed} hashed) in {result.Elapsed}: {result.Missing.Count} missing, {result.Modified.Count} modified, {result.MutableDrift.Count} drift, {result.UnknownFiles.Count} unknown ({result.UnknownDlls.Count} DLLs).");
        // Disabled override targets are expected to be missing from their original path.
        var disabled = lockDoc.Overrides.Where(o => o.Op == "disable").Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = result.Missing.Where(p => !disabled.Contains(p.Path)).ToList();
        if (missing.Count > 0 || result.Modified.Count > 0 || result.UnknownDlls.Count > 0)
        {
            var details = missing.Concat(result.Modified).Select(p => $"{p.Kind} {p.Path} ({p.Owner}): {p.Detail}").Concat(result.UnknownDlls.Select(u => $"unknown dll {u}")).Take(50).ToList();
            throw new LauncherException(ErrorCode.VerifyFailed, "Installed files do not match the pack.", RecoveryHint.Repair, details: details);
        }
        return result;
    }

    private Progress<(int Done, int Total)> VerifyProgress()
        => new Progress<(int Done, int Total)>(p => _progress?.Report(new CkanProgress($"Verifying files {p.Done}/{p.Total}", p.Total == 0 ? 0 : p.Done * 100 / p.Total, null)));

    private void HandleSideloads(ProfileSession session, PrefetchPlan plan, IReadOnlyDictionary<string, string>? provided, CancellationToken ct)
    {
        if (plan.Sideload.Count == 0)
        {
            return;
        }
        provided ??= new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var (module, artifact) in plan.Sideload)
        {
            if (provided.TryGetValue(module.identifier, out var file) && File.Exists(file))
            {
                session.Sideload(module, artifact, file, ct);
                _log.WriteLine($"  sideloaded {module.identifier} from {Path.GetFileName(file)}");
            }
            else
            {
                missing.Add($"{module.identifier} {module.version}: {artifact.FileName ?? "(archive)"}");
            }
        }
        if (missing.Count > 0)
        {
            throw new LauncherException(ErrorCode.PrivateArtifactNeeded, "This pack includes private packages the host must give you.", RecoveryHint.ProvideFile, details: missing);
        }
    }

    private static void CheckDiskSpace(Request r, PackLock lockDoc, PrefetchPlan plan)
    {
        var gameDrive = new DriveInfo(Path.GetPathRoot(r.Profile.GameDir)!);
        var needed = lockDoc.Inventory.TotalBytes + (ProfilePathValidator.SameVolume(r.Profile.GameDir, r.CacheDir) ? plan.BytesToDownload : 0) + 512L * 1024 * 1024;
        if (gameDrive.AvailableFreeSpace < needed)
        {
            throw new LauncherException(ErrorCode.NotEnoughDiskSpace,
                $"Not enough space on {gameDrive.Name}: need about {needed / (1024.0 * 1024 * 1024):F1} GB free, have {gameDrive.AvailableFreeSpace / (1024.0 * 1024 * 1024):F1} GB.",
                RecoveryHint.FreeDiskSpace);
        }
    }

    public static void CleanCkanTempDirs(ProfilePaths profile)
    {
        if (!Directory.Exists(profile.CkanDir))
        {
            return;
        }
        foreach (var dir in Directory.EnumerateDirectories(profile.CkanDir, "TxFileMgr-*"))
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static string Describe(Exception ex) => ex is LauncherException le ? $"{le.Code}: {le.Message}" : $"{ex.GetType().Name}: {ex.Message}";
}
