using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage.Postgres;
using Datalium.Strata.Stress.Harness;

namespace Datalium.Strata.Stress.Storm;

/// <summary>
/// A worker (SCENARIOS.md, "3. Kill it mid-storm", "Procedure"), in a child process: it recovers what a dead worker
/// left, every 250 ms, and starts every order that has no checkpoint yet, until every order's thread has completed.
/// It reports to the driver on standard output (<see cref="WorkerLine"/>) every 100 ms, so a worker the driver kills
/// has reported all but its last 100 ms.
/// </summary>
internal static class Worker
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<int> RunAsync(Settings settings, int index)
    {
        var owner = $"worker-{index}";
        var log = $"{Storm.Name} ({owner})";

        // 1. The store and the ledger. Every run and every recovery holds a lease under this worker's name.
        await using var postgres = await StressPostgres.ConnectAsync();
        await using var store = await postgres.OpenCheckpointStoreAsync(Storm.GroupCommit);
        var ledger = new Ledger(postgres.DataSource, owner);
        var lateness = new ConcurrentQueue<long>();
        var graph = StormGraph.Build(settings.Model, ledger, lateness.Enqueue);
        var lease = new LeaseOptions(owner, settings.LeaseTtl);
        var done = new CompletedOrders(settings.Orders);
        var recovered = new ConcurrentQueue<(long Order, long UtcTicks)>();
        var passes = new ConcurrentQueue<Pass>();
        var problems = new Problems(log);

        using var finished = new CancellationTokenSource();
        var reporting = ReportAsync(done, recovered, lateness, passes, finished.Token);
        var recovering = RecoverAsync(graph, store, lease, done, recovered, passes, problems, finished.Token);

        // 3. Start every order that has no checkpoint, at most --in-flight at once.
        await Console.Error.WriteLineAsync($"{log}: starting");
        var runOptions = new RunOptions { Lease = lease };
        await Concurrently.ForEachAsync(0, settings.Orders, settings.InFlight, order =>
            StartAsync(graph, store, runOptions, settings.LeaseTtl, order, done, problems));

        // 5. Done when every order's thread has completed: the recovery finishes what the dead workers left.
        await done.All;
        await finished.CancelAsync();
        await recovering;
        await reporting;
        await Console.Error.WriteLineAsync($"{log}: every order completed");
        return 0;
    }

    /// <summary>
    /// Starts one order unless its thread has a checkpoint already: those are done, or the recovery's. A start the
    /// dead worker's live lease refuses is tried again after the TTL, looking for a checkpoint again first.
    /// </summary>
    private static async Task StartAsync(
        CompiledGraph<StormState> graph,
        PostgresCheckpointStore store,
        RunOptions options,
        TimeSpan ttl,
        long order,
        CompletedOrders done,
        Problems problems)
    {
        var threadId = StormGraph.ThreadId(order);
        var held = $"thread '{threadId}' is held by ";
        try
        {
            while (true)
            {
                if (await store.LoadAsync(threadId) is { } existing)
                {
                    if (existing.Status == CheckpointStatus.Completed)
                    {
                        done.Mark(order);
                    }

                    return;
                }

                var result = await graph.RunAsync(threadId, StormState.Start((ulong)order), store, options);
                switch (result)
                {
                    case GraphExecutionResult<StormState>.Completed:
                        done.Mark(order);
                        return;
                    case GraphExecutionResult<StormState>.Failed failed when failed.Message.StartsWith(held, StringComparison.Ordinal):
                        await Task.Delay(ttl);
                        continue;
                    case GraphExecutionResult<StormState>.Cancelled { Reason: CancelReason.LeaseLost }:
                        // Its lease ran out while it ran, and a recovery took the thread: the recovery finishes it.
                        return;
                    default:
                        problems.Note($"{threadId} returned {result}");
                        return;
                }
            }
        }
        catch (Exception e)
        {
            problems.Note($"{threadId} threw {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 2. <c>RecoverStaleAsync</c> under the worker's lease, one pass at a time: each starts 250 ms after the last
    /// one started, or as soon as it returns if it took longer. An observer notes when each recovery completes.
    /// </summary>
    private static async Task RecoverAsync(
        CompiledGraph<StormState> graph,
        PostgresCheckpointStore store,
        LeaseOptions lease,
        CompletedOrders done,
        ConcurrentQueue<(long Order, long UtcTicks)> recovered,
        ConcurrentQueue<Pass> passes,
        Problems problems,
        CancellationToken stop)
    {
        var options = new RecoverOptions
        {
            Run = new RunOptions { Lease = lease, Observer = new RecoveryObserver(done, recovered) },

            // Stops the pass between recoveries once every order is done; the recovery in flight finishes.
            Stop = stop,
        };
        while (!stop.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var report = await graph.RecoverStaleAsync(store, options);
                foreach (var thread in report.Recovered)
                {
                    if (thread.Result is GraphExecutionResult<StormState>.Completed)
                    {
                        done.Mark(StormGraph.Order(thread.ThreadId));
                    }
                }

                passes.Enqueue(new Pass(Ticks.ToMilliseconds(Stopwatch.GetTimestamp() - started), report.Recovered.Count, report.Skipped.Count));
            }
            catch (Exception e)
            {
                problems.Note($"a recovery pass threw {e.GetType().Name}: {e.Message}");
            }

            var wait = Storm.RecoverInterval - Stopwatch.GetElapsedTime(started);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>4. Every 100 ms, what happened since the last report; once more when the worker is done.</summary>
    private static async Task ReportAsync(
        CompletedOrders done,
        ConcurrentQueue<(long Order, long UtcTicks)> recovered,
        ConcurrentQueue<long> lateness,
        ConcurrentQueue<Pass> passes,
        CancellationToken stop)
    {
        using var timer = new PeriodicTimer(ReportInterval);
        var output = Console.Out;
        var lines = new StringBuilder();
        while (true)
        {
            var last = false;
            try
            {
                await timer.WaitForNextTickAsync(stop);
            }
            catch (OperationCanceledException)
            {
                last = true;
            }

            lines.Clear();
            while (recovered.TryDequeue(out var r))
            {
                lines.Append(WorkerLine.Recovered(r.Order, r.UtcTicks)).Append('\n');
            }

            while (passes.TryDequeue(out var pass))
            {
                lines.Append(WorkerLine.PassDone(pass)).Append('\n');
            }

            WorkerLine.AppendLateness(lines, lateness);
            lines.Append(WorkerLine.Progress(done.Count)).Append('\n');
            await output.WriteAsync(lines.ToString());
            await output.FlushAsync(CancellationToken.None);
            if (last)
            {
                return;
            }
        }
    }
}

/// <summary>Which orders this worker has seen completed, each counted once; <see cref="All"/> completes with the last.</summary>
internal sealed class CompletedOrders(long orders)
{
    private readonly int[] _completed = new int[orders];
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public Task All => _all.Task;

    /// <summary>Marks <paramref name="order"/> completed; says whether it wasn't already.</summary>
    public bool Mark(long order)
    {
        if (order < 0 || order >= _completed.Length || Interlocked.Exchange(ref _completed[order], 1) == 1)
        {
            return false;
        }

        if (Interlocked.Increment(ref _count) == _completed.Length)
        {
            _all.TrySetResult();
        }

        return true;
    }
}

/// <summary>Notes the moment each recovered run completes, on the wall clock the driver shares.</summary>
internal sealed class RecoveryObserver(CompletedOrders done, ConcurrentQueue<(long Order, long UtcTicks)> recovered) : IRunObserver
{
    public void OnEvent(RunEvent runEvent)
    {
        if (runEvent is RunCompleted { Outcome: RunOutcome.Completed })
        {
            var now = DateTime.UtcNow.Ticks;
            var order = StormGraph.Order(runEvent.ThreadId);
            if (done.Mark(order))
            {
                recovered.Enqueue((order, now));
            }
        }
    }
}

/// <summary>A run or a recovery that didn't go as the scenario expects: the first few are shown, all are counted.</summary>
internal sealed class Problems(string log)
{
    private const int Shown = 5;
    private long _count;

    public void Note(string problem)
    {
        if (Interlocked.Increment(ref _count) <= Shown)
        {
            Console.Error.WriteLine($"{log}: {problem}");
        }
    }
}

/// <summary>One <c>RecoverStaleAsync</c> pass: how long it took, the threads it recovered and those it skipped.</summary>
internal readonly record struct Pass(double Milliseconds, int Recovered, int Skipped);

/// <summary>
/// What a worker tells the driver, one line each: <c>progress N</c>, <c>recovered ORDER UTC_TICKS</c>,
/// <c>late US,US,...</c> (how late each model call's timer woke) and <c>pass MS RECOVERED SKIPPED</c>.
/// </summary>
internal static class WorkerLine
{
    private const int LatenessPerLine = 1000;

    public static string Progress(long completed) => string.Create(CultureInfo.InvariantCulture, $"progress {completed}");

    public static string Recovered(long order, long utcTicks) => string.Create(CultureInfo.InvariantCulture, $"recovered {order} {utcTicks}");

    public static string PassDone(Pass pass) =>
        string.Create(CultureInfo.InvariantCulture, $"pass {pass.Milliseconds:R} {pass.Recovered} {pass.Skipped}");

    public static void AppendLateness(StringBuilder lines, ConcurrentQueue<long> lateness)
    {
        var inLine = 0;
        while (lateness.TryDequeue(out var micros))
        {
            lines.Append(inLine == 0 ? "late " : ",").Append(micros.ToString(CultureInfo.InvariantCulture));
            if (++inLine == LatenessPerLine)
            {
                lines.Append('\n');
                inLine = 0;
            }
        }

        if (inLine > 0)
        {
            lines.Append('\n');
        }
    }
}
