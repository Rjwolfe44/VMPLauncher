using System.IO;
using CKAN;
using VmpLauncher.Ckan.Catalog;
using VmpLauncher.Ckan.Repo;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.IntegrationTests.Fixtures;
using Xunit;

namespace VmpLauncher.IntegrationTests;

/// <summary>The general mod manager against the fixture CKAN universe: catalog, preview, apply, rollback, CKAN lock.</summary>
public sealed class CatalogChangesTests : IDisposable
{
    private readonly TestWorld _w = new("catalog");
    private readonly CatalogService _svc;
    private readonly CatalogTarget _target;

    public CatalogChangesTests()
    {
        var latest = _w.Repo.All.GroupBy(m => m.Id).SelectMany(g => g).Select(m => m.Ckan).ToList();
        var zip = PackRepoBuilder.Build(latest, Path.Combine(_w.Root, "repo", "fixture.zip"));
        var repo = new Repository("fixture", new Uri(zip));
        _svc = _w.Worker.Run(() => new CatalogService(_w.Paths, _w.CacheDir, repo, Path.Combine(_w.Root, "repos")));
        _target = new CatalogTarget(_w.Profile.GameDir, "test", ProfileRoot: _w.Profile.Root);
        _w.Worker.Run(() => _svc.Update(force: true, null, CancellationToken.None));
    }

    public void Dispose()
    {
        _w.Worker.Run(() => _svc.Dispose());
        _w.Dispose();
    }

    private T Run<T>(Func<CatalogService, T> f) => _w.Worker.Run(() => f(_svc));

    private ApplyReport Apply(ChangeRequest r) => Run(s => s.Apply(_target, r, null, CancellationToken.None));

    private CatalogEntry Entry(string id) => Run(s => s.Build(_target)).Entries.Single(e => e.Identifier == id);

    [Fact]
    public void Catalog_lists_every_mod_with_its_newest_version()
    {
        var snap = Run(s => s.Build(_target));
        Assert.False(snap.ReadOnly);
        Assert.Contains(snap.Entries, e => e.Identifier == "F");
        Assert.Equal("1.1", snap.Entries.Single(e => e.Identifier == "A").LatestVersion);
        Assert.DoesNotContain(snap.Entries, e => e.IsInstalled);
    }

    [Fact]
    public void Preview_adds_dependencies_and_apply_installs_them()
    {
        var plan = Run(s => s.Preview(_target, new ChangeRequest { Install = new[] { "B" } }));
        Assert.True(plan.CanApply);
        Assert.Contains(plan.Changes, c => c.Identifier == "B" && c.ByPlayer && c.Action == ChangeAction.Install);
        Assert.Contains(plan.Changes, c => c.Identifier == "A" && !c.ByPlayer && c.Reason.Contains("B", StringComparison.Ordinal));

        var report = Apply(new ChangeRequest { Install = new[] { "B" } });
        Assert.Equal(1, report.Installed);
        Assert.True(File.Exists(_w.GamePath("GameData/B/B.dll")));
        Assert.True(File.Exists(_w.GamePath("GameData/A/A.dll")));
        Assert.True(Entry("A").IsInstalled);
        Assert.True(Entry("A").IsAutoInstalled);
        Assert.True(_w.SentinelIntact());
    }

