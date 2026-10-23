using CKAN;
using CKAN.Configuration;
using CKAN.IO;
using VmpLauncher.Ckan.Users;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Profiles;

namespace VmpLauncher.Ckan.Catalog;

/// <summary>
/// Installing, removing and updating any mod in a custom or existing setup. Works like CKAN's own client: the resolver
/// previews the full change (dependencies, mods that must go with a removed one, conflicts), then uninstall, install
/// and upgrade run in one CKAN transaction, so a failure or cancel leaves the setup as it was.
/// </summary>
public sealed partial class CatalogService
{
    private sealed record Composed(List<CkanModule> Install, List<CkanModule> Upgrade, List<string> Remove, HashSet<CkanModule> Auto, List<string> Errors, List<string> Notes);

    private static void ThrowIfPack(CatalogTarget target)
    {
        if (target.PackPins is not null)
        {
            throw new LauncherException(ErrorCode.InstallFailed, "This setup follows a pack, so its mods only change with the pack. Make a custom copy to pick your own mods.");
        }
    }

    private static CkanModule? Latest(Context ctx, string identifier)
    {
        try
        {
            return ctx.Registry.LatestAvailable(identifier, ctx.Stability, ctx.Criteria);
        }
        catch (ModuleNotFoundKraken)
        {
            return null;
        }
    }

