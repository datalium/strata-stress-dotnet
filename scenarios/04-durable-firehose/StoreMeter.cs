using System.Diagnostics.Metrics;
using Datalium.Strata.Storage;

namespace Datalium.Strata.Stress.Firehose;

/// <summary>
/// What the store says about itself, through its own telemetry (<see cref="StorageTelemetry"/>): checkpoints it
/// committed, group commit's flushes and how long saves waited for them, and calls repeated after a transient
/// failure. Not the scenario's figures, which count the store's rows: these explain them. Cheap enough to keep on (a
/// few atomic additions a save), and the same whether group commit is on or off.
/// </summary>
internal sealed class StoreMeter : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _saved;
    private long _flushes;
    private long _flushedSaves;
    private long _flushMax;
    private long _waitCount;
    private long _waitMicroseconds;
    private long _waitMaxMicroseconds;
    private long _saveRetries;
    private long _otherRetries;

    public StoreMeter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == StorageTelemetry.Name
                && instrument.Name is "strata.store.checkpoint.size" or "strata.store.flush.size" or "strata.store.flush.wait" or "strata.store.retries")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "strata.store.checkpoint.size")
            {
                Interlocked.Increment(ref _saved);
            }
            else if (instrument.Name == "strata.store.retries")
            {
                var save = false;
                foreach (var tag in tags)
                {
                    save |= tag.Key == "strata.store.operation" && tag.Value is "save";
                }

                Interlocked.Add(ref save ? ref _saveRetries : ref _otherRetries, value);
            }
        });
        _listener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
        {
            Interlocked.Increment(ref _flushes);
            Interlocked.Add(ref _flushedSaves, value);
            Max(ref _flushMax, value);
        });
        _listener.SetMeasurementEventCallback<double>((instrument, seconds, _, _) =>
        {
            var micros = (long)(seconds * 1_000_000);
            Interlocked.Increment(ref _waitCount);
            Interlocked.Add(ref _waitMicroseconds, micros);
            Max(ref _waitMaxMicroseconds, micros);
        });
        _listener.Start();
    }

    /// <summary>The counters now. The maxima are since the last <see cref="ResetMaxima"/>.</summary>
    public Snapshot Read() => new(
        Interlocked.Read(ref _saved),
        Interlocked.Read(ref _flushes),
        Interlocked.Read(ref _flushedSaves),
        Interlocked.Read(ref _waitCount),
        Interlocked.Read(ref _waitMicroseconds),
        Interlocked.Read(ref _saveRetries),
        Interlocked.Read(ref _otherRetries),
        Interlocked.Read(ref _flushMax),
        Interlocked.Read(ref _waitMaxMicroseconds));

    public void ResetMaxima()
    {
        Interlocked.Exchange(ref _flushMax, 0);
        Interlocked.Exchange(ref _waitMaxMicroseconds, 0);
    }

    public void Dispose() => _listener.Dispose();

    private static void Max(ref long field, long value)
    {
        var seen = Interlocked.Read(ref field);
        while (value > seen)
        {
            var was = Interlocked.CompareExchange(ref field, value, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    /// <summary>The store's counters at one moment; <see cref="Minus"/> gives what happened between two.</summary>
    public readonly record struct Snapshot(
        long Saved,
        long Flushes,
        long FlushedSaves,
        long WaitCount,
        long WaitMicroseconds,
        long SaveRetries,
        long OtherRetries,
        long FlushMax,
        long WaitMaxMicroseconds)
    {
        /// <summary>The counters' growth since <paramref name="earlier"/>; the maxima are this snapshot's.</summary>
        public Snapshot Minus(Snapshot earlier) => new(
            Saved - earlier.Saved,
            Flushes - earlier.Flushes,
            FlushedSaves - earlier.FlushedSaves,
            WaitCount - earlier.WaitCount,
            WaitMicroseconds - earlier.WaitMicroseconds,
            SaveRetries - earlier.SaveRetries,
            OtherRetries - earlier.OtherRetries,
            FlushMax,
            WaitMaxMicroseconds);
    }
}
