using System.Diagnostics;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// A sample's summary (SCENARIOS.md, "Percentiles"): nearest rank on the whole sorted sample, no histogram and no
/// interpolation, so both engines compute the same figure from the same values.
/// </summary>
public sealed record Distribution(long Count, double Mean, double P50, double P90, double P99, double P999, double Max)
{
    public static readonly Distribution Empty = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Summarises <paramref name="values"/>, which it sorts in place.</summary>
    public static Distribution Of(Span<double> values)
    {
        if (values.IsEmpty)
        {
            return Empty;
        }

        values.Sort();
        double sum = 0;
        foreach (var v in values)
        {
            sum += v;
        }

        return new Distribution(
            values.Length,
            sum / values.Length,
            Percentile(values, 50),
            Percentile(values, 90),
            Percentile(values, 99),
            Percentile(values, 99.9),
            values[^1]);
    }

    /// <summary>Summarises durations in <see cref="Stopwatch"/> ticks, in milliseconds.</summary>
    public static Distribution OfTicks(ReadOnlySpan<long> ticks)
    {
        var ms = new double[ticks.Length];
        for (var i = 0; i < ticks.Length; i++)
        {
            ms[i] = Ticks.ToMilliseconds(ticks[i]);
        }

        return Of(ms);
    }

    /// <summary>The nearest-rank percentile of a sorted sample: the value at rank ⌈p/100 · n⌉ (1-based).</summary>
    public static double Percentile(ReadOnlySpan<double> sorted, double p)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sorted.Length);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(p, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(p, 100);

        // p/100 · n in decimal, so that 99.9% of 1,000 is rank 999 and not 1,000 (0.999 · 1000 is 999.0000000000001 in binary).
        var rank = (long)Math.Ceiling((decimal)p / 100m * sorted.Length);
        return sorted[(int)Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}

/// <summary><see cref="Stopwatch"/> ticks to time.</summary>
public static class Ticks
{
    public static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static double ToMicroseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    public static double ToSeconds(long ticks) => (double)ticks / Stopwatch.Frequency;
}
