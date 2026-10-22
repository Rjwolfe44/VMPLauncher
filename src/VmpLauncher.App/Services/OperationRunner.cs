using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VmpLauncher.Ckan.Users;

namespace VmpLauncher.App.Services;

public enum OperationKind
{
    None,
    Install,
    Update,
    Repair,
    Verify,
    Recover,
    Capture,
}

/// <summary>Latest known state of the running operation; sampled by the UI at 10 Hz, never pushed per event.</summary>
public sealed record OperationSnapshot(OperationKind Kind, string Title, string Message, int Percent, double? BytesPerSecond, string? TimeLeft, DateTimeOffset StartedUtc);

/// <summary>
/// One mutating operation at a time. Core/CKAN report progress on worker threads; the runner keeps the latest
/// snapshot in a field and the UI polls it with a DispatcherTimer so the dispatcher is never flooded.
/// </summary>
public sealed partial class OperationRunner : ObservableObject
{
    private readonly ILogger<OperationRunner> _log;
    private readonly object _gate = new();
    private OperationSnapshot? _snapshot;
    private CancellationTokenSource? _cts;

    public OperationRunner(ILogger<OperationRunner> log)
    {
        _log = log;
    }

    [ObservableProperty]
    private bool _isBusy;

    public OperationSnapshot? Snapshot
    {
        get { lock (_gate) { return _snapshot; } }
    }

    public List<string> RecentLines { get; } = new();

    public IProgress<CkanProgress> Progress => new ProgressSink(this);

    public void RequestCancel()
    {
        _cts?.Cancel();
        Report("Cancelling…", percent: null);
    }

    public async Task<T> RunAsync<T>(OperationKind kind, string title, Func<IProgress<CkanProgress>, CancellationToken, Task<T>> work)
    {
        lock (_gate)
        {
            if (IsBusy)
            {
                throw new InvalidOperationException("Another operation is already running.");
            }
            _cts = new CancellationTokenSource();
            _snapshot = new OperationSnapshot(kind, title, "Starting…", 0, null, null, DateTimeOffset.UtcNow);
            RecentLines.Clear();
        }
        IsBusy = true;
        _log.LogInformation("Operation {Kind} started: {Title}", kind, title);
        try
        {
            return await work(Progress, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _log.LogInformation("Operation {Kind} finished", kind);
            lock (_gate)
            {
                _cts.Dispose();
                _cts = null;
                _snapshot = null;
            }
            IsBusy = false;
        }
    }

    public void Report(string message, int? percent, double? bytesPerSecond = null, string? timeLeft = null)
    {
        lock (_gate)
        {
            if (_snapshot is null)
            {
                return;
            }
            _snapshot = _snapshot with { Message = message, Percent = percent ?? _snapshot.Percent, BytesPerSecond = bytesPerSecond ?? _snapshot.BytesPerSecond, TimeLeft = timeLeft ?? _snapshot.TimeLeft };
            if (RecentLines.Count == 0 || RecentLines[^1] != message)
            {
                RecentLines.Add(message);
                if (RecentLines.Count > 200)
                {
                    RecentLines.RemoveAt(0);
                }
            }
        }
    }

    private sealed class ProgressSink(OperationRunner owner) : IProgress<CkanProgress>
    {
        public void Report(CkanProgress value)
        {
            var rate = value.Rate;
            owner.Report(value.Message, value.Percent, rate?.BytesPerSecond, rate?.TimeLeftString);
        }
    }
}