    [Fact]
    public void Conflicts_block_the_plan()
    {
        Apply(new ChangeRequest { Install = new[] { "B" } });
        var plan = Run(s => s.Preview(_target, new ChangeRequest { Install = new[] { "C" } }));
        Assert.NotEmpty(plan.Conflicts);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void Removing_a_dependency_takes_its_dependents_and_unneeded_auto_installs_along()
    {
        Apply(new ChangeRequest { Install = new[] { "B", "F" } });
        var plan = Run(s => s.Preview(_target, new ChangeRequest { Remove = new[] { "B" } }));
        Assert.Contains(plan.Changes, c => c.Identifier == "B" && c.Action == ChangeAction.Remove && c.ByPlayer);
        Assert.Contains(plan.Changes, c => c.Identifier == "A" && c.Action == ChangeAction.Remove && !c.ByPlayer);
        Assert.DoesNotContain(plan.Changes, c => c.Identifier == "F");

        var report = Apply(new ChangeRequest { Remove = new[] { "B" } });
        Assert.Equal(1, report.Removed);
        Assert.False(File.Exists(_w.GamePath("GameData/B/B.dll")));
        Assert.False(File.Exists(_w.GamePath("GameData/A/A.dll")));
        Assert.True(File.Exists(_w.GamePath("GameData/F/F.cfg")));
    }

    [Fact]
    public void A_failed_change_leaves_the_setup_as_it_was()
    {
        Apply(new ChangeRequest { Install = new[] { "A" } });
        var before = _w.GameDataFiles();
        // Clash ships GameData/A/A.dll, which A owns: CKAN refuses mid-transaction, after F was already placed.
        var ex = Assert.Throws<LauncherException>(() => Apply(new ChangeRequest { Install = new[] { "F", "Clash" } }));
        Assert.NotEqual(ErrorCode.Unknown, ex.Code);
        Assert.Equal(before, _w.GameDataFiles());
        Assert.False(Entry("F").IsInstalled);
        Assert.StartsWith("A-DLL-1.1-newer", File.ReadAllText(_w.GamePath("GameData/A/A.dll")), StringComparison.Ordinal);
    }

    [Fact]
    public void Updates_are_offered_and_applied()
    {
        // An exact older version (as a shared setup would pin it) leaves something to update.
        Apply(new ChangeRequest { Install = new[] { "A" }, Versions = new Dictionary<string, string> { ["A"] = "1.0" } });
        Assert.StartsWith("A-DLL-1.0", File.ReadAllText(_w.GamePath("GameData/A/A.dll")), StringComparison.Ordinal);
        var a = Entry("A");
        Assert.True(a.HasUpdate);
        Assert.Equal("1.0", a.InstalledVersion);

        var report = Apply(new ChangeRequest { Upgrade = new[] { "A" } });
        Assert.Equal(1, report.Upgraded);
        Assert.StartsWith("A-DLL-1.1-newer", File.ReadAllText(_w.GamePath("GameData/A/A.dll")), StringComparison.Ordinal);
        Assert.False(Entry("A").HasUpdate);
    }

    [Fact]
    public void A_folder_CKAN_has_open_is_read_only_and_refuses_changes()
    {
        var lockFile = Path.Combine(_w.Profile.GameDir, "CKAN", "registry.locked");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        // Like CKAN: the file holds its PID and stays open, unshared, for as long as CKAN runs.
        using (var held = new FileStream(lockFile, FileMode.Create, FileAccess.Write, FileShare.None, 512, FileOptions.DeleteOnClose))
        {
            held.Write(System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            held.Flush();
            Assert.True(Run(s => s.Build(_target)).ReadOnly);
            var ex = Assert.Throws<LauncherException>(() => Apply(new ChangeRequest { Install = new[] { "F" } }));
            Assert.Equal(ErrorCode.CkanBusy, ex.Code);
            Assert.False(File.Exists(_w.GamePath("GameData/F/F.cfg")));
        }
    }

    [Fact]
    public void A_lock_left_behind_by_a_crash_does_not_block_the_folder()
    {
        var lockFile = Path.Combine(_w.Profile.GameDir, "CKAN", "registry.locked");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        File.WriteAllText(lockFile, "4242"); // power cut: the file stayed, nobody holds it
        Assert.False(Run(s => s.Build(_target)).ReadOnly);
        Assert.False(File.Exists(lockFile));
        Apply(new ChangeRequest { Install = new[] { "F" } });
        Assert.True(File.Exists(_w.GamePath("GameData/F/F.cfg")));
    }

    [Fact]
    public void Pack_setups_are_browse_only()
    {
        var pack = _target with { PackPins = new Dictionary<string, string> { ["F"] = "1.0" }, PackGameVersion = "1.12.5" };
        var snap = Run(s => s.Build(pack));
        Assert.True(snap.ReadOnly);
        Assert.True(snap.Entries.Single(e => e.Identifier == "F").IsInPack);
        Assert.Throws<LauncherException>(() => Run(s => s.Apply(pack, new ChangeRequest { Install = new[] { "A" } }, null, CancellationToken.None)));
    }
}
