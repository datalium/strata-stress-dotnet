using System.Diagnostics;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// The stand-in for a model call (SCENARIOS.md, "The simulated model"): it waits a latency drawn from the run's
/// seed and returns how long it really waited. No model, no network: the numbers measure Strata.
/// </summary>
/// <remarks>
/// The latency of a call is a pure function of (seed, run, call), so the Rust twin draws the same latencies in the
/// same places. A timer wakes late, by up to a scheduler tick, so a scenario charges a handler with what it measured,
/// not with what it asked for.
/// </remarks>
public sealed class SimulatedModel(ulong seed, LatencyRange latency)
{
    public ulong Seed => seed;

    public LatencyRange Latency => latency;

    /// <summary>The latency, in whole microseconds, of call <paramref name="call"/> of run <paramref name="run"/>.</summary>
    public long LatencyMicroseconds(ulong run, ulong call) => latency.Draw(seed, run, call);

    /// <summary>
    /// Waits the call's latency and returns the time it really took, in <see cref="Stopwatch"/> ticks. A zero
    /// latency returns at once, without a timer.
    /// </summary>
    public async ValueTask<long> CallAsync(ulong run, ulong call, CancellationToken cancellationToken = default)
    {
        var micros = LatencyMicroseconds(run, call);
        var start = Stopwatch.GetTimestamp();
        if (micros > 0)
        {
            await Task.Delay(TimeSpan.FromMicroseconds(micros), cancellationToken).ConfigureAwait(false);
        }

        return Stopwatch.GetTimestamp() - start;
    }
}

/// <summary>A uniform latency between <see cref="MinMicroseconds"/> and <see cref="MaxMicroseconds"/>, both included.</summary>
public readonly record struct LatencyRange(long MinMicroseconds, long MaxMicroseconds)
{
    public static readonly LatencyRange None = new(0, 0);

    public static LatencyRange Milliseconds(long min, long max) => new(min * 1000, max * 1000);

    public long Draw(ulong seed, ulong run, ulong call)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MinMicroseconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMicroseconds, MinMicroseconds);
        if (MaxMicroseconds == MinMicroseconds)
        {
            return MinMicroseconds;
        }

        var span = (ulong)(MaxMicroseconds - MinMicroseconds) + 1;
        return MinMicroseconds + (long)(SplitMix64.Hash(seed, run, call) % span);
    }

    public override string ToString() =>
        MinMicroseconds == MaxMicroseconds ? $"{MinMicroseconds / 1000.0:0.###} ms" : $"{MinMicroseconds / 1000.0:0.###}–{MaxMicroseconds / 1000.0:0.###} ms";
}

/// <summary>
/// SplitMix64's output function (Steele, Lea and Flood, 2014), which both engines implement bit for bit. SCENARIOS.md
/// gives test vectors.
/// </summary>
public static class SplitMix64
{
    public static ulong Mix(ulong x)
    {
        unchecked
        {
            var z = x + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary><c>mix(mix(mix(seed) ^ run) ^ call)</c>.</summary>
    public static ulong Hash(ulong seed, ulong run, ulong call) => Mix(Mix(Mix(seed) ^ run) ^ call);
}
