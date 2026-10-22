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

    public CkanWorker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ckan-worker" };
        _thread.Start();
    }

    public bool IsOnWorkerThread => Thread.CurrentThread == _thread;

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        if (IsOnWorkerThread)
        {
            return Task.FromResult(work());
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
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
        }, CancellationToken.None);
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
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
