using System.CommandLine;
using System.Diagnostics;
using VmpLauncher.Ckan;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Ckan.Catalog;
using VmpLauncher.Core.Catalog;

namespace VmpLauncher.Cli.Commands;

internal static class CatalogCommands
{
    public static Command Build(Option<string?> dataDirOption)
    {
        var catalog = new Command("catalog", "Browse the full CKAN mod catalog");

        var force = new Option<bool>("--force") { Description = "Download even if the catalog did not change upstream." };
        var update = new Command("update", "Download the CKAN catalog into the launcher's own cache") { force };
        update.SetAction(parse => Run(dataDirOption, parse, (svc, _) =>
        {
            var sw = Stopwatch.StartNew();
            var outcome = svc.Update(parse.GetValue(force), new Progress<Ckan.Users.CkanProgress>(p => Console.Error.Write($"\r{p.Percent,3}% {p.Message,-70}")), CancellationToken.None);
            Console.Error.WriteLine();
            Console.WriteLine($"{(outcome.Changed ? "updated" : "unchanged")}: {outcome.Mods} mods, {outcome.NewMods} new, {outcome.Elapsed.TotalSeconds:F1} s; working set {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB");
            return 0;
        }));
        catalog.Subcommands.Add(update);

        var ksp = new Option<string>("--ksp") { Description = "KSP folder to check compatibility and installed mods against.", Required = true };
        var search = new Option<string?>("--search") { Description = "Words to search for." };
        var filter = new Option<CatalogFilter>("--filter") { Description = "compatible, all, installed, updates, notinstalled, new, incompatible, cached", DefaultValueFactory = _ => CatalogFilter.Compatible };
        var sort = new Option<CatalogSort>("--sort") { Description = "popular, recentlyupdated, name, size", DefaultValueFactory = _ => CatalogSort.Popular };
        var top = new Option<int>("--top") { Description = "How many rows to print.", DefaultValueFactory = _ => 20 };
        var list = new Command("list", "List catalog entries for a KSP folder") { ksp, search, filter, sort, top };
        list.SetAction(parse => Run(dataDirOption, parse, (svc, _) =>
        {
            var sw = Stopwatch.StartNew();
            svc.EnsureLoaded();
            var loaded = sw.Elapsed;
            var snap = svc.Build(new CatalogTarget(Path.GetFullPath(parse.GetValue(ksp)!), "cli"));
            var built = sw.Elapsed;
            var index = new CatalogIndex(snap.Entries);
            var rows = index.Query(parse.GetValue(search), parse.GetValue(filter), null, parse.GetValue(sort));
            Console.WriteLine($"KSP {snap.GameVersion}: {snap.Entries.Count} mods ({snap.Entries.Count(e => e.IsCompatible)} compatible, {snap.Entries.Count(e => e.IsInstalled)} installed, {snap.Entries.Count(e => e.HasUpdate)} with updates); {rows.Count} match.");
            Console.WriteLine($"load {loaded.TotalSeconds:F1} s, build {(built - loaded).TotalSeconds:F1} s, query {(sw.Elapsed - built).TotalMilliseconds:F0} ms; working set {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB{(snap.ReadOnly ? "; read-only: " + snap.ReadOnlyReason : string.Empty)}");
            foreach (var e in rows.Take(parse.GetValue(top)))
            {
                var state = e.HasUpdate ? $"update {e.InstalledVersion} -> {e.LatestVersion}" : e.IsInstalled ? $"installed {e.InstalledVersion}" : e.IsCompatible ? e.LatestVersion : $"incompatible ({e.GameVersions})";
                Console.WriteLine($"  {e.Name,-40} {e.Downloads,8:N0}  {state}");
            }
            return 0;
        }));
        catalog.Subcommands.Add(list);

        var ident = new Option<string>("--id") { Description = "Mod identifier.", Required = true };
        var show = new Command("show", "Show one mod's details") { ksp, ident };
        show.SetAction(parse => Run(dataDirOption, parse, (svc, _) =>
        {
            var d = svc.Details(new CatalogTarget(Path.GetFullPath(parse.GetValue(ksp)!), "cli"), parse.GetValue(ident)!);
            var e = d.Entry;
            Console.WriteLine($"{e.Name} ({e.Identifier}) by {e.Authors}");
            Console.WriteLine($"  {e.Abstract}");
            Console.WriteLine($"  latest {e.LatestVersion}, installed {e.InstalledVersion ?? "-"}, {e.GameVersions}, {e.Downloads:N0} downloads, license {e.License}");
            Console.WriteLine($"  spacedock {e.SpaceDock}  homepage {e.Homepage}  repo {e.Repository}");
            foreach (var r in d.Relations)
            {
                Console.WriteLine($"  {r.Kind,-10} {r.Text}{(r.Satisfied ? " (installed)" : string.Empty)}");
            }
            foreach (var v in d.Versions.Take(8))
            {
                Console.WriteLine($"  v {v.Version,-20} {v.GameVersions,-18} {v.ReleaseDate:yyyy-MM-dd} {(v.IsCompatible ? "compatible" : string.Empty)}{(v.IsInstalled ? " installed" : string.Empty)}");
            }
            return 0;
        }));
        catalog.Subcommands.Add(show);

        var installOpt = new Option<string?>("--install") { Description = "Comma-separated identifiers to install." };
        var removeOpt = new Option<string?>("--remove") { Description = "Comma-separated identifiers to remove." };
        var upgradeOpt = new Option<string?>("--upgrade") { Description = "Comma-separated identifiers to update." };
        var chooseOpt = new Option<string[]>("--choose") { Description = "virtual=identifier picks for 'several mods provide X'.", AllowMultipleArgumentsPerToken = true };
        ChangeRequest Request(ParseResult p)
        {
            static string[] Split(string? v) => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new ChangeRequest
            {
                Install = Split(p.GetValue(installOpt)),
                Remove = Split(p.GetValue(removeOpt)),
                Upgrade = Split(p.GetValue(upgradeOpt)),
                ProvidesChoices = (p.GetValue(chooseOpt) ?? Array.Empty<string>()).Select(c => c.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]),
            };
        }
        void Print(ChangePlan plan)
        {
            foreach (var c in plan.Changes)
            {
                Console.WriteLine($"  {c.Action,-8} {c.Name,-36} {CatalogFormat.DisplayVersion(c.Version),-16} {c.Reason}{(c.Cached ? string.Empty : $" ({c.DownloadSize / 1024} KB)")}");
            }
            foreach (var x in plan.Conflicts) { Console.WriteLine($"  CONFLICT {x}"); }
            foreach (var x in plan.Errors) { Console.WriteLine($"  ERROR {x}"); }
            foreach (var x in plan.Suggestions) { Console.WriteLine($"  {x.Kind,-11} {x.Name} (by {string.Join(", ", x.By)}){(x.PreChecked ? " [ticked]" : string.Empty)}"); }
            if (plan.Choice is { } ch)
            {
                Console.WriteLine($"  CHOOSE {ch.Requested} (for {ch.RequestedBy}): {string.Join(" | ", ch.Options.Select(o => o.Identifier))}");
            }
            Console.WriteLine($"  download {plan.DownloadCount} archive(s), {plan.DownloadBytes / (1024 * 1024)} MB; can apply: {plan.CanApply}");
        }
        var plan = new Command("plan", "Preview installing/removing/updating mods in a KSP folder") { ksp, installOpt, removeOpt, upgradeOpt, chooseOpt };
        plan.SetAction(parse => Run(dataDirOption, parse, (svc, _) =>
        {
            Print(svc.Preview(new CatalogTarget(Path.GetFullPath(parse.GetValue(ksp)!), "cli"), Request(parse)));
            return 0;
        }));
        catalog.Subcommands.Add(plan);
        var apply = new Command("apply", "Install/remove/update mods in a KSP folder (one CKAN transaction)") { ksp, installOpt, removeOpt, upgradeOpt, chooseOpt };
        apply.SetAction(parse => Run(dataDirOption, parse, (svc, _) =>
        {
            var target = new CatalogTarget(Path.GetFullPath(parse.GetValue(ksp)!), "cli");
            var preview = svc.Preview(target, Request(parse));
            Print(preview);
            if (!preview.CanApply)
            {
                return 2;
            }
            var report = svc.Apply(target, Request(parse), new Progress<Ckan.Users.CkanProgress>(p => Console.Error.Write($"\r{p.Percent,3}% {p.Message,-70}")), CancellationToken.None);
            Console.Error.WriteLine();
            Console.WriteLine($"done in {report.Elapsed.TotalSeconds:F1} s: {report.Installed} installed, {report.Upgraded} updated, {report.Removed} removed");
            return 0;
        }));
        catalog.Subcommands.Add(apply);