    private static Composed Compose(Context ctx, ChangeRequest req)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var install = new List<CkanModule>();
        var auto = new HashSet<CkanModule>();
        var dependencies = req.Dependencies.ToHashSet(StringComparer.Ordinal);
        foreach (var ident in req.Install.Concat(req.AcceptedSuggestions).Concat(req.Dependencies).Distinct(StringComparer.Ordinal))
        {
            if (ctx.Registry.InstalledModule(ident) is not null)
            {
                continue;
            }
            CkanModule? pick = null;
            if (req.Versions.TryGetValue(ident, out var pinned))
            {
                pick = ctx.Registry.GetModuleByVersion(ident, new CKAN.Versioning.ModuleVersion(pinned));
                if (pick is null && Latest(ctx, ident) is { } newer)
                {
                    pick = newer;
                    notes.Add($"{newer.name} {CatalogFormat.DisplayVersion(pinned)} is no longer in CKAN; installing {CatalogFormat.DisplayVersion(newer.version.ToString())} instead.");
                }
            }
            else
            {
                pick = Latest(ctx, ident);
            }
            if (pick is null)
            {
                errors.Add(req.Versions.TryGetValue(ident, out var v) ? $"{ident} {CatalogFormat.DisplayVersion(v)} is not in the CKAN catalog for KSP {ctx.Criteria}." : $"{ident} has no version for KSP {ctx.Criteria}.");
                continue;
            }
            install.Add(pick);
            if (dependencies.Contains(ident))
            {
                auto.Add(pick);
            }
        }
        foreach (var chosen in req.ProvidesChoices.Values.Distinct(StringComparer.Ordinal))
        {
            if (ctx.Registry.InstalledModule(chosen) is null && Latest(ctx, chosen) is { } m && !install.Any(x => x.identifier == m.identifier))
            {
                install.Add(m);
                auto.Add(m);
            }
        }
        var upgrade = new List<CkanModule>();
        foreach (var ident in req.Upgrade.Distinct(StringComparer.Ordinal))
        {
            var installed = ctx.Registry.InstalledModule(ident);
            if (installed is not null && Latest(ctx, ident) is { } latest && latest.version.CompareTo(installed.Module.version) > 0)
            {
                upgrade.Add(latest);
            }
        }
        var remove = req.Remove.Where(i => ctx.Registry.InstalledModule(i) is not null).Distinct(StringComparer.Ordinal).ToList();
        return new Composed(install, upgrade, remove, auto, errors, notes);
    }

    private static RelationshipResolverOptions PreviewOptions(Context ctx)
    {
        var opts = RelationshipResolverOptions.DependsOnlyOpts(ctx.Stability);
        opts.proceed_with_inconsistencies = true;
        opts.without_enforce_consistency = true;
        opts.without_toomanyprovides_kraken = false;
        return opts;
    }

    /// <summary>The full effect of <paramref name="req"/>; changes nothing.</summary>
    public ChangePlan Preview(CatalogTarget target, ChangeRequest req)
    {
        ThrowIfPack(target);
        var ctx = Open(target);
        var registry = ctx.Registry;
        var c = Compose(ctx, req);
        var installedMods = registry.InstalledModules.Select(im => im.Module).ToList();
        var removeMods = c.Remove.Select(i => registry.InstalledModule(i)!.Module).ToList();
        var wanted = c.Install.Concat(c.Upgrade).ToList();

        RelationshipResolver resolver;
        try
        {
            resolver = new RelationshipResolver(wanted, removeMods, PreviewOptions(ctx), registry, _game, ctx.Criteria);
        }
        catch (TooManyModsProvideKraken k)
        {
            var options = k.modules
                .Select(m => new ProvidesOption(m.identifier, m.name, OneLine(m.@abstract), _cache.IsMaybeCachedZip(m), _repoData.GetDownloadCount(ctx.Repos, m.identifier) ?? 0))
                .OrderByDescending(o => o.Cached).ThenByDescending(o => o.Downloads).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Empty(c.Errors) with { Choice = new ProvidesChoice(k.requested, k.requester.name, options), Notes = c.Notes };
        }
        catch (DependenciesNotSatisfiedKraken k)
        {
            return Empty(c.Errors.Append(k.Message).ToList());
        }
        catch (Kraken k)
        {
            return Empty(c.Errors.Append(k.Message).ToList());
        }

        var changes = new List<PlannedChange>();
        var byPlayer = req.Install.Concat(req.Upgrade).Concat(req.AcceptedSuggestions).ToHashSet(StringComparer.Ordinal);
        long downloadBytes = 0;
        var downloads = 0;
        var countedUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in resolver.ModList(parallel: false))
        {
            var installed = registry.InstalledModule(m.identifier);
            if (installed is not null && installed.Module.version.Equals(m.version))
            {
                continue;
            }
            var cached = m.download is not { Count: > 0 } || _cache.IsMaybeCachedZip(m);
            // Modules that share one archive (Scatterer and its configs) download it once.
            if (!cached && countedUrls.Add(m.download![0].ToString()))
            {
                downloadBytes += m.download_size;
                downloads++;
            }
            var reason = resolver.ReasonsFor(m).FirstOrDefault() switch
            {
                SelectionReason.UserRequested when req.Versions.ContainsKey(m.identifier) => "In the shared setup",
                SelectionReason.UserRequested => byPlayer.Contains(m.identifier) ? "You picked it" : "Your choice for a shared dependency",
                SelectionReason.RelationshipReason rr => $"Needed by {rr.Parent.name}",
                { } other => other.ToString() ?? string.Empty,
                null => string.Empty,
            };
            changes.Add(new PlannedChange(m.identifier, m.name, m.version.ToString(), installed is null ? ChangeAction.Install : ChangeAction.Upgrade, reason, byPlayer.Contains(m.identifier), m.download_size, cached));
        }

        if (c.Remove.Count > 0)
        {
            var going = Registry.FindReverseDependencies(c.Remove, wanted, installedMods, registry.InstalledDlls, registry.InstalledDlc).ToHashSet(StringComparer.Ordinal);
            going.UnionWith(c.Remove);
            foreach (var ident in going)
            {
                var im = registry.InstalledModule(ident);
                if (im is null || changes.Any(x => x.Identifier == ident))
                {
                    continue;
                }
                var mine = c.Remove.Contains(ident, StringComparer.Ordinal);
                changes.Add(new PlannedChange(ident, im.Module.name, im.Module.version.ToString(), ChangeAction.Remove, mine ? "You removed it" : "Needs a mod you are removing", mine, 0, true));
            }
            var goingNow = changes.Where(x => x.Action == ChangeAction.Remove).Select(x => x.Identifier).ToHashSet(StringComparer.Ordinal);
            foreach (var im in registry.FindRemovableAutoInstalled(wanted, goingNow, ctx.Instance!))
            {
                if (!goingNow.Contains(im.identifier))
                {
                    changes.Add(new PlannedChange(im.identifier, im.Module.name, im.Module.version.ToString(), ChangeAction.Remove, "No longer needed by any mod", false, 0, true));
                }
            }
        }

        var suggestions = new List<ModSuggestion>();
        if (wanted.Count > 0)
        {
            var seen = req.SeenSuggestions.Concat(req.AcceptedSuggestions).Select(i => Latest(ctx, i)).OfType<CkanModule>().ToList();
            var source = c.Install.Concat(c.Upgrade).Where(m => !c.Auto.Contains(m)).ToList();
            try
            {
                ModuleInstaller.FindRecommendations(ctx.Instance!, source, resolver.ModList(parallel: false).ToList(), removeMods, seen, registry,
                    out var recommendations, out var suggested, out var supporters);
                suggestions.AddRange(recommendations.Select(kv => Suggestion(ctx, kv.Key, "recommended", kv.Value.Item2, kv.Value.Item1)));
                suggestions.AddRange(suggested.Select(kv => Suggestion(ctx, kv.Key, "suggested", kv.Value, false)));
                suggestions.AddRange(supporters.Select(kv => Suggestion(ctx, kv.Key, "supported", kv.Value.ToList(), false)));
            }
            catch (Kraken)
            {
                // Suggestions are optional; a resolver problem among them must not block the plan.
            }
        }
        suggestions = suggestions.Where(s => changes.All(x => x.Identifier != s.Identifier) && registry.InstalledModule(s.Identifier) is null)
            .DistinctBy(s => s.Identifier)
            .ToList();

        return new ChangePlan(
            changes.OrderBy(x => x.Action).ThenByDescending(x => x.ByPlayer).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            resolver.ConflictDescriptions.ToList(),
            c.Errors,
            suggestions,
            null,
            downloadBytes,
            downloads) { Notes = c.Notes };
    }

    private static ChangePlan Empty(IReadOnlyList<string> errors)
        => new(Array.Empty<PlannedChange>(), Array.Empty<string>(), errors, Array.Empty<ModSuggestion>(), null, 0, 0);

    private static ModSuggestion Suggestion(Context ctx, CkanModule m, string kind, IReadOnlyCollection<string> by, bool preChecked)
        => new(m.identifier, m.name, OneLine(m.@abstract), kind, by.ToList(), preChecked);

    /// <summary>Applies <paramref name="req"/> in one CKAN transaction. Pack setups are refused.</summary>
    public ApplyReport Apply(CatalogTarget target, ChangeRequest req, IProgress<CkanProgress>? progress, CancellationToken ct)
    {
        ThrowIfPack(target);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EnsureLoaded();
        var instance = TryInstance(target)
                       ?? throw new LauncherException(ErrorCode.GameNotFound, $"{target.GameDir} is not a KSP folder the launcher can read.", RecoveryHint.ChooseAnotherFolder);
        if (OperatingSystem.IsWindows())
        {
            GameProcessGuard.ThrowIfRunning(target.GameDir);
        }
        using var profileLock = target.ProfileRoot is { } root ? ProfileLock.Acquire(new ProfilePaths(root), "mods") : null;
        RegistryManager manager;
        try
        {
            manager = RegistryManager.Instance(instance, _repoData, new[] { DefaultRepo });
        }
        catch (RegistryInUseKraken ex)
        {
            throw new LauncherException(ErrorCode.CkanBusy, "CKAN has this KSP folder open.", RecoveryHint.None, ex);
        }
        var notes = new List<string>();
        try
        {
            if (manager.registry.Repositories.Count == 0)
            {
                manager.registry.RepositoriesSet(new SortedDictionary<string, Repository> { [DefaultRepo.name] = DefaultRepo });
            }
            manager.ScanUnmanagedFiles();
            var ctx = new Context(manager.registry, instance, instance.VersionCriteria(), instance.StabilityToleranceConfig, false, null);
            var c = Compose(ctx, req);
            if (c.Errors.Count > 0)
            {
                throw new LauncherException(ErrorCode.ModConflict, "Some of these mods cannot be installed.", RecoveryHint.None, details: c.Errors);
            }
            var user = new VmpCkanUser(progress: progress);
            var installer = new ModuleInstaller(instance, _cache, new JsonConfiguration(_paths.CkanConfigFile), user, ct);
            installer.InstallProgress += (m, done, total) => progress?.Report(new CkanProgress($"Installing {m.name}", total == 0 ? 0 : (int)(done * 100 / total), null));
            installer.RemoveProgress += (m, done, total) => progress?.Report(new CkanProgress($"Removing {m.Module.name}", total == 0 ? 0 : (int)(done * 100 / total), null));
            installer.OneComplete += m => notes.Add($"{m.name} {m.version}");
            var downloader = new NetAsyncModulesDownloader(user, _cache, Backend.ProfileSession.UserAgent, ct);
            var message = "Downloading mods";
            downloader.OneComplete += m => message = $"Downloaded {m.name}";
            downloader.OverallDownloadProgress += rate => progress?.Report(new CkanProgress(message, rate.Percent, rate));
            HashSet<string>? configOnlyDirs = null;
            var wanted = c.Install.Concat(c.Upgrade).ToList();
            using (var tx = CkanTransaction.CreateTransactionScope())
            {
                if (c.Remove.Count > 0)
                {
                    installer.UninstallList(c.Remove, ref configOnlyDirs, manager, ConfirmPrompt: false, installing: wanted);
                }
                if (c.Install.Count > 0)
                {
                    installer.InstallList(c.Install, RelationshipResolverOptions.DependsOnlyOpts(ctx.Stability), manager, ref configOnlyDirs,
                        deduper: null, userAgent: Backend.ProfileSession.UserAgent, downloader: downloader, autoInstalled: c.Auto, ConfirmPrompt: false);
                }
                if (c.Upgrade.Count > 0)
                {
                    installer.Upgrade(c.Upgrade, downloader, ref configOnlyDirs, manager, deduper: null, autoInstalled: c.Auto, skipFiles: null, enforceConsistency: true, ConfirmPrompt: false);
                }
                ct.ThrowIfCancellationRequested();
                tx.Complete();
            }
            foreach (var dir in configOnlyDirs ?? new HashSet<string>())
            {
                notes.Add($"Left settings folder {dir}");
            }
            return new ApplyReport(c.Install.Count, c.Upgrade.Count, c.Remove.Count, sw.Elapsed, notes);
        }
        catch (CancelledActionKraken)
        {
            throw new OperationCanceledException(ct);
        }
        catch (TooManyModsProvideKraken k)
        {
            throw new LauncherException(ErrorCode.ModConflict, $"Several mods provide {k.requested}; review the changes again and pick one.", RecoveryHint.None, k);
        }
        catch (FileExistsKraken k)
        {
            throw new LauncherException(ErrorCode.InstallFailed, $"{k.filename} is already in the game folder but no installed mod owns it (installed by hand?). Remove it and try again.", RecoveryHint.None, k);
        }
        catch (ModuleDownloadErrorsKraken k)
        {
            throw new LauncherException(ErrorCode.ArtifactUnavailable, "Some mod downloads failed. Nothing was changed.", RecoveryHint.Retry, k, new[] { k.Message });
        }
        catch (DependenciesNotSatisfiedKraken k)
        {
            throw new LauncherException(ErrorCode.ModConflict, "Some dependencies cannot be met.", RecoveryHint.None, k, new[] { k.Message });
        }
        catch (InconsistentKraken k)
        {
            throw new LauncherException(ErrorCode.ModConflict, "These changes would leave mods that conflict.", RecoveryHint.None, k, new[] { k.Message });
        }
        catch (Kraken k)
        {
            throw new LauncherException(ErrorCode.InstallFailed, "CKAN could not complete the changes. Nothing was changed.", RecoveryHint.Retry, k, new[] { k.Message });
        }
        finally
        {
            RegistryManager.DisposeInstance(instance);
            _lastContext = null;
        }
    }
}
