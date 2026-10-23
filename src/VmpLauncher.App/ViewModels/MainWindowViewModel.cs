using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;
using Wpf.Ui.Controls;

namespace VmpLauncher.App.ViewModels;

public partial class NavItem : ObservableObject
{
    public required AppPage Page { get; init; }
    public required string Label { get; init; }
    public required SymbolRegular Symbol { get; init; }
    public string? Shortcut { get; init; }
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string? _badge;
    [ObservableProperty] private bool _isVisible = true;
}

/// <summary>Shell state: sidebar, live activity strip, server status card, update pill.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly OperationRunner _runner;
    private readonly IAppNavigator _navigator;
    private readonly LauncherService _launcher;
    private readonly DispatcherTimer _timer;

    public MainWindowViewModel(OperationRunner runner, IAppNavigator navigator, LauncherService launcher, ServerStatusService server, UpdateService updates, HostToolsService host)
    {
        _runner = runner;
        _navigator = navigator;
        _launcher = launcher;
        Server = server;
        Updates = updates;
        Host = host;
        NavItems = new ObservableCollection<NavItem>
        {
            new() { Page = AppPage.Home, Label = "Home", Symbol = SymbolRegular.Home24, Shortcut = "Ctrl+1" },
            new() { Page = AppPage.Play, Label = "Play", Symbol = SymbolRegular.Rocket24, Shortcut = "Ctrl+2" },
            new() { Page = AppPage.Mods, Label = "Mods", Symbol = SymbolRegular.PuzzlePiece24, Shortcut = "Ctrl+3" },
            new() { Page = AppPage.Activity, Label = "Activity", Symbol = SymbolRegular.ArrowDownload24, Shortcut = "Ctrl+4" },
            new() { Page = AppPage.Doctor, Label = "Doctor", Symbol = SymbolRegular.Stethoscope24, Shortcut = "Ctrl+5" },
        };
        FooterItems = new ObservableCollection<NavItem>
        {
            new() { Page = AppPage.Host, Label = "Host tools", Symbol = SymbolRegular.CloudArrowUp24, IsVisible = host.IsHost },
            new() { Page = AppPage.Settings, Label = "Settings", Symbol = SymbolRegular.Settings24, Shortcut = "Ctrl+," },
            new() { Page = AppPage.About, Label = "About", Symbol = SymbolRegular.Info24 },
        };
        navigator.Navigated += OnNavigated;
        launcher.StateChanged += UpdatePackLine;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    public ObservableCollection<NavItem> NavItems { get; }
    public ObservableCollection<NavItem> FooterItems { get; }
    public ServerStatusService Server { get; }
    public UpdateService Updates { get; }
    public HostToolsService Host { get; }

    [ObservableProperty] private bool _isOnboarding;
    [ObservableProperty] private bool _activityVisible;
    [ObservableProperty] private string _activityTitle = string.Empty;
    [ObservableProperty] private string _activityText = string.Empty;
    [ObservableProperty] private int _activityPercent;
    [ObservableProperty] private bool _activityIndeterminate;
    [ObservableProperty] private string _packLine = "Loading pack…";
    [ObservableProperty] private string _packTooltip = string.Empty;

    [RelayCommand]
    private void Navigate(NavItem item) => _navigator.Navigate(item.Page);

    [RelayCommand]
    private void OpenActivity() => _navigator.Navigate(AppPage.Activity);

    [RelayCommand]
    private void CancelActivity() => _runner.RequestCancel();

    [RelayCommand]
    private void RestartToUpdate() => Updates.TryRestartToUpdate();

    private void OnNavigated(AppPage page)
    {
        IsOnboarding = page == AppPage.Welcome;
        foreach (var n in NavItems.Concat(FooterItems))
        {
            n.IsActive = n.Page == page;
        }
        Sample();
    }

    private void UpdatePackLine()
    {
        var pack = _launcher.CurrentPack;
        var status = _launcher.GetStatus();
        PackLine = pack is null
            ? "No pack loaded"
            : status.Installed is { } i && i.PackVersion != pack.Lock.Pack.Version
                ? $"Update to {pack.Lock.Pack.Version}"
                : $"Pack {pack.Lock.Pack.Version}";
        PackTooltip = pack is null ? string.Empty : $"{pack.Lock.Pack.Name} {pack.Lock.Pack.Version} from {pack.Source}";
    }

    private void Sample()
    {
        var s = _runner.Snapshot;
        var activity = NavItems.First(n => n.Page == AppPage.Activity);
        if (s is null)
        {
            ActivityVisible = false;
            activity.Badge = null;
            return;
        }
        var pct = Math.Clamp(s.Percent, 0, 100);
        activity.Badge = s.Percent > 0 ? $"{pct}%" : "•";
        // The Home and Activity pages show progress themselves; the strip covers every other page.
        ActivityVisible = _navigator.Current is not (AppPage.Activity or AppPage.Home);
        ActivityTitle = s.Title;
        ActivityPercent = pct;
        ActivityIndeterminate = s.Percent <= 0;
        var speed = s.BytesPerSecond is { } bps && bps > 0 ? $" · {Format.Speed(bps)}" : string.Empty;
        var left = string.IsNullOrEmpty(s.TimeLeft) ? string.Empty : $" · {s.TimeLeft} left";
        ActivityText = $"{s.Message}{speed}{left}";
    }
}

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long bytes)
    {
        double v = bytes;
        var i = 0;
        while (v >= 1000 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {Units[i]}";
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Ago(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        return age.TotalSeconds < 60 ? "just now"
             : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
             : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
             : $"{(int)age.TotalDays} d ago";
    }
}