        var from = new Option<string>("--from") { Description = "KSP install to copy the stock game from.", Required = true };
        var to = new Option<string>("--to") { Description = "New game folder (must not exist).", Required = true };
        var seed = new Command("seed", "Make a stock KSP copy (hard links when on the same drive) to try mods in") { from, to };
        seed.SetAction(parse =>
        {
            var result = new VmpLauncher.Core.Profiles.ProfileSeeder(Console.Out).Seed(new VmpLauncher.Core.Profiles.ProfileSeeder.Plan(
                Path.GetFullPath(parse.GetValue(from)!), Path.GetFullPath(parse.GetValue(to)!), HardLink: true, Array.Empty<string>()));
            Console.WriteLine($"{result.FilesLinked} linked, {result.FilesCopied} copied in {result.Elapsed.TotalSeconds:F1} s");
            return 0;
        });
        catalog.Subcommands.Add(seed);
        return catalog;
    }

    private static int Run(Option<string?> dataDirOption, ParseResult parse, Func<CatalogService, CkanWorker, int> action)
    {
        var paths = Program.ResolvePaths(parse.GetValue(dataDirOption));
        CkanBootstrap.Initialize(paths);
        using var worker = new CkanWorker();
        return worker.Run(() =>
        {
            using var svc = new CatalogService(paths, paths.CacheDir);
            return action(svc, worker);
        });
    }
}
