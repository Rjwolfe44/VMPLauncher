using System.IO;
using VmpLauncher.Core.Transactions;
using Xunit;

namespace VmpLauncher.Tests.Core;

/// <summary>
/// The contract snapshot, delete and verify rely on when they run on several threads: a cancel or a failure stops every
/// worker before the call returns, so nothing keeps changing files behind a rollback.
/// </summary>
public class ParallelFilesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancel_or_failure_stops_every_worker_before_returning(bool cancel)
    {
        const int count = 20_000;
        using var cts = new CancellationTokenSource();
        var started = 0;
        var running = 0;
        Exception? caught = null;
        try
        {
            ParallelFiles.For(count, 8, i =>
            {
                Interlocked.Increment(ref running);
                try
                {
                    if (Interlocked.Increment(ref started) == 500)
                    {
                        if (cancel)
                        {
                            cts.Cancel();
                        }
                        else
                        {
                            throw new IOException("disk full");
                        }
                    }
                    Thread.SpinWait(2_000);
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
            }, cts.Token);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.Equal(0, Volatile.Read(ref running));
        var atReturn = Volatile.Read(ref started);
        Thread.Sleep(100);
        Assert.Equal(atReturn, Volatile.Read(ref started)); // no worker started another item after the call returned
        Assert.True(atReturn < count / 4, $"workers kept going: {atReturn} items started");
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(caught);
        }
        else
        {
            var io = Assert.IsType<IOException>(caught); // the original exception, not an AggregateException
            Assert.Equal("disk full", io.Message);
        }
    }
}
