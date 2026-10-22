using System.IO;
using VmpLauncher.Ckan;
using VmpLauncher.Ckan.Backend;
using VmpLauncher.Core;
using VmpLauncher.Core.Profiles;

namespace VmpLauncher.IntegrationTests.Fixtures;

/// <summary>Everything one test needs: isolated launcher data dir, fake KSP, fixture repo + server, a seeded profile.</summary>
public sealed class TestWorld : IDisposable
{
    private static readonly object BootstrapGate = new();
    private static readonly string SharedDataRoot = Path.Combine(Path.GetTempPath(), "vmpl-itests", "data");

    public TestWorld(string name)
    {
        Root = Path.Combine(Path.GetTempPath(), "vmpl-itests", name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Root);
        lock (BootstrapGate)
        {
            Paths = new LauncherPaths(SharedDataRoot);
            CkanBootstrap.Initialize(Paths);
        }
        CacheDir = Path.Combine(Root, "cache");
        Directory.CreateDirectory(CacheDir);
        SourceKsp = FakeKsp.Create(Path.Combine(Root, "ksp"));
        Server = new LocalArtifactServer(Path.Combine(Root, "archives"));
        Repo = new FixtureRepo(Path.Combine(Root, "archives"), Server.BaseUrl);
        Worker = new CkanWorker();
        Profile = new ProfilePaths(Path.Combine(Root, "profile"));
        Profile.EnsureCreated();
        new ProfileSeeder().Seed(new ProfileSeeder.Plan(SourceKsp, Profile.GameDir, HardLink: false, Array.Empty<string>()));
        // Seeding never copies saves; simulate a player's save that every operation must leave alone.
        var sentinel = Path.Combine(Profile.GameDir, FakeKsp.SentinelRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, FakeKsp.SentinelContent);
        Profile.WriteManifest(new ProfileManifest { Id = "test", Name = "test", Seed = new SeedInfo { FromPath = SourceKsp, BuildId64 = "03190", Mode = "copy" } });
    }

    public string Root { get; }
    public LauncherPaths Paths { get; }
    public string CacheDir { get; }
    public string SourceKsp { get; }
    public LocalArtifactServer Server { get; }
    public FixtureRepo Repo { get; }
    public CkanWorker Worker { get; }
    public ProfilePaths Profile { get; }
    public StringWriter Log { get; } = new();

    public ProfileOperations Ops(Action<CKAN.CkanModule>? faultInjector = null) => new(Paths, Log) { FaultInjector = faultInjector };

    public ProfileOperations.Request Request(string lockPath, bool offline = false, IReadOnlyDictionary<string, string>? sideload = null, bool keepSnapshot = false)
        => new(Profile, new ProfileOperations.LockSource(lockPath, Path.Combine(Path.GetDirectoryName(lockPath)!, "inventory.json.gz")), CacheDir, offline, sideload, keepSnapshot);

    public string LockDir(string name) => Path.Combine(Root, "locks", name);

    public string GamePath(string rel) => Path.Combine(Profile.GameDir, rel.Replace('/', Path.DirectorySeparatorChar));

    public IReadOnlyList<string> GameDataFiles()
        => Directory.EnumerateFiles(Path.Combine(Profile.GameDir, "GameData"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Profile.GameDir, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    public bool SentinelIntact() => File.ReadAllText(Path.Combine(Profile.GameDir, FakeKsp.SentinelRelativePath)) == FakeKsp.SentinelContent;

    public void Dispose()
    {
        Worker.Dispose();
        Server.Dispose();
        CKAN.RegistryManager.DisposeAll();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
