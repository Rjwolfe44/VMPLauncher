using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using VmpLauncher.App;
using VmpLauncher.App.Services;
using Xunit;

namespace VmpLauncher.Tests.Net;

/// <summary>
/// The shell used to tick a 100 ms timer for the whole session; it now samples the operation runner only while an
/// operation runs. These pin that: no samples when idle, samples while busy, one last sample at the end, then silence.
/// </summary>
public sealed class OperationPollerTests
{
    [Fact]
    public void Polls_only_while_an_operation_runs_and_samples_once_more_when_it_ends()
    {
        var idle = -1;
        var duringWork = -1;
        var afterEnd = -1;
        var finalDelta = -1;
        var quietAfter = -1;

        RunOnUiThread(async dispatcher =>
        {
            var runner = new OperationRunner(NullLogger<OperationRunner>.Instance);
            var samples = 0;
            using var poller = new OperationPoller(runner, () => samples++);

            await Task.Delay(400);
            idle = samples;

            var gate = new TaskCompletionSource();
            var work = runner.RunAsync(OperationKind.Verify, "test", async (_, _) =>
            {
                await gate.Task.ConfigureAwait(false);
                return 0;
            });
            await Task.Delay(500);
            duringWork = samples;

            var beforeEnd = samples;
            gate.SetResult();
            await work;
            await Task.Delay(300);
            afterEnd = samples;
            finalDelta = samples - beforeEnd;

            await Task.Delay(500);
            quietAfter = samples - afterEnd;
            dispatcher.InvokeShutdown();
        });

        Assert.Equal(0, idle);
        Assert.InRange(duringWork, 2, 10); // started with one sample, then ~10 Hz for half a second
        Assert.True(finalDelta >= 1, "the end of an operation must produce a last sample so the final state shows");
        Assert.Equal(0, quietAfter);
    }

    [Fact]
    public void Software_rendering_is_the_default_and_gpu_is_opt_in()
    {
        Assert.False(StartupOptions.Parse(Array.Empty<string>()).GpuRendering);
        Assert.True(StartupOptions.Parse(new[] { "--gpu" }).GpuRendering);
    }

    private static void RunOnUiThread(Func<Dispatcher, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body(dispatcher);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the dispatcher thread did not finish");
        if (failure is not null)
        {
            throw failure;
        }
    }
}
