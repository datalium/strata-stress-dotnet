using System.Diagnostics;
using System.Runtime;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// The process's memory at one moment (SCENARIOS.md, "Memory"), taken after a full, blocking, compacting collection
/// so that what is left is what the process still holds. <see cref="ResidentBytes"/> is the figure both engines report;
/// <see cref="ManagedHeapBytes"/> is .NET's own.
/// </summary>
public sealed record MemorySample(long ResidentBytes, long PrivateBytes, long ManagedHeapBytes)
{
    public static MemorySample Take()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var process = Process.GetCurrentProcess();
        return new MemorySample(process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(forceFullCollection: false));
    }

    /// <summary>What the process grew by between <paramref name="before"/> and this sample, per item.</summary>
    public PerItem Since(MemorySample before, long items) => new(
        (double)(ResidentBytes - before.ResidentBytes) / items,
        (double)(PrivateBytes - before.PrivateBytes) / items,
        (double)(ManagedHeapBytes - before.ManagedHeapBytes) / items);

    public sealed record PerItem(double ResidentBytes, double PrivateBytes, double ManagedHeapBytes);
}

/// <summary>
/// The highest resident memory while something runs, sampled every <c>interval</c> without a collection
/// (SCENARIOS.md, scenario 1's procedure). It samples on a thread of its own, so a thread pool busy with the
/// workload doesn't delay it.
/// </summary>
public sealed class PeakResidentSampler : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private long _peak;

    private PeakResidentSampler(TimeSpan interval)
    {
        _interval = interval;
        Sample();
        _thread = new Thread(Loop) { IsBackground = true, Name = "peak resident sampler" };
        _thread.Start();
    }

    public static PeakResidentSampler Start(TimeSpan? interval = null) => new(interval ?? TimeSpan.FromMilliseconds(100));

    /// <summary>Stops sampling, takes a last sample, and returns the highest.</summary>
    public long Stop()
    {
        if (!_stop.IsSet)
        {
            _stop.Set();
            _thread.Join();
            Sample();
        }

        return Interlocked.Read(ref _peak);
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }

    private void Loop()
    {
        while (!_stop.Wait(_interval))
        {
            Sample();
        }
    }

    private void Sample()
    {
        using var process = Process.GetCurrentProcess();
        var resident = process.WorkingSet64;
        if (resident > Interlocked.Read(ref _peak))
        {
            Interlocked.Exchange(ref _peak, resident);
        }
    }
}
