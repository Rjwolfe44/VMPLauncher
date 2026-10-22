using System.IO;
using VmpLauncher.Core;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.App.Services;

/// <summary>settings.json: non-secret user preferences and the current pack/profile selection.</summary>
public sealed record AppSettings
{
    public string? SourceKspPath { get; init; }
    public string? ProfileRoot { get; init; }
    public string ActiveProfileId { get; init; } = "vmp-train-career";
    public string? PlayerName { get; init; }
    public bool DisclaimerAccepted { get; init; }
    public string? SelectedRouteId { get; init; }
    public bool BorderlessWindow { get; init; }
    public bool MinimizeWhilePlaying { get; init; } = true;
    public string Backdrop { get; init; } = "mica"; // mica | solid
    public double TextScale { get; init; } = 1.0;
    public string UpdateChannel { get; init; } = "win";
    public bool AutoCheckUpdates { get; init; } = true;
    public string? CatalogUrl { get; init; }
    /// <summary>Developer/host: a local export folder to use instead of the online catalog.</summary>
    public string? LocalPackDir { get; init; }
    public string? CacheDirOverride { get; init; }
    public bool WelcomeCompleted { get; init; }
    public List<string> RememberedKspPaths { get; init; } = new();
}

public sealed class AppSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _current;

    public AppSettingsStore(LauncherPaths paths)
    {
        _path = paths.SettingsFile;
        _current = Load();
    }

    public event Action<AppSettings>? Changed;

    public AppSettings Current
    {
        get { lock (_gate) { return _current; } }
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings next;
        lock (_gate)
        {
            next = change(_current);
            _current = next;
            LauncherJson.WriteFile(_path, next);
        }
        Changed?.Invoke(next);
    }

    private AppSettings Load()
    {
        try
        {
            return File.Exists(_path) ? LauncherJson.ReadFile<AppSettings>(_path) : new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }
}
