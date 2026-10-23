using System.CommandLine;
using VmpLauncher.Ckan;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Inventory;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Packs;
using VmpLauncher.Core.Profiles;
using VmpLauncher.Core.Serialization;
using VmpLauncher.Core.Transactions;

namespace VmpLauncher.Cli.Commands;

internal static class ProfileCommands
{
    public static IEnumerable<Command> Build(Option<string?> dataDirOption)
    {
        yield return ProfileCommand(dataDirOption);
        yield return InstallCommand(dataDirOption, "install", "Install a lock into an empty profile");
        yield return InstallCommand(dataDirOption, "update", "Update an installed profile to a new lock (downloads only changed archives)");
        yield return VerifyCommand(dataDirOption);
        yield return RepairCommand(dataDirOption);
        yield return RecoverCommand(dataDirOption);
        yield return DiffCommand();
    }

    private static Command ProfileCommand(Option<string?> dataDirOption)
    {
        var profile = new Command("profile", "Create, list and inspect managed KSP profiles");

        var id = new Option<string>("--id") { Description = "Profile id (folder name).", Required = true };
        var from = new Option<string>("--from") { Description = "The player's own KSP install to seed stock files from.", Required = true };
        var root = new Option<string?>("--root") { Description = "Profile root directory (default: <data>\\profiles\\<id>)." };
        var copy = new Option<bool>("--copy") { Description = "Copy stock files instead of hard-linking (forced when on a different volume)." };
        var lockOpt = new Option<string?>("--lock") { Description = "Lock the profile will install; used to skip stock root files a mod owns and to check path length." };
        var name = new Option<string?>("--name") { Description = "Display name." };

        var create = new Command("create", "Seed a new profile from an existing KSP install") { id, from, root, copy, lockOpt, name };
        create.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            paths.EnsureCreated();
            var profileId = parse.GetValue(id)!;
            var store = new ProfileStore(paths);
            var customRoot = parse.GetValue(root);
            var pp = customRoot is { } r ? new ProfilePaths(r) : store.Get(profileId);
            if (pp.Exists)
            {
                Console.Error.WriteLine($"Profile already exists at {pp.Root}");
                return 2;
            }
            var source = Path.GetFullPath(parse.GetValue(from)!);
            PackLock? lockDoc = null;
            if (parse.GetValue(lockOpt) is { } lp)
            {
                lockDoc = LauncherJson.ReadFile<PackLock>(lp);
            }
            var game = lockDoc?.Game;
            var requiredBuild = game?.BuildId64 ?? new GameRequirements().BuildId64;
            var dlc = (game?.RequiredDlc ?? new GameRequirements().RequiredDlc).Select(d => (d.Folder, d.PolicyName));
            var buildId = ProfileSeeder.ValidateSource(source, requiredBuild, dlc);
            ProfilePathValidator.Validate(pp.Root, lockDoc?.Inventory.MaxRelPathLength ?? 180, source, paths.CacheDir);

            var hardLink = !parse.GetValue(copy) && ProfilePathValidator.SameVolume(source, pp.Root);
            var skipRoot = lockDoc is null ? Array.Empty<string>() : PathsOwnedByPack(lockDoc, parse.GetValue(lockOpt)!);
            Console.WriteLine($"Seeding {pp.GameDir} from {source} ({(hardLink ? "hard links" : "copies")})...");
            pp.EnsureCreated();
            if (customRoot is not null)
            {
                store.Register(profileId, pp.Root);
            }
            var result = new ProfileSeeder(Console.Out).Seed(new ProfileSeeder.Plan(source, pp.GameDir, hardLink, skipRoot));
            pp.WriteManifest(new ProfileManifest
            {
                Id = profileId,
                Name = parse.GetValue(name) ?? profileId,
                PackId = lockDoc?.Pack.Id,
                Seed = new SeedInfo
                {
                    FromPath = source, BuildId64 = buildId, Mode = hardLink ? "hardlink" : "copy",
                    BytesLinked = result.BytesLinked, BytesCopied = result.BytesCopied, FilesLinked = result.FilesLinked, FilesCopied = result.FilesCopied,
                },
            });
            Console.WriteLine($"Seeded: {result.FilesLinked} linked ({result.BytesLinked / 1048576.0:N0} MiB), {result.FilesCopied} copied ({result.BytesCopied / 1048576.0:N0} MiB) in {result.Elapsed}.");
            foreach (var w in result.Warnings)
            {
                Console.WriteLine($"warning: {w}");
            }
            Console.WriteLine($"Profile: {pp.Root}");
            return 0;
        });

        var list = new Command("list", "List profiles");
        list.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            foreach (var (pp, m) in new ProfileStore(paths).List())
            {
                var state = InstalledState.TryRead(pp);
                Console.WriteLine($"{m.Id,-24} {m.Name,-24} {(state is null ? "(empty)" : $"{state.PackId} {state.PackVersion} applied {state.AppliedUtc:u}")}  {pp.Root}");
            }
            return 0;
        });

        profile.Subcommands.Add(create);
        profile.Subcommands.Add(list);
        return profile;
    }

    private static string[] PathsOwnedByPack(PackLock lockDoc, string lockPath)
    {
        var invPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(lockPath))!, lockDoc.Inventory.File);
        if (!File.Exists(invPath))
        {
            return Array.Empty<string>();
        }
        var inv = InventoryFile.Read(invPath, lockDoc.Inventory.Sha256);
        return inv.Files.Select(f => f.Path).ToArray();
    }

    private static Command InstallCommand(Option<string?> dataDirOption, string verb, string description)
    {
        var id = new Option<string>("--profile") { Description = "Profile id.", Required = true };
        var lockOpt = new Option<string>("--lock") { Description = "Lock file to install (inventory.json.gz must sit next to it).", Required = true };
        var cache = new Option<string?>("--cache") { Description = "Download cache (default: launcher cache)." };
        var offline = new Option<bool>("--offline") { Description = "Fail instead of downloading anything." };
        var sideload = new Option<string[]>("--sideload") { Description = "identifier=path for private packages.", AllowMultipleArgumentsPerToken = true };

        var keepSnapshot = new Option<bool>("--keep-snapshot") { Description = "Keep the pre-transaction snapshot after commit (for inspection)." };
        var install = new Command(verb, description) { id, lockOpt, cache, offline, sideload, keepSnapshot };
        install.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            CkanBootstrap.Initialize(paths);
            var pp = new ProfileStore(paths).Get(parse.GetValue(id)!);
            if (!pp.Exists)
            {
                Console.Error.WriteLine("Profile not found; create it first with `vmpl profile create`.");
                return 2;
            }
            var lockPath = Path.GetFullPath(parse.GetValue(lockOpt)!);
            var lockDoc = LauncherJson.ReadFile<PackLock>(lockPath);
            var invPath = Path.Combine(Path.GetDirectoryName(lockPath)!, lockDoc.Inventory.File);
            var sideloads = (parse.GetValue(sideload) ?? Array.Empty<string>())
                .Select(s => s.Split('=', 2))
                .Where(a => a.Length == 2)
                .ToDictionary(a => a[0], a => Path.GetFullPath(a[1]), StringComparer.Ordinal);
            var cacheDir = parse.GetValue(cache) ?? pp.ReadManifest().CacheDir ?? paths.CacheDir;
            Directory.CreateDirectory(cacheDir);
            if (!parse.GetValue(offline))
            {
                // Same as the launcher: reuse matching archives from a normal CKAN client's cache before downloading.
                var sources = CkanCacheImporter.DiscoverCaches(cacheDir);
                var imported = CkanCacheImporter.Import(lockDoc, cacheDir, sources, null, CancellationToken.None);
                if (imported.Imported > 0 || imported.Rejected > 0)
                {
                    Console.WriteLine($"Reused {imported.Imported} archive(s) ({imported.Bytes:N0} bytes; {imported.Linked} linked, {imported.Copied} copied, {imported.Rejected} rejected) from {string.Join(", ", sources)}.");
                }
            }
            using var worker = new CkanWorker();
            try
            {
                var last = string.Empty;
                var progress = new Progress<Ckan.Users.CkanProgress>(p =>
                {
                    if (p.Message != last)
                    {
                        last = p.Message;
                        Console.WriteLine($"  {p.Percent,3}% {p.Message}");
                    }
                });
                var ops = new ProfileOperations(paths, Console.Out, progress);
                var request = new ProfileOperations.Request(pp, new ProfileOperations.LockSource(lockPath, invPath), cacheDir, parse.GetValue(offline), sideloads, parse.GetValue(keepSnapshot));
                var report = worker.Run(() => verb == "update" ? ops.Update(request) : ops.Install(request));
                Console.WriteLine();
                Console.WriteLine($"{report.Operation} {report.TxnId}: +{report.ModulesInstalled} modules, -{report.ModulesRemoved}; verified {report.FilesVerified} files ({report.Bytes:N0} bytes) in {report.Elapsed}. Downloaded {report.BytesDownloaded:N0} bytes.");
                foreach (var w in report.Warnings)
                {
                    Console.WriteLine($"warning: {w}");
                }
                return 0;
            }
            catch (LauncherException ex)
            {
                return ReportError(ex);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("cancelled; profile restored");
                return 130;
            }
        });
        return install;
    }

    private static Command RepairCommand(Option<string?> dataDirOption)
    {
        var id = new Option<string>("--profile") { Description = "Profile id.", Required = true };
        var cache = new Option<string?>("--cache") { Description = "Download cache (default: launcher cache)." };
        var quarantine = new Option<bool>("--quarantine-unknown-dlls") { Description = "Move DLLs the pack does not know about into the profile quarantine folder." };
        var repair = new Command("repair", "Restore damaged or missing pack files from the cached archives") { id, cache, quarantine };
        repair.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            CkanBootstrap.Initialize(paths);
            var pp = new ProfileStore(paths).Get(parse.GetValue(id)!);
            var cacheDir = parse.GetValue(cache) ?? pp.ReadManifest().CacheDir ?? paths.CacheDir;
            using var worker = new CkanWorker();
            try
            {
                var report = worker.Run(() => new ProfileOperations(paths, Console.Out).Repair(pp, cacheDir, parse.GetValue(quarantine)));
                Console.WriteLine(report.TxnId is null
                    ? $"Nothing to repair ({report.Elapsed})."
                    : $"repair {report.TxnId}: {report.FilesRestored} files restored, {report.OverridesReapplied} overrides re-applied, {report.Quarantined} quarantined in {report.Elapsed}.");
                return 0;
            }
            catch (LauncherException ex)
            {
                return ReportError(ex);
            }
        });
        return repair;
    }

    private static Command RecoverCommand(Option<string?> dataDirOption)
    {
        var id = new Option<string>("--profile") { Description = "Profile id.", Required = true };
        var recover = new Command("recover", "Restore a profile whose last operation was interrupted") { id };
        recover.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            CkanBootstrap.Initialize(paths);
            var pp = new ProfileStore(paths).Get(parse.GetValue(id)!);
            using var worker = new CkanWorker();
            try
            {
                var rb = worker.Run(() => new ProfileOperations(paths, Console.Out).Recover(pp));
                if (rb is null)
                {
                    Console.WriteLine("No interrupted transaction found.");
                    return 0;
                }
                Console.WriteLine($"Restored: {rb.Untouched} untouched, {rb.Restored} restored, {rb.Quarantined} quarantined, {rb.StateFilesRestored} state files in {rb.Elapsed}; {rb.Errors.Count} errors.");
                foreach (var e in rb.Errors.Take(20))
                {
                    Console.WriteLine($"  {e}");
                }
                return rb.Errors.Count == 0 ? 0 : 3;
            }
            catch (LauncherException ex)
            {
                return ReportError(ex);
            }
        });
        return recover;
    }

    private static Command DiffCommand()
    {
        var a = new Argument<string>("from") { Description = "Old lock file." };
        var b = new Argument<string>("to") { Description = "New lock file." };
        var diff = new Command("diff", "Show what an update from one lock to another would change") { a, b };
        diff.SetAction(parse =>
        {
            var from = LauncherJson.ReadFile<PackLock>(parse.GetValue(a)!);
            var to = LauncherJson.ReadFile<PackLock>(parse.GetValue(b)!);
            var d = LockDiff.Compute(from, to);
            Console.WriteLine(d.Summary());
            foreach (var m in d.Added)
            {
                Console.WriteLine($"  + {m.Identifier} {m.Version} ({m.Artifact?.Size:N0} bytes)");
            }
            foreach (var (o, n) in d.Changed)
            {
                Console.WriteLine($"  ~ {n.Identifier} {o.Version} -> {n.Version} ({n.Artifact?.Size:N0} bytes)");
            }
            foreach (var m in d.Removed)
            {
                Console.WriteLine($"  - {m.Identifier} {m.Version}");
            }
            Console.WriteLine($"Bytes to download (before cache): {d.ModulesNeedingArtifacts.Sum(m => m.Artifact!.Size):N0}");
            return 0;
        });
        return diff;
    }

    private static Command VerifyCommand(Option<string?> dataDirOption)
    {
        var id = new Option<string>("--profile") { Description = "Profile id.", Required = true };
        var full = new Option<bool>("--full") { Description = "Hash every file instead of the quick size/mtime/file-id check." };
        var verify = new Command("verify", "Verify a profile against its installed lock") { id, full };
        verify.SetAction(parse =>
        {
            var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
            var pp = new ProfileStore(paths).Get(parse.GetValue(id)!);
            var state = InstalledState.TryRead(pp);
            if (state is null)
            {
                Console.Error.WriteLine("Nothing installed in this profile.");
                return 2;
            }
            var lockDoc = LauncherJson.ReadFile<PackLock>(pp.LockCopyFile);
            var inventory = InventoryFile.Read(pp.InventoryCopyFile, lockDoc.Inventory.Sha256);
            var result = new InstallVerifier().Verify(pp, inventory, lockDoc.FileRules, parse.GetValue(full) ? InstallVerifier.Mode.Full : InstallVerifier.Mode.Quick, state);
            Console.WriteLine($"{result.FilesChecked} files checked, {result.FilesHashed} hashed in {result.Elapsed}.");
            Console.WriteLine($"missing {result.Missing.Count}, modified {result.Modified.Count}, mutable drift {result.MutableDrift.Count}, unknown {result.UnknownFiles.Count} (dlls {result.UnknownDlls.Count})");
            foreach (var p in result.Missing.Concat(result.Modified).Take(30))
            {
                Console.WriteLine($"  {p.Kind,-8} {p.Path} ({p.Owner}) {p.Detail}");
            }
            foreach (var u in result.UnknownFiles.Take(20))
            {
                Console.WriteLine($"  unknown  {u}");
            }
            if (result.IsClean)
            {
                new InstalledState { LockSha256 = state.LockSha256, PackId = state.PackId, PackVersion = state.PackVersion, AppliedUtc = state.AppliedUtc, Files = result.Records }.Write(pp);
            }
            return result.IsClean ? 0 : 3;
        });
        return verify;
    }

    internal static int ReportError(LauncherException ex)
    {
        Console.Error.WriteLine($"error [{ex.Code}]: {ex.Message}");
        foreach (var d in ex.Details.Take(40))
        {
            Console.Error.WriteLine($"  {d}");
        }
        if (ex.InnerException is not null)
        {
            Console.Error.WriteLine($"  cause: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        return 1;
    }
}
