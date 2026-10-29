using VmpLauncher.Ckan.Backend;
using Xunit;

namespace VmpLauncher.IntegrationTests;

public class CkanWorkerTests
{
    [Fact]
    public async Task Dispose_while_work_runs_cancels_queued_items_and_lets_the_running_one_finish()
    {
        var worker = new CkanWorker { DisposeTimeout = TimeSpan.FromMilliseconds(100) };
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var running = worker.RunAsync(() =>
        {
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
            return 1;
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var queued = worker.RunAsync(() => 2);

        worker.Dispose(); // times out waiting for the running item
        Assert.True(queued.IsCanceled);
        Assert.Throws<ObjectDisposedException>(() => { _ = worker.RunAsync(() => 3); });
        Assert.True(worker.IsWorkerAlive);

        gate.Set();
        Assert.Equal(1, await running);
        // The worker thread ends cleanly instead of dying on a disposed queue (which would take the process down).
        Assert.True(SpinWait.SpinUntil(() => !worker.IsWorkerAlive, TimeSpan.FromSeconds(10)));
    }
}
