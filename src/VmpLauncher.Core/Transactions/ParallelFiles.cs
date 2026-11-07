using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace VmpLauncher.Core.Transactions;

/// <summary>
/// Runs per-file work on a bounded number of threads. On cancellation or the first failure every worker stops at its
/// next item, the call returns only after all of them have stopped (nothing keeps writing behind the caller's back),
/// and the caller sees the original exception, not an AggregateException.
/// </summary>
public static class ParallelFiles
{
    /// <summary>Below this many items the work runs on the calling thread.</summary>
    private const int SequentialBelow = 64;

    public static void For(int count, int threads, Action<int> body, CancellationToken ct)
    {
        if (threads <= 1 || count < SequentialBelow)
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                body(i);
            }
            return;
        }
        // Small ranges keep the threads busy when some files (big textures) take far longer than others.
        var rangeSize = Math.Clamp(count / (threads * 16), 4, 256);
        try
        {
            Parallel.ForEach(Partitioner.Create(0, count, rangeSize), new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, (range, state) =>
            {
                for (var i = range.Item1; i < range.Item2; i++)
                {
                    if (state.ShouldExitCurrentIteration)
                    {
                        return;
                    }
                    ct.ThrowIfCancellationRequested();
                    body(i);
                }
            });
        }
        catch (AggregateException ae)
        {
            var inner = ae.Flatten().InnerExceptions;
            if (ct.IsCancellationRequested && inner.All(e => e is OperationCanceledException))
            {
                throw new OperationCanceledException(ct);
            }
            ExceptionDispatchInfo.Capture(inner.FirstOrDefault(e => e is not OperationCanceledException) ?? inner[0]).Throw();
            throw; // unreachable
        }
    }
}
