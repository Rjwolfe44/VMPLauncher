using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VmpLauncher.App.Services;

namespace VmpLauncher.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly OperationRunner _runner;
    private readonly DispatcherTimer _timer;

    public MainWindowViewModel(OperationRunner runner)
    {
        _runner = runner;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    [ObservableProperty]
    private bool _activityVisible;

    [ObservableProperty]
    private string _activityText = string.Empty;

    [ObservableProperty]
    private int _activityPercent;

    private void Sample()
    {
        var s = _runner.Snapshot;
        if (s is null)
        {
            ActivityVisible = false;
            return;
        }
        ActivityVisible = true;
        ActivityPercent = Math.Clamp(s.Percent, 0, 100);
        var speed = s.BytesPerSecond is { } bps && bps > 0 ? $" · {Format.Speed(bps)}" : string.Empty;
        var left = string.IsNullOrEmpty(s.TimeLeft) ? string.Empty : $" · {s.TimeLeft} left";
        ActivityText = $"{s.Title} · {s.Message}{speed}{left}";
    }
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1000 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";
}
