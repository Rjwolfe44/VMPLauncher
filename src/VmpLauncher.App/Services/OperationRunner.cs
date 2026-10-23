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
    Publish,
}

/// <summary>Latest known state of the running operation; sampled by the UI at 10 Hz, never pushed per event.</summary>
public sealed record OperationSnapshot(OperationKind Kind, string Title, string Message, int Percent, double? BytesPerSecond, string? TimeLeft, DateTimeOffset StartedUtc);

/// <summary>How the last operation ended (shown on the Activity page and in a toast).</summary>
public sealed record OperationOutcome(OperationKind Kind, string Title, bool Succeeded, bool Cancelled, string Message, TimeSpan Elapsed, DateTimeOffset FinishedUtc);

/// <summary>
/// One mutating operation at a time. Core/CKAN report progress on worker threads; the runner keeps the latest
/// snapshot and a numbered log under a lock, and the UI polls both with a DispatcherTimer so the dispatcher is never
/// flooded and never reads a list another thread is writing.
/// </summary>
public sealed partial class OperationRunner : ObservableObject
{
    private const int MaxLines = 500;

    private readonly ILogger<OperationRunner> _log;
    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private long _firstLineNumber; // number of the line at _lines[0]
    private OperationSnapshot? _snapshot;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public OperationRunner(ILogger<OperationRunner> log)
    {
        _log = log;
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private OperationOutcome? _lastOutcome;

    /// <summary>Raised on the thread that finished the operation.</summary>
    public event Action<OperationOutcome>? Finished;

    public OperationSnapshot? Snapshot
    {
        get { lock (_gate) { return _snapshot; } }
    }

    /// <summary>Total number of lines ever logged by the current/last operation.</summary>
    public long LineCount
    {
        get { lock (_gate) { return _firstLineNumber + _lines.Count; } }
    }

    /// <summary>Copies the lines numbered <paramref name="from"/> and later (older ones may have been dropped).</summary>
    public IReadOnlyList<string> LinesSince(long from, out long next)
    {
        lock (_gate)
        {
            next = _firstLineNumber + _lines.Count;
            var start = (int)Math.Max(0, from - _firstLineNumber);
            return start >= _lines.Count ? Array.Empty<string>() : _lines.GetRange(start, _lines.Count - start);
        }
    }

    public IProgress<CkanProgress> Progress => new ProgressSink(this);

    public void RequestCancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
        }
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        Report("Cancelling… your previous setup is being restored.", percent: null);
    }

    public async Task<T> RunAsync<T>(OperationKind kind, string title, Func<IProgress<CkanProgress>, CancellationToken, Task<T>> work)
    {
        CancellationTokenSource cts;
        DateTimeOffset started;
        lock (_gate)
        {
            if (_busy)
            {
                throw new Core.Diagnostics.LauncherException(Core.Diagnostics.ErrorCode.ProfileBusy, "Another operation is already running.", Core.Diagnostics.RecoveryHint.Retry);
            }
            _busy = true;
            cts = _cts = new CancellationTokenSource();
            started = DateTimeOffset.UtcNow;
            _snapshot = new OperationSnapshot(kind, title, "Starting…", 0, null, null, started);
            _firstLineNumber += _lines.Count;
            _lines.Clear();
        }
        IsBusy = true;
        _log.LogInformation("Operation {Kind} started: {Title}", kind, title);
        var succeeded = false;
        var cancelled = false;
        var message = "Done.";
        try
        {
            var result = await work(Progress, cts.Token).ConfigureAwait(false);
            succeeded = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            message = "Cancelled. Your previous setup was kept.";
            throw;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            throw;
        }
        finally
        {
            var elapsed = DateTimeOffset.UtcNow - started;
            _log.LogInformation("Operation {Kind} finished: {Outcome} after {Elapsed}", kind, succeeded ? "ok" : cancelled ? "cancelled" : "failed", elapsed);
            lock (_gate)
            {
                _lines.Add(succeeded ? $"Finished in {elapsed:m\\:ss}." : cancelled ? "Cancelled." : "Failed: " + message);
                _cts = null;
                _snapshot = null;
                _busy = false;
            }
            cts.Dispose();
            var outcome = new OperationOutcome(kind, title, succeeded, cancelled, succeeded ? $"Finished in {elapsed:m\\:ss}." : message, elapsed, DateTimeOffset.UtcNow);
            LastOutcome = outcome;
            IsBusy = false;
            Finished?.Invoke(outcome);
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
            _snapshot = _snapshot with
            {
                Message = message,
                Percent = percent ?? _snapshot.Percent,
                BytesPerSecond = bytesPerSecond ?? _snapshot.BytesPerSecond,
                TimeLeft = timeLeft ?? _snapshot.TimeLeft,
            };
            if (_lines.Count == 0 || _lines[^1] != message)
            {
                _lines.Add(message);
                if (_lines.Count > MaxLines)
                {
                    var drop = _lines.Count - MaxLines;
                    _lines.RemoveRange(0, drop);
                    _firstLineNumber += drop;
                }
            }
        }
    }

    private sealed class ProgressSink(OperationRunner owner) : IProgress<CkanProgress>
    {
        public void Report(CkanProgress value)
        {
            var rate = value.Rate;
            // Percent -1 means "log line, no progress information". Keep the last percentage while a phase is under way,
            // but once a phase reached 100% the next silent step (snapshot, apply, verify) shows as indeterminate
            // instead of a bar stuck at 100%.
            int? percent = value.Percent >= 0 ? value.Percent : owner.Snapshot is { Percent: >= 100 } ? -1 : null;
            owner.Report(value.Message, percent, rate?.BytesPerSecond, rate?.TimeLeftString);
        }
    }
}
