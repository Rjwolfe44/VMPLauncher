using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;

namespace VmpLauncher.App.ViewModels;

/// <summary>Activity page: samples the operation runner at 10 Hz and shows the recent log lines.</summary>
public partial class InstallViewModel : ObservableObject
{
    private readonly OperationRunner _runner;
    private readonly DispatcherTimer _timer;
    private int _shownLines;

    public InstallViewModel(OperationRunner runner)
    {
        _runner = runner;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _title = "Nothing running";
    [ObservableProperty] private string _message = "Install or update from the Home page to see progress here.";
    [ObservableProperty] private int _percent;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private string _speed = string.Empty;
    [ObservableProperty] private string _timeLeft = string.Empty;
    [ObservableProperty] private string _elapsed = string.Empty;

    public ObservableCollection<string> Lines { get; } = new();

    private void Sample()
    {
        var s = _runner.Snapshot;
        IsRunning = s is not null;
        if (s is null)
        {
            if (Title != "Nothing running" && Lines.Count > 0)
            {
                Title = "Finished";
                Message = Lines[^1];
            }
            return;
        }
        Title = s.Title;
        Message = s.Message;
        Indeterminate = s.Percent < 0;
        Percent = Math.Clamp(s.Percent, 0, 100);
        Speed = s.BytesPerSecond is { } bps && bps > 0 ? Format.Speed(bps) : string.Empty;
        TimeLeft = s.TimeLeft ?? string.Empty;
        Elapsed = (DateTimeOffset.UtcNow - s.StartedUtc).ToString(@"m\:ss");
        var lines = _runner.RecentLines;
        if (lines.Count < _shownLines)
        {
            Lines.Clear();
            _shownLines = 0;
        }
        for (var i = _shownLines; i < lines.Count; i++)
        {
            Lines.Add(lines[i]);
        }
        _shownLines = lines.Count;
        while (Lines.Count > 200)
        {
            Lines.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void Cancel() => _runner.RequestCancel();
}
