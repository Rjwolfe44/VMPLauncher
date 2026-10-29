using System.Collections.Concurrent;

namespace VmpLauncher.Ckan.Backend;

/// <summary>
/// CKAN is not thread-safe (static registry cache, ambient TransactionScope, Autofac singleton), so every CKAN
/// call goes through this single dedicated thread. Callers get a Task; the work itself is synchronous.
/// </summary>
public sealed class CkanWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    public CkanWorker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ckan-worker" };
        _thread.Start();
    }

    public bool IsOnWorkerThread => Thread.CurrentThread == _thread;

    /// <summary>How long <see cref="Dispose"/> waits for the work item that is running; tests shorten it.</summary>
    internal TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>True while the worker thread runs (it ends after <see cref="Dispose"/> once the running item returns).</summary>
    internal bool IsWorkerAlive => _thread.IsAlive;

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        if (IsOnWorkerThread)
        {
            return Task.FromResult(work());
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Execute()
        {
            // Work still queued when the worker is disposed is cancelled, not run.
            if (_disposed || ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct.IsCancellationRequested ? ct : CancellationToken.None);
                return;
            }
            try
            {
                tcs.TrySetResult(work());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
        try
        {
            _queue.Add(Execute, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Dispose completed the queue between the check above and the Add.
            throw new ObjectDisposedException(nameof(CkanWorker));
        }
        return tcs.Task;
    }

    public Task RunAsync(Action work, CancellationToken ct = default)
        => RunAsync(() => { work(); return 0; }, ct);

    /// <summary>Synchronous variant for the CLI and tests.</summary>
    public T Run<T>(Func<T> work) => RunAsync(work).GetAwaiter().GetResult();
    public void Run(Action work) => RunAsync(work).GetAwaiter().GetResult();

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            action();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _queue.CompleteAdding();
        // Cancel what is still queued (each item sees _disposed); the item that is running finishes on its own.
        while (_queue.TryTake(out var pending))
        {
            pending();
        }
        if (IsOnWorkerThread)
        {
            return;
        }
        if (_thread.Join(DisposeTimeout))
        {
            _queue.Dispose();
        }
        // Otherwise the worker is still inside a CKAN call. Disposing the queue under it would throw
        // ObjectDisposedException on that thread (and end the process) once the call returns; instead it drains the
        // completed queue and exits by itself. The queue is left to the garbage collector.
    }
}
