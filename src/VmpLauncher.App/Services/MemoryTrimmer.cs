using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace VmpLauncher.App.Services;

/// <summary>
/// Gives memory back after the launcher has been busy. Loading the CKAN mod list (3,800 mods) or running an install
/// leaves several hundred MB of garbage that the GC would otherwise keep until it happens to run again, which on an
/// idle launcher is never. KSP is very RAM-hungry and runs next to the launcher, so after a quiet moment this does one
/// compacting collection and, while the window is not in use, also trims the working set (the pages fault back in
/// on demand if the player returns).
/// </summary>
public sealed class MemoryTrimmer : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(4);
    private const long MinAllocatedToCollect = 8L * 1024 * 1024;

    private readonly ILogger<MemoryTrimmer> _log;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool _trimWorkingSet;
    private bool _disposed;
    private long _allocatedAtLastCollect;

    public MemoryTrimmer(ILogger<MemoryTrimmer> log)
    {
        _log = log;
        _timer = new Timer(_ => Run(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Compacts the heap once things have been quiet for a few seconds (each call restarts the wait, so a burst of
    /// work costs one collection). With <paramref name="workingSet"/> the working set is trimmed as well; use that
    /// only while nobody is looking at the window.
    /// </summary>
    public void Schedule(bool workingSet = false)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _trimWorkingSet |= workingSet;
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drops a pending working-set trim (the window came back); a pending heap compaction still runs.</summary>
    public void WindowInUse()
    {
        lock (_gate)
        {
            _trimWorkingSet = false;
        }
    }

    private void Run()
    {
        bool workingSet;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            workingSet = _trimWorkingSet;
            _trimWorkingSet = false;
        }
        try
        {
            using var start = Process.GetCurrentProcess();
            var before = start.PrivateMemorySize64;
            // Nothing worth collecting if the app barely allocated since the last one (for example the window just
            // lost focus again): only the working-set trim is useful then.
            if (GC.GetTotalAllocatedBytes() - Interlocked.Read(ref _allocatedAtLastCollect) > MinAllocatedToCollect)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                Interlocked.Exchange(ref _allocatedAtLastCollect, GC.GetTotalAllocatedBytes());
            }
            if (workingSet)
            {
                using var self = Process.GetCurrentProcess();
                _ = EmptyWorkingSet(self.Handle);
            }
            using var after = Process.GetCurrentProcess();
            _log.LogInformation("Trimmed memory: private {Before} MB -> {After} MB{WorkingSet}", before >> 20, after.PrivateMemorySize64 >> 20, workingSet ? " (working set trimmed)" : string.Empty);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Memory trim skipped");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        _timer.Dispose();
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
