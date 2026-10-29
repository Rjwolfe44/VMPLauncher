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
/// Once the journal says Committed nothing undoes the transaction; later cleanup failures are only warnings.
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
            // A removed or changed replace/disable override leaves its owner's file overwritten or renamed. The owner is
            // reinstalled so the file is CKAN's own content again before the new overrides (if any) apply.
            var oldOverrides = diff.OverridesRemoved
                .Concat(diff.OverridesChanged.Select(c => previous.Overrides.First(p => p.Id == c.Id)))
                .ToList();
            foreach (var owner in oldOverrides.Where(o => o.Op is "replace" or "disable").Select(o => o.Owner).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var module = session.Modules.FirstOrDefault(m => m.identifier == owner);
                if (module is null || toRemove.Contains(owner, StringComparer.Ordinal) || session.Registry.InstalledModule(owner) is null)
                {
                    continue; // removed or replaced anyway
                }
                _log.WriteLine($"  reinstalling {owner}: an override of its files was removed or changed");
                toRemove.Add(owner);
                toInstall.Add(module);
            }
            // Overrides whose owner is being uninstalled must be undone so CKAN finds its own files.
            var affectedOwners = toRemove.ToHashSet(StringComparer.Ordinal);
            OverrideApplier.Unapply(r.Profile, previous.Overrides.Where(o => o.Owner is not null && affectedOwners.Contains(o.Owner)).Concat(oldOverrides).DistinctBy(o => o.Id));
            if (toRemove.Count > 0)
            {
                _log.WriteLine($"Removing {toRemove.Count} modules...");
                session.Uninstall(toRemove, toInstall, ct);
            }
            // CKAN also removes auto-installed modules nothing depends on any more (and reverse dependencies); the
            // lock may still pin them, and the diff calls them unchanged, so put back every pinned module that is gone.
            var dropped = session.ModulesMissingFromRegistry().Where(m => !toInstall.Any(t => t.identifier == m.identifier)).ToList();
            if (dropped.Count > 0)
            {
                _log.WriteLine($"  reinstalling {dropped.Count} pinned modules CKAN removed: {string.Join(", ", dropped.Select(m => m.identifier))}");
                toInstall.AddRange(dropped);
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

        using var profileLock = ProfileLock.Acquire(r.Profile, operation);
        ThrowIfIncomplete(r.Profile);
        // Game-root files the pack owned before this transaction (its own inventory adds the ones it will own).
        var previousRootFiles = previousLockSha is null ? Array.Empty<string>() : PreviousRootFiles(r.Profile);
        var txnId = TransactionJournal.NewId();
        var journal = new TransactionJournal { TxnId = txnId, Operation = operation, PreviousLockSha256 = previousLockSha, TargetLockSha256 = lockSha, TargetPackVersion = lockDoc.Pack.Version };
        journal.Write(r.Profile);

        ProfileSession? session = null;
        var snapshotTaken = false;
        var committed = false;
        PrefetchPlan plan;
        InstallVerifier.Result result;
        int installed, removed;
        try
        {
            var user = new VmpCkanUser(progress: _progress);
            session = new ProfileSession(_paths, r.Profile, lockDoc, r.CacheDir, user);
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
            plan = session.PlanPrefetch(ct);
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
            var rootFiles = RootFilesOf(inventory).Concat(previousRootFiles);
            var manifest = SnapshotEngine.Create(r.Profile, lockDoc.FileRules, txnId, rootFiles, ct);
            snapshotTaken = true;
            _log.WriteLine($"Snapshot: {manifest.Files.Count} files linked.");
            journal = journal.Advance(TxnState.Snapshotted);
            journal.Write(r.Profile);

            journal = journal.Advance(TxnState.Applying);
            journal.Write(r.Profile);
            var applySw = Stopwatch.StartNew();
            (installed, removed) = apply(session, lockDoc, inventory);
            _log.WriteLine($"CKAN apply finished in {applySw.Elapsed}.");

            journal = journal.Advance(TxnState.Overriding);
            journal.Write(r.Profile);
            session.AssertOverrideTargets(lockDoc.Overrides);
            var notes = OverrideApplier.Apply(r.Profile, lockDoc.Overrides, a => fetcher.Fetch(a, ct));
            foreach (var n in notes)
            {
                _log.WriteLine($"  override {n}");
            }
            session.Rescan();

            journal = journal.Advance(TxnState.Verifying);
            journal.Write(r.Profile);
            result = VerifyAll(session, lockDoc, inventory, ct);

            // ---- commit ----
            new InstalledState { LockSha256 = lockSha, PackId = lockDoc.Pack.Id, PackVersion = lockDoc.Pack.Version, AppliedUtc = DateTimeOffset.UtcNow, Files = result.Records }.Write(r.Profile);
            CopyAtomically(r.Lock.LockPath, r.Profile.LockCopyFile);
            CopyAtomically(r.Lock.InventoryPath, r.Profile.InventoryCopyFile);
            journal = journal.Advance(TxnState.Committed);
            journal.Write(r.Profile);
            committed = true;
        }
        catch (Exception ex) when (!committed)
        {
            DisposeQuietly(session);
            if (!snapshotTaken)
            {
                // The game folder was not touched. A snapshot cut short (failure or cancel while linking) is useless.
                if (journal.SnapshotDir is not null)
                {
                    TryCleanup("delete the partial snapshot", () => SnapshotEngine.Delete(r.Profile, txnId));
                }
                journal = journal.Advance(TxnState.RolledBack, Describe(ex));
                journal.Write(r.Profile);
                throw;
            }
            _log.WriteLine($"Failure after snapshot ({Describe(ex)}); restoring...");
            RollBack(r.Profile, ref journal, ex, r.KeepSnapshot);
            throw;
        }

        // ---- after commit: nothing below may undo the transaction ----
        warnings.AddRange(CleanUpAfterCommit(session, r.Profile, txnId, r.KeepSnapshot));
        foreach (var u in result.UnknownFiles.Take(20))
        {
            warnings.Add($"Unknown file in GameData (left alone): {u}");
        }
        return new Report(operation, txnId, installed, removed, result.FilesChecked, lockDoc.Inventory.TotalBytes, plan.BytesToDownload, sw.Elapsed, warnings);
    }

    /// <summary>Restores the snapshot after a failure; updates the journal to RolledBack or RollbackFailed.</summary>
    private void RollBack(ProfilePaths profile, ref TransactionJournal journal, Exception cause, bool keepSnapshot)
    {
        journal = journal.Advance(TxnState.RollingBack, Describe(cause));
        journal.Write(profile);
        try
        {
            var rb = RollbackEngine.Restore(profile, journal, null, CancellationToken.None);
            _log.WriteLine($"Restored: {rb.Untouched} untouched, {rb.Restored} restored, {rb.Quarantined} quarantined, {rb.StateFilesRestored} state files in {rb.Elapsed}; {rb.Errors.Count} errors.");
            journal = journal.Advance(rb.Errors.Count == 0 ? TxnState.RolledBack : TxnState.RollbackFailed) with { Notes = rb.Errors };
            journal.Write(profile);
            if (rb.Errors.Count == 0 && !keepSnapshot)
            {
                var txnId = journal.TxnId;
                TryCleanup("delete the snapshot", () => SnapshotEngine.Delete(profile, txnId));
            }
        }
        catch (Exception rbEx)
        {
            journal = journal.Advance(TxnState.RollbackFailed, Describe(rbEx));
            journal.Write(profile);
            throw new LauncherException(ErrorCode.RollbackFailed, "The operation failed and the profile could not be restored automatically. Your previous files are kept in the snapshot folder.", RecoveryHint.Restore, rbEx,
                new[] { Describe(cause), Path.Combine(profile.SnapshotsDir, journal.TxnId) });
        }
    }

    /// <summary>
    /// Restores a profile whose last transaction did not finish (crash, power loss, failed rollback). Never leaves the
    /// journal blocking the profile for good: an interruption before the game folder changed, or a missing snapshot,
    /// closes the journal; a restore that still has errors on its second attempt closes it too (snapshot kept), so
    /// Repair can deal with the remaining files.
    /// </summary>
    public RollbackEngine.Result? Recover(ProfilePaths profile, CancellationToken ct = default)
    {
        using var profileLock = ProfileLock.Acquire(profile, "recover");
        var journal = TransactionJournal.FindIncomplete(profile);
        if (journal is null)
        {
            return null;
        }
        var sw = Stopwatch.StartNew();
        RegistryManager.DisposeAll();
        var txnId = journal.TxnId;
        void Close(TransactionJournal j, TxnState state, IReadOnlyList<string> notes) => (j.Advance(state) with { Notes = notes }).Write(profile);
        var manifest = SnapshotEngine.Read(profile, txnId);
        if (journal.State <= TxnState.Snapshotted)
        {
            // Interrupted before Applying: nothing in the game folder changed. A partial snapshot is useless.
            TryCleanup("delete the partial snapshot", () => SnapshotEngine.Delete(profile, txnId));
            Close(journal, TxnState.RolledBack, new[] { $"interrupted while {journal.State}, before the game folder changed; nothing to restore" });
            _log.WriteLine($"Transaction {txnId} stopped before changing the game folder; closed it.");
            return new RollbackEngine.Result(0, 0, 0, 0, sw.Elapsed, Array.Empty<string>());
        }
        if (manifest is null)
        {
            // Without the snapshot nothing can be restored, and trying again would block the profile forever.
            var message = $"The snapshot of transaction {txnId} is missing, so the profile could not be restored. Run Repair to check it.";
            Close(journal, TxnState.RolledBack, new[] { message });
            _log.WriteLine(message);
            return new RollbackEngine.Result(0, 0, 0, 0, sw.Elapsed, new[] { message });
        }

        var isRetry = journal.State == TxnState.RollbackFailed;
        var updated = journal.Advance(TxnState.RollingBack);
        updated.Write(profile);
        RollbackEngine.Result rb;
        try
        {
            rb = RollbackEngine.Restore(profile, updated, manifest.Rules is null ? LockCopyRules(profile) : null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rb = new RollbackEngine.Result(0, 0, 0, 0, sw.Elapsed, new[] { $"restore failed: {Describe(ex)}" });
        }
        if (rb.Errors.Count == 0)
        {
            Close(updated, TxnState.RolledBack, rb.Errors);
            TryCleanup("delete the snapshot", () => SnapshotEngine.Delete(profile, txnId));
        }
        else if (!isRetry)
        {
            // Errors may be passing (a file in use): one more Recover retries.
            Close(updated, TxnState.RollbackFailed, rb.Errors);
        }
        else
        {
            // Second failed attempt: close the journal so the profile is usable; the snapshot stays for manual recovery.
            Close(updated, TxnState.RolledBack, rb.Errors.Prepend("restore finished with errors twice; snapshot kept; run Repair").ToList());
        }
        return rb;
    }

    /// <summary>Verifies and, when needed, restores damaged or missing files from the cached archives (registry untouched).</summary>
    /// <param name="sideloadFiles">identifier → local archive, as <see cref="Request.SideloadFiles"/>: used instead of downloading (private packages).</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last",
        Justification = "sideloadFiles was added after ct so existing positional calls Repair(profile, cache, quarantine, token) keep compiling.")]
    public RepairReport Repair(ProfilePaths profile, string cacheDir, bool quarantineUnknownDlls, CancellationToken ct = default, IReadOnlyDictionary<string, string>? sideloadFiles = null)
    {
        var sw = Stopwatch.StartNew();
        var state = InstalledState.TryRead(profile) ?? throw new LauncherException(ErrorCode.InstallFailed, "Nothing is installed in this profile.");
        var lockDoc = LauncherJson.ReadFile<PackLock>(profile.LockCopyFile);
        var inventory = InventoryFile.Read(profile.InventoryCopyFile, lockDoc.Inventory.Sha256);
        if (OperatingSystem.IsWindows())
        {
            GameProcessGuard.ThrowIfRunning(profile.GameDir);
        }
        using var profileLock = ProfileLock.Acquire(profile, "repair");
        ThrowIfIncomplete(profile);
        var verifier = new InstallVerifier(VerifyProgress());
        var before = verifier.Verify(profile, inventory, lockDoc.FileRules, InstallVerifier.Mode.Full, state, ct);
        _log.WriteLine($"Verify: {before.Missing.Count} missing, {before.Modified.Count} modified, {before.MutableDrift.Count} drift, {before.UnknownDlls.Count} unknown DLLs.");
        var disabledTargets = lockDoc.Overrides.Where(o => o.Op == "disable").Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A disabled file is expected to be missing from its original path.
        var toFix = before.Missing.Concat(before.Modified).Where(p => !disabledTargets.Contains(p.Path)).ToList();
        if (toFix.Count == 0 && (!quarantineUnknownDlls || before.UnknownDlls.Count == 0))
        {
            return new RepairReport(null, 0, 0, 0, Array.Empty<string>(), sw.Elapsed);
        }

        var txnId = TransactionJournal.NewId();
        var journal = new TransactionJournal { TxnId = txnId, Operation = "repair", PreviousLockSha256 = state.LockSha256, TargetLockSha256 = state.LockSha256, TargetPackVersion = state.PackVersion };
        journal.Write(profile);
        ProfileSession? session = null;
        var snapshotTaken = false;
        var committed = false;
        var restored = 0; var overridesReapplied = 0; var quarantined = 0;
        try
        {
            var user = new VmpCkanUser(progress: _progress);
            session = new ProfileSession(_paths, profile, lockDoc, cacheDir, user);
            var fetcher = new ArtifactFetcher(cacheDir);
            var invByPath = inventory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var overrideByProducedPath = new Dictionary<string, LockOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in lockDoc.Overrides)
            {
                overrideByProducedPath.TryAdd(ProducedPath(o), o);
            }

            // Plan: override-produced files are rewritten from the override (add/replace) or re-extracted from the
            // owner's archive under the renamed path (disable); every other file comes from its owner's archive.
            var fromArchives = new List<ArchiveFix>();
            var rewrites = new List<LockOverride>();
            foreach (var problem in toFix)
            {
                var inv = invByPath[problem.Path];
                if (inv.Class != FileClass.Override)
                {
                    fromArchives.Add(new ArchiveFix(inv.Owner, problem.Path, problem.Path, inv.Sha256, inv.Size, false));
                    continue;
                }
                if (!overrideByProducedPath.TryGetValue(problem.Path, out var o))
                {
                    throw new LauncherException(ErrorCode.VerifyFailed, $"{problem.Path} is an override file but no override in the pack produces it.", RecoveryHint.AskHost);
                }
                if (o.Op == "disable")
                {
                    fromArchives.Add(new ArchiveFix(o.Owner!, o.Path, problem.Path, inv.Sha256, inv.Size, true));
                }
                else
                {
                    rewrites.Add(o);
                }
            }

            // Every archive and override bundle needed is in the cache and verified before anything changes.
            var archives = new Dictionary<string, string>(StringComparer.Ordinal);
            var missingPrivate = new List<string>();
            foreach (var owner in fromArchives.Select(f => f.Owner).Distinct(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var module = session.Modules.FirstOrDefault(m => m.identifier == owner)
                             ?? throw new LauncherException(ErrorCode.VerifyFailed, $"Files owned by {owner} need repair but the pack has no such module.", RecoveryHint.AskHost);
                if (EnsureArchive(session, module, lockDoc.Ckan.Modules.First(m => m.Identifier == owner), sideloadFiles, missingPrivate, ct) is { } zip)
                {
                    archives[owner] = zip;
                }
            }
            if (missingPrivate.Count > 0)
            {
                throw new LauncherException(ErrorCode.PrivateArtifactNeeded, "Repairing this profile needs private packages the host must give you.", RecoveryHint.ProvideFile, details: missingPrivate);
            }
            foreach (var o in lockDoc.Overrides.Where(o => o.Content?.Artifact is not null))
            {
                fetcher.Fetch(o.Content!.Artifact!, ct);
            }
            journal = journal.Advance(TxnState.Prefetched);
            journal.Write(profile);

            // ---- from here on the profile changes ----
            journal = journal.Advance(TxnState.Snapshotting) with { SnapshotDir = Path.Combine(profile.SnapshotsDir, txnId) };
            journal.Write(profile);
            SnapshotEngine.Create(profile, lockDoc.FileRules, txnId, RootFilesOf(inventory), ct);
            snapshotTaken = true;
            journal = journal.Advance(TxnState.Snapshotted);
            journal.Write(profile);
            journal = journal.Advance(TxnState.Applying);
            journal.Write(profile);

            foreach (var group in fromArchives.GroupBy(f => f.Owner, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var module = session.Modules.First(m => m.identifier == group.Key);
                var zip = archives[group.Key];
                var entriesByDest = ModuleInstaller.FindInstallableFiles(module, zip, session.Game)
                    .Where(f => !f.source.IsDirectory)
                    .ToDictionary(f => CKANPathUtils.NormalizePath(f.destination), f => f.source.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var fix in group)
                {
                    if (!entriesByDest.TryGetValue(fix.ArchivePath, out var entryName))
                    {
                        throw new LauncherException(ErrorCode.VerifyFailed, $"{fix.ArchivePath} is owned by {group.Key} but its archive does not contain it.", RecoveryHint.AskHost);
                    }
                    var dest = SafePath.Resolve(profile.GameDir, fix.Destination);
                    SafePath.ThrowIfReparsePointAbove(profile.GameDir, dest, fix.Destination);
                    SafeZip.ExtractEntryVerified(zip, entryName, dest, fix.Sha256, fix.Size);
                    if (fix.IsOverride)
                    {
                        overridesReapplied++;
                    }
                    else
                    {
                        restored++;
                    }
                    _log.WriteLine($"  restored {fix.Destination} ({group.Key})");
                }
            }
            foreach (var o in rewrites)
            {
                OverrideApplier.Rewrite(profile, o, a => fetcher.Fetch(a, ct));
                overridesReapplied++;
                _log.WriteLine($"  rewrote {o.Path} (override {o.Id})");
            }
            var notes = OverrideApplier.Apply(profile, lockDoc.Overrides, a => fetcher.Fetch(a, ct));
            overridesReapplied += notes.Count(n => !n.EndsWith("already applied", StringComparison.Ordinal) && !n.EndsWith("already disabled", StringComparison.Ordinal));
            if (quarantineUnknownDlls)
            {
                var qdir = Path.Combine(profile.QuarantineDir, txnId);
                foreach (var rel in before.UnknownDlls)
                {
                    var abs = Path.Combine(profile.GameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(abs) || SafePath.HasReparsePointAbove(profile.GameDir, abs))
                    {
                        continue; // gone already (e.g. renamed by a disable override), or not really inside the profile
                    }
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
            committed = true;
        }
        catch (Exception ex) when (!committed)
        {
            DisposeQuietly(session);
            if (!snapshotTaken)
            {
                if (journal.SnapshotDir is not null)
                {
                    TryCleanup("delete the partial snapshot", () => SnapshotEngine.Delete(profile, txnId));
                }
                journal = journal.Advance(TxnState.RolledBack, Describe(ex));
                journal.Write(profile);
                throw;
            }
            _log.WriteLine($"Repair failed ({Describe(ex)}); restoring...");
            RollBack(profile, ref journal, ex, keepSnapshot: false);
            throw;
        }
        CleanUpAfterCommit(session, profile, txnId, keepSnapshot: false);
        return new RepairReport(txnId, restored, overridesReapplied, quarantined, Array.Empty<string>(), sw.Elapsed);
    }

    /// <summary>One file Repair takes from a module archive: the archive entry installed at ArchivePath, written to Destination.</summary>
    private sealed record ArchiveFix(string Owner, string ArchivePath, string Destination, string Sha256, long Size, bool IsOverride);

    /// <summary>The game path an override's result lives at (a disable's file moves to its new name).</summary>
    private static string ProducedPath(LockOverride o)
        => o.Op == "disable" && o.RenameTo is not null
            ? (o.Path.Contains('/') ? o.Path[..(o.Path.LastIndexOf('/') + 1)] : string.Empty) + o.RenameTo
            : o.Path;

    /// <summary>
    /// The verified cached archive for <paramref name="module"/>: from the cache, from a file the user provided, or
    /// downloaded. Private archives nobody provided are added to <paramref name="missingPrivate"/> (null returned).
    /// </summary>
    private string? EnsureArchive(ProfileSession session, CkanModule module, LockedModule lm, IReadOnlyDictionary<string, string>? sideloadFiles, List<string> missingPrivate, CancellationToken ct)
    {
        var artifact = lm.Artifact ?? throw new LauncherException(ErrorCode.InvalidLock, $"{lm.Identifier} has no artifact in the lock.");
        var cached = session.Cache.GetCachedFilename(module);
        if (cached is not null)
        {
            if (Sha256Util.HexEquals(session.VerifiedIndex.Sha256Of(cached, ct), artifact.Sha256))
            {
                return cached;
            }
            _log.WriteLine($"  cached archive for {module.identifier} is corrupt; removing it");
            session.Cache.Purge(module);
            session.VerifiedIndex.Forget(cached);
            session.VerifiedIndex.Save();
        }
        if (sideloadFiles is not null && sideloadFiles.TryGetValue(module.identifier, out var file) && File.Exists(file))
        {
            session.Sideload(module, artifact, file, ct);
            _log.WriteLine($"  sideloaded {module.identifier} from {Path.GetFileName(file)}");
        }
        else if (artifact.Distribution == "private-sideload" || artifact.Urls.Count == 0)
        {
            missingPrivate.Add($"{module.identifier} {module.version}: {artifact.FileName ?? "(archive)"}");
            return null;
        }
        else
        {
            _log.WriteLine($"  fetching archive for {module.identifier}...");
            session.Download(new[] { module }, _progress, ct);
        }
        return session.Cache.GetCachedFilename(module) ?? throw new LauncherException(ErrorCode.ArtifactUnavailable, $"No archive for {module.identifier}.", RecoveryHint.Retry);
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

    private static void ThrowIfIncomplete(ProfilePaths profile)
    {
        if (TransactionJournal.FindIncomplete(profile) is { } incomplete)
        {
            throw new LauncherException(ErrorCode.InstallFailed, $"A previous {incomplete.Operation} did not finish (state {incomplete.State}). Restore the profile first.", RecoveryHint.Restore,
                details: new[] { $"transaction {incomplete.TxnId} started {incomplete.StartedUtc:u}" });
        }
    }

    /// <summary>Releases CKAN and removes the snapshot after a commit. Failures here are warnings, never a rollback.</summary>
    private List<string> CleanUpAfterCommit(ProfileSession? session, ProfilePaths profile, string txnId, bool keepSnapshot)
    {
        var warnings = new List<string>();
        void Step(string what, Action action)
        {
            if (TryCleanup(what, action) is { } warning)
            {
                warnings.Add(warning);
            }
        }
        Step("release the CKAN registry", () => session?.Dispose());
        Step("clean CKAN temp folders", () => CleanCkanTempDirs(profile));
        if (!keepSnapshot)
        {
            Step("delete the snapshot", () => SnapshotEngine.Delete(profile, txnId));
        }
        return warnings;
    }

    /// <summary>Runs a best-effort step; returns a warning (also logged) when it fails.</summary>
    private string? TryCleanup(string what, Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            var warning = $"Could not {what}: {Describe(ex)}";
            _log.WriteLine($"Warning: {warning}");
            return warning;
        }
    }

    private void DisposeQuietly(ProfileSession? session) => TryCleanup("release the CKAN registry", () => session?.Dispose());

    /// <summary>Copies via a temp file and rename, so a crash never leaves a half-written state copy behind.</summary>
    private static void CopyAtomically(string source, string destination)
    {
        var tmp = destination + ".tmp";
        File.Copy(source, tmp, overwrite: true);
        File.Move(tmp, destination, overwrite: true);
    }

    private static IEnumerable<string> RootFilesOf(InventoryDocument inventory) => inventory.Files.Select(f => f.Path).Where(p => !p.Contains('/'));

    /// <summary>Root files of the installed pack's inventory (empty when there is none or it cannot be read).</summary>
    private string[] PreviousRootFiles(ProfilePaths profile)
    {
        try
        {
            if (!File.Exists(profile.LockCopyFile) || !File.Exists(profile.InventoryCopyFile))
            {
                return Array.Empty<string>();
            }
            var previous = LauncherJson.ReadFile<PackLock>(profile.LockCopyFile);
            return RootFilesOf(InventoryFile.Read(profile.InventoryCopyFile, previous.Inventory.Sha256)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _log.WriteLine($"Warning: could not read the installed inventory ({ex.Message}); root files it owned are not snapshotted.");
            return Array.Empty<string>();
        }
    }

    /// <summary>File rules of the installed lock copy, for snapshots older than the rules recorded in their manifest.</summary>
    private static Core.Packs.FileRules? LockCopyRules(ProfilePaths profile)
    {
        try
        {
            return File.Exists(profile.LockCopyFile) ? LauncherJson.ReadFile<PackLock>(profile.LockCopyFile).FileRules : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Describe(Exception ex) => ex is LauncherException le ? $"{le.Code}: {le.Message}" : $"{ex.GetType().Name}: {ex.Message}";
}
