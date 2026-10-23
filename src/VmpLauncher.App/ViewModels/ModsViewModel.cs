using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Locking;

namespace VmpLauncher.App.ViewModels;

public sealed record ModRow(string Name, string Identifier, string Version, string Summary, string Size, long SizeBytes, string Badge, string BadgeKind, bool IsCustom, bool IsPrivate, bool IsDependency, string SearchText);

/// <summary>Read-only browser of the pack's mods (the pack is locked to match the server, so nothing here installs).</summary>
public partial class ModsViewModel : ObservableObject
{
    private readonly LauncherService _launcher;
    private readonly DispatcherTimer _debounce;
    private List<ModRow> _all = new();
    private string? _loadedFor;

    public ModsViewModel(LauncherService launcher)
    {
        _launcher = launcher;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyFilter(); };
        launcher.StateChanged += () => { if (_launcher.CurrentPack?.LockSha256 != _loadedFor) { Load(); } };
    }

    public ObservableCollection<ModRow> Mods { get; } = new();

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _filter = "all"; // all | custom | private | deps
    [ObservableProperty] private string _summary = "Loading the pack…";
    [ObservableProperty] private string _countLine = string.Empty;
    [ObservableProperty] private bool _isEmpty;

    partial void OnSearchChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SetFilter(string value) => Filter = value;

    public void Load()
    {
        var pack = _launcher.CurrentPack;
        if (pack is null)
        {
            Summary = "The pack is not loaded yet. Open Home first.";
            return;
        }
        _loadedFor = pack.LockSha256;
        _all = pack.Lock.Ckan.Modules
            .Where(m => m.Kind == ModuleKindDto.Package)
            .Select(ToRow)
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var custom = _all.Count(r => r.IsCustom);
        var priv = _all.Count(r => r.IsPrivate);
        Summary = $"{pack.Lock.Pack.Name} {pack.Lock.Pack.Version} pins {_all.Count} mods to exact versions, so everyone runs the same files as the server.";
        CountLine = $"{_all.Count} mods · {custom} custom · {priv} private · {_all.Count(r => r.IsDependency)} pulled in as dependencies";
        ApplyFilter();
    }

    private static ModRow ToRow(LockedModule m)
    {
        var meta = m.Metadata;
        string Str(string name) => meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        var name = Str("name");
        var isPrivate = m.Artifact?.Distribution == "private-sideload";
        var isCustom = m.Origin == ModuleOrigin.Custom;
        var (badge, kind) = isPrivate ? ("Private", "warn") : isCustom ? ("Custom", "info") : m.AutoInstalled ? ("Dependency", "neutral") : (string.Empty, "neutral");
        var size = m.Artifact?.Size ?? 0;
        var summary = System.Text.RegularExpressions.Regex.Replace(Str("abstract"), @"\s+", " ").Trim();
        return new ModRow(
            string.IsNullOrWhiteSpace(name) ? m.Identifier : name,
            m.Identifier,
            m.Version,
            summary,
            size > 0 ? Format.Bytes(size) : string.Empty,
            size,
            badge,
            kind,
            isCustom,
            isPrivate,
            m.AutoInstalled,
            $"{name} {m.Identifier} {summary} {Str("author")}".ToLowerInvariant());
    }

    private void ApplyFilter()
    {
        var q = Search.Trim().ToLowerInvariant();
        IEnumerable<ModRow> rows = _all;
        rows = Filter switch
        {
            "custom" => rows.Where(r => r.IsCustom),
            "private" => rows.Where(r => r.IsPrivate),
            "deps" => rows.Where(r => r.IsDependency),
            _ => rows,
        };
        if (q.Length > 0)
        {
            var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rows = rows.Where(r => terms.All(t => r.SearchText.Contains(t, StringComparison.Ordinal)));
        }
        Mods.Clear();
        foreach (var r in rows)
        {
            Mods.Add(r);
        }
        IsEmpty = Mods.Count == 0;
    }
}
