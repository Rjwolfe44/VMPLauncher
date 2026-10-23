using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;

namespace VmpLauncher.App.ViewModels;

/// <summary>Activity page: samples the operation runner at 10 Hz and appends new log lines (never reads a list another thread writes).</summary>
public partial class InstallViewModel : ObservableObject
{
    private const int MaxShown = 400;

    private readonly OperationRunner _runner;
    private readonly IAppNavigator _navigation;
    private readonly DispatcherTimer _timer;
    private long _nextLine;

    public InstallViewModel(OperationRunner runner, IAppNavigator navigation)
    {
        _runner = runner;
        _navigation = navigation;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty] private string _title = "Nothing running";
    [ObservableProperty] private string _message = "Installs, updates and repairs show their progress here.";
    [ObservableProperty] private string _stateKind = "neutral";
    [ObservableProperty] private int _percent;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private string _speed = string.Empty;
    [ObservableProperty] private string _timeLeft = string.Empty;
    [ObservableProperty] private string _elapsed = string.Empty;

    public ObservableCollection<string> Lines { get; } = new();

    /// <summary>Raised after new lines were appended (the view scrolls to the end).</summary>
    public event Action? LinesAppended;

    private void Sample()
    {
        var s = _runner.Snapshot;
        IsRunning = s is not null;
        if (s is not null)
        {
            Title = s.Title;
            Message = s.Message;
            StateKind = "checking";
            Indeterminate = s.Percent <= 0;
            Percent = Math.Clamp(s.Percent, 0, 100);
            Speed = s.BytesPerSecond is { } bps && bps > 0 ? Format.Speed(bps) : string.Empty;
            TimeLeft = string.IsNullOrEmpty(s.TimeLeft) ? string.Empty : s.TimeLeft + " left";
            Elapsed = (DateTimeOffset.UtcNow - s.StartedUtc).ToString(@"m\:ss") + " elapsed";
        }
        else if (_runner.LastOutcome is { } o)
        {
            Title = o.Succeeded ? $"{o.Title}: done" : o.Cancelled ? $"{o.Title}: cancelled" : $"{o.Title}: failed";
            Message = $"{o.Message} ({Format.Ago(o.FinishedUtc)})";
            StateKind = o.Succeeded ? "ok" : o.Cancelled ? "warn" : "error";
            Indeterminate = false;
            Percent = o.Succeeded ? 100 : Percent;
            Speed = TimeLeft = string.Empty;
            Elapsed = $"took {o.Elapsed:m\\:ss}";
        }

        if (s is not null && s.StartedUtc != _shownStart)
        {
            // A new operation started: show its log from the top.
            _shownStart = s.StartedUtc;
            Lines.Clear();
        }
        var added = _runner.LinesSince(_nextLine, out var next);
        _nextLine = next;
        if (added.Count > 0)
        {
            foreach (var line in added)
            {
                Lines.Add(line);
            }
            while (Lines.Count > MaxShown)
            {
                Lines.RemoveAt(0);
            }
            HasHistory = true;
            LinesAppended?.Invoke();
        }
    }

    private DateTimeOffset _shownStart;

    [RelayCommand]
    private void Cancel() => _runner.RequestCancel();

    [RelayCommand]
    private void CopyLog()
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Lines));
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private void GoHome() => _navigation.Navigate(AppPage.Home);
}
