using System.ComponentModel;
using System.Windows.Threading;

namespace VmpLauncher.App.Services;

/// <summary>
/// Samples the <see cref="OperationRunner"/> at 10 Hz on the UI thread, but only while an operation is running (plus one
/// last sample when it ends, so the final state shows). The view models used to keep a 100 ms timer ticking for the
/// whole session, which woke the UI thread ten times a second on an idle launcher.
/// Create it on the UI thread.
/// </summary>
public sealed class OperationPoller : IDisposable
{
    private readonly OperationRunner _runner;
    private readonly Action _sample;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer;

    public OperationPoller(OperationRunner runner, Action sample)
    {
        _runner = runner;
        _sample = sample;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => _sample();
        runner.PropertyChanged += OnRunnerChanged;
        if (runner.IsBusy)
        {
            Update();
        }
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationRunner.IsBusy))
        {
            // Raised on whatever thread the operation runs on; the timer belongs to the UI thread.
            _dispatcher.BeginInvoke(Update);
        }
    }

    private void Update()
    {
        if (_runner.IsBusy)
        {
            _timer.Start();
            _sample();
        }
        else
        {
            _timer.Stop();
            _sample();
        }
    }

    public void Dispose()
    {
        _runner.PropertyChanged -= OnRunnerChanged;
        _timer.Stop();
    }
}
