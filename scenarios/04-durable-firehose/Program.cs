// Scenario 4, Durable firehose (SCENARIOS.md, "4. Durable firehose").
//
// Many durable runs at once, each a ten-node line checkpointed after every node (EveryNode), one run after another
// in a closed loop, write their checkpoints to Postgres: with group commit off, then on, at each concurrency. The
// figures are the store's rows a second, counted in the table over a window, and what a run's checkpoints cost it.
//
// The checks are about correctness only: every run completed, every thread has its eleven checkpoints and nothing
// else is in the store. How many checkpoints a second the store took is the result, never a check.

using System.Diagnostics;
using System.Globalization;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Storage.Postgres;
using Datalium.Strata.Stress.Firehose;
using Datalium.Strata.Stress.Harness;

const string Usage =
    "dotnet run -c Release --project scenarios/04-durable-firehose -- [--quick] [--json PATH] [--seed N] " +
    "[--concurrency N,...] [--group-commit off,on] [--repeats N] [--warmup-s N] [--measure-s N]";

return await Scenario.RunAsync(Firehose.Name, Usage, () => Firehose.RunAsync(args));

namespace Datalium.Strata.Stress.Firehose
{
    internal static class Firehose
    {
        public const string Name = "04-durable-firehose";

        /// <summary>How many threads' logs the checks read at once.</summary>
        private const int CheckInFlight = 64;

        public static async Task<int> RunAsync(string[] argv)
        {
            var args = ScenarioArgs.Parse(argv, "concurrency", "group-commit", "repeats", "warmup-s", "measure-s");
            if (args.Positional.Count > 0)
            {
                throw new UsageException($"unknown mode '{string.Join(' ', args.Positional)}': this scenario has none");
            }

            var settings = Settings.From(args);
            var report = new Report(Name, args);
            await using var postgres = await StressPostgres.ConnectAsync();
            Describe(report.Parameters, settings, postgres);
            report.Columns(
                new Column("concurrency", "Runs at once"),
                new Column("group_commit", "Group commit", Numeric: false),
                new Column("checkpoints_per_s_median", "Checkpoints/s (min–max)", Text: row =>
                    $"{Report.Cell(row["checkpoints_per_s_median"])} ({Report.Cell(row["checkpoints_per_s_min"])}–{Report.Cell(row["checkpoints_per_s_max"])})"),
                new Column("runs_per_s_median", "Runs/s"),
                new Column("run_p50_ms", "Run P50 ms"),
                new Column("run_p99_ms", "Run P99 ms"),
                new Column("checkpoint_cost_p50_ms", "Checkpoints' cost P50 ms"),
                new Column("checkpoint_cost_p99_ms", "Checkpoints' cost P99 ms"),
                new Column("timer_late_p50_ms", "Timer late P50 ms"),
                new Column("timer_late_p99_ms", "Timer late P99 ms"));

            var configurations = settings.Configurations();
            var measured = configurations.Select(_ => new List<Measured>()).ToArray();
            using var meter = new StoreMeter();
            Console.Error.WriteLine(
                $"{Name}: {configurations.Count} configurations × {settings.Repeats} repeat(s), {settings.WarmupSeconds} s warm-up and {settings.MeasureSeconds} s measured each");

            // Every configuration once, then every one again, so a drift of the machine spreads across them.
            for (var repeat = 0; repeat < settings.Repeats; repeat++)
            {
                foreach (var configuration in configurations)
                {
                    var one = await MeasureAsync(postgres, meter, settings, configuration, repeat);
                    measured[configuration.Index].Add(one);
                    Console.Error.WriteLine(
                        $"{Name}: repeat {repeat}, {configuration}: {one.CheckpointsPerSecond:N0} checkpoints/s, {one.RunsPerSecond:N0} runs/s, " +
                        $"{one.RunsStarted:N0} runs; counts took {one.CountStartMs:N0} and {one.CountEndMs:N0} ms; checked in {one.CheckSeconds:N1} s");
                }
            }

            var byRepeat = new List<OrderedDictionary<string, object?>>();
            foreach (var configuration in configurations)
            {
                var repeats = measured[configuration.Index];
                var perSecond = Distribution.Of(repeats.Select(m => m.CheckpointsPerSecond).ToArray());
                var runs = Distribution.Of(repeats.SelectMany(m => m.RunMs).ToArray());
                var cost = Distribution.Of(repeats.SelectMany(m => m.CostMs).ToArray());
                var late = Distribution.Of(repeats.SelectMany(m => m.LateMs).ToArray());
                report.Row(new()
                {
                    ["concurrency"] = (long)configuration.Concurrency,
                    ["group_commit"] = configuration.GroupCommitName,
                    ["repeats"] = (long)repeats.Count,
                    ["checkpoints_per_s_min"] = perSecond.Count == 0 ? double.NaN : repeats.Min(m => m.CheckpointsPerSecond),
                    ["checkpoints_per_s_median"] = perSecond.P50,
                    ["checkpoints_per_s_max"] = perSecond.Max,
                    ["runs_per_s_median"] = Distribution.Of(repeats.Select(m => m.RunsPerSecond).ToArray()).P50,
                    ["run_p50_ms"] = runs.P50,
                    ["run_p99_ms"] = runs.P99,
                    ["checkpoint_cost_p50_ms"] = cost.P50,
                    ["checkpoint_cost_p99_ms"] = cost.P99,
                    ["timer_late_p50_ms"] = late.P50,
                    ["timer_late_p99_ms"] = late.P99,
                });
                byRepeat.Add(new()
                {
                    ["concurrency"] = (long)configuration.Concurrency,
                    ["group_commit"] = configuration.GroupCommitName,
                    ["checkpoints_per_s"] = repeats.Select(m => m.CheckpointsPerSecond).ToList(),
                });
            }

            report.Parameters["checkpoints_per_s_by_repeat"] = byRepeat;

            // What explains the figures: the store's own telemetry, each count's cost, the checks' cost, per run.
            report.EngineSpecific["by_repeat"] = configurations
                .SelectMany(c => measured[c.Index].Select(m => m.Describe(c)))
                .OrderBy(d => (long)d["repeat"]!)
                .ThenBy(d => (long)d["config"]!)
                .ToList();

            // The checks, over every configuration and repeat.
            var all = measured.SelectMany(m => m).ToList();
            var notCompleted = all.Sum(m => m.NotCompleted);
            report.Check(
                "every run completed",
                notCompleted == 0,
                notCompleted == 0 ? null : $"{notCompleted:N0} runs didn't complete; the first: {all.First(m => m.NotCompleted > 0).FirstNotCompleted}");
            var badThreads = all.Sum(m => m.BadThreads);
            report.Check(
                "every completed thread has exactly eleven checkpoints, the last completed with step 10",
                badThreads == 0,
                badThreads == 0 ? null : $"{badThreads:N0} threads; the first: {all.First(m => m.BadThreads > 0).FirstBadThread}");
            var extra = all.Where(m => m.RowsAtEnd != (long)FirehoseGraph.CheckpointsPerRun * m.RunsStarted).ToList();
            report.Check(
                "the store holds no other checkpoint: eleven rows for each run started",
                extra.Count == 0,
                extra.Count == 0
                    ? null
                    : $"{extra.Count} configuration(s) and repeat(s); the first: {extra[0].RowsAtEnd:N0} rows for {extra[0].RunsStarted:N0} runs");
            return await report.FinishAsync();
        }

        /// <summary>
        /// The <c>parameters</c> SCENARIOS.md lists for this scenario. <c>group_commit</c> is the list of the
        /// configurations' settings here, not <see cref="StressPostgres.Describe"/>'s single one.
        /// </summary>
        private static void Describe(OrderedDictionary<string, object?> parameters, Settings settings, StressPostgres postgres)
        {
            parameters["concurrency"] = settings.Concurrency.Select(c => (long)c).ToList();
            parameters["group_commit"] = settings.GroupCommit.ToList();
            parameters["repeats"] = (long)settings.Repeats;
            parameters["warmup_s"] = (double)settings.WarmupSeconds;
            parameters["measure_s"] = (double)settings.MeasureSeconds;
            parameters["latency_min_ms"] = Settings.Latency.MinMicroseconds / 1000.0;
            parameters["latency_max_ms"] = Settings.Latency.MaxMicroseconds / 1000.0;
            parameters["postgres_version"] = postgres.ServerVersion;
            parameters["pool_size"] = (long)postgres.PoolSize;
            if (settings.GroupCommit.Contains("on"))
            {
                parameters["group_commit_max_batch"] = (long)Configuration.GroupCommitOn.MaxBatch;
                parameters["group_commit_max_flushes"] = (long)Configuration.GroupCommitOn.MaxConcurrentFlushes;
            }
        }

        /// <summary>One configuration, one repeat: SCENARIOS.md's procedure, steps 1 to 5.</summary>
        private static async Task<Measured> MeasureAsync(
            StressPostgres postgres, StoreMeter meter, Settings settings, Configuration configuration, int repeat)
        {
            // 1. Empty the store's tables. Open a store with the configuration's group commit.
            await postgres.DropCheckpointStoreAsync();
            await using var store = await postgres.OpenCheckpointStoreAsync(configuration.GroupCommit);

            // 2. The closed loop: each worker runs one run after another, a new thread each time, until told to stop.
            var concurrency = configuration.Concurrency;
            var waits = new Waits(concurrency);
            var graph = FirehoseGraph.Build(settings.Model, waits);
            var logs = Enumerable.Range(0, concurrency).Select(_ => new WorkerLog()).ToArray();
            using var stop = new CancellationTokenSource();
            var workers = Enumerable.Range(0, concurrency)
                .Select(worker => Task.Run(() => LoopAsync(graph, store, settings, configuration, repeat, worker, waits, logs[worker], stop.Token)))
                .ToArray();

            // 3. The window: from the count after the warm-up to the count --measure-s later.
            var loopStarted = Stopwatch.GetTimestamp();
            await DelayUntilAsync(loopStarted, TimeSpan.FromSeconds(settings.WarmupSeconds));
            meter.ResetMaxima();
            var meterAtStart = meter.Read();
            var collectionsAtStart = Collections();
            var cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
            var (windowStart, rowsAtStart, countStartMs) = await CountRowsAsync(postgres);
            await DelayUntilAsync(windowStart, TimeSpan.FromSeconds(settings.MeasureSeconds));
            var (windowEnd, rowsAtEnd, countEndMs) = await CountRowsAsync(postgres);
            var meterInWindow = meter.Read().Minus(meterAtStart);
            var collections = Collections().Zip(collectionsAtStart, (end, start) => end - start).ToArray();
            var cpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuAtStart).TotalSeconds;

            // Stop the workers and let their runs finish.
            await stop.CancelAsync();
            await Task.WhenAll(workers);

            // 4. Which runs count: toward the latency figures if they started and finished inside the window, toward
            // the runs a second if they finished inside it.
            var windowSeconds = Ticks.ToSeconds(windowEnd - windowStart);
            var runMs = new List<double>();
            var costMs = new List<double>();
            var lateMs = new List<double>();
            long finishedInWindow = 0;
            foreach (var log in logs)
            {
                for (var i = 0; i < log.Runs.Count; i++)
                {
                    var run = log.Runs[i];
                    if (run.Finished >= windowStart && run.Finished <= windowEnd)
                    {
                        finishedInWindow++;
                    }

                    if (run.Started >= windowStart && run.Finished <= windowEnd && run.Completed)
                    {
                        runMs.Add(Ticks.ToMilliseconds(run.Finished - run.Started));
                        costMs.Add(Ticks.ToMilliseconds(run.Finished - run.Started - run.Waited));
                        for (var call = 0; call < FirehoseGraph.Nodes; call++)
                        {
                            lateMs.Add(log.LateMicroseconds[(i * FirehoseGraph.Nodes) + call] / 1000.0);
                        }
                    }
                }
            }

            // 5. Check what the store holds before the next configuration empties it.
            var checkStarted = Stopwatch.GetTimestamp();
            var threads = logs.SelectMany((log, worker) => Enumerable.Range(0, log.Runs.Count)
                    .Where(n => log.Runs[n].Completed)
                    .Select(n => FirehoseGraph.ThreadId(configuration.Index, repeat, worker, n)))
                .ToArray();
            var bad = new string?[threads.Length];
            await Concurrently.ForEachAsync(0, threads.Length, CheckInFlight, async i => bad[i] = await CheckThreadAsync(store, threads[i]));
            var (_, rowsAfter, _) = await CountRowsAsync(postgres);
            var storeBytes = await postgres.CheckpointStoreBytesAsync();
            var checkSeconds = Ticks.ToSeconds(Stopwatch.GetTimestamp() - checkStarted);

            var notCompleted = logs.Sum(log => log.Runs.Count(r => !r.Completed));
            return new Measured(
                repeat,
                windowSeconds,
                (rowsAtEnd - rowsAtStart) / windowSeconds,
                finishedInWindow / windowSeconds,
                logs.Sum(log => (long)log.Runs.Count),
                finishedInWindow,
                [.. runMs],
                [.. costMs],
                [.. lateMs],
                countStartMs,
                countEndMs,
                meterInWindow,
                collections,
                cpuSeconds,
                notCompleted,
                logs.Select(log => log.FirstFailure).FirstOrDefault(f => f is not null),
                bad.Count(b => b is not null),
                bad.FirstOrDefault(b => b is not null),
                rowsAfter,
                storeBytes,
                checkSeconds);
        }

        /// <summary>One worker's closed loop: run after run, each on a new thread, until <paramref name="stop"/>.</summary>
        private static async Task LoopAsync(
            CompiledGraph<FirehoseState> graph,
            PostgresCheckpointStore store,
            Settings settings,
            Configuration configuration,
            int repeat,
            int worker,
            Waits waits,
            WorkerLog log,
            CancellationToken stop)
        {
            for (long n = 0; !stop.IsCancellationRequested; n++)
            {
                var run = FirehoseGraph.RunNumber(configuration.Concurrency, worker, n);
                var threadId = FirehoseGraph.ThreadId(configuration.Index, repeat, worker, n);
                var initial = FirehoseState.Start(settings.Seed, run);
                string? failure = null;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    // Not cancelled by stop: a run that has started finishes.
                    var result = await graph.RunAsync(threadId, initial, store);
                    if (result is not GraphExecutionResult<FirehoseState>.Completed { State: { Step: FirehoseGraph.Nodes } state } || state.Run != run)
                    {
                        failure = $"{threadId}: {result}";
                    }
                }
                catch (Exception e)
                {
                    failure = $"{threadId}: {e.GetType().Name}: {e.Message}";
                }

                var finished = Stopwatch.GetTimestamp();
                var waited = 0L;
                var measured = waits.Of(worker);
                for (var call = 0; call < FirehoseGraph.Nodes; call++)
                {
                    waited += measured[call];
                    log.LateMicroseconds.Add((int)((long)Ticks.ToMicroseconds(measured[call]) - settings.Model.LatencyMicroseconds(run, (ulong)call)));
                }

                log.Runs.Add(new RunRecord(started, finished, waited, failure is null));
                log.FirstFailure ??= failure;
            }
        }

        /// <summary>A thread's log, read through the store: why it isn't eleven checkpoints ending <c>completed</c> at step 10, or null.</summary>
        private static async Task<string?> CheckThreadAsync(PostgresCheckpointStore store, string threadId)
        {
            var page = await store.GetLogAsync(threadId, afterSequence: null, ThreadQuery.MaxLimit);
            var entries = page.Entries;
            if (entries.Count != FirehoseGraph.CheckpointsPerRun)
            {
                return $"{threadId} has {entries.Count} checkpoints";
            }

            var last = entries[^1].Checkpoint;
            var step = last.FrozenState?["step"]?.GetValue<long>();
            return last.Status == CheckpointStatus.Completed && step == FirehoseGraph.Nodes
                ? null
                : $"{threadId} ends {last.Status} at step {step?.ToString(CultureInfo.InvariantCulture) ?? "none"}";
        }

        /// <summary>The store's checkpoint rows, the time just before the count was sent, and how long it took.</summary>
        private static async Task<(long At, long Rows, double Ms)> CountRowsAsync(StressPostgres postgres)
        {
            await using var command = postgres.DataSource.CreateCommand("SELECT count(*) FROM checkpoints");
            var at = Stopwatch.GetTimestamp();
            var rows = (long)(await command.ExecuteScalarAsync() ?? 0L);
            return (at, rows, Ticks.ToMilliseconds(Stopwatch.GetTimestamp() - at));
        }

        private static async Task DelayUntilAsync(long from, TimeSpan after)
        {
            var left = after - Stopwatch.GetElapsedTime(from);
            if (left > TimeSpan.Zero)
            {
                await Task.Delay(left);
            }
        }

        private static long[] Collections() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    }

    /// <summary>The run's sizes, from the command line or the spec's table.</summary>
    internal sealed record Settings(
        IReadOnlyList<int> Concurrency,
        IReadOnlyList<string> GroupCommit,
        int Repeats,
        long WarmupSeconds,
        long MeasureSeconds,
        ulong Seed,
        SimulatedModel Model)
    {
        /// <summary>Each model call: 2–10 ms.</summary>
        public static readonly LatencyRange Latency = LatencyRange.Milliseconds(2, 10);

        public static Settings From(ScenarioArgs args)
        {
            var concurrency = args.Longs("concurrency", [16, 64, 256, 1024], [16]);
            if (concurrency.Count == 0 || concurrency.Any(c => c is < 1 or > 100_000) || concurrency.Distinct().Count() != concurrency.Count)
            {
                throw new UsageException("--concurrency takes distinct whole numbers from 1 to 100,000, separated by commas");
            }

            var groupCommit = args.String("group-commit", "off,on", "off,on")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (groupCommit.Length == 0 || groupCommit.Any(g => g is not ("off" or "on")) || groupCommit.Distinct().Count() != groupCommit.Length)
            {
                throw new UsageException("--group-commit takes off, on, or both, separated by a comma");
            }

            var repeats = args.Long("repeats", 3, 1);
            var warmup = args.Long("warmup-s", 5, 0);
            var measure = args.Long("measure-s", 20, 2);
            if (repeats is < 1 or > 1_000)
            {
                throw new UsageException("--repeats takes a whole number from 1 to 1,000");
            }

            if (warmup is < 0 or > 3_600 || measure is < 1 or > 3_600)
            {
                throw new UsageException("--warmup-s takes a whole number of seconds from 0 to 3,600, and --measure-s from 1 to 3,600");
            }

            return new Settings(
                [.. concurrency.Select(c => (int)c)],
                groupCommit,
                (int)repeats,
                warmup,
                measure,
                args.Seed,
                new SimulatedModel(args.Seed, Latency));
        }

        /// <summary>The configurations, in order: each concurrency as given, and for each, group commit as given.</summary>
        public IReadOnlyList<Configuration> Configurations() =>
            [.. Concurrency.SelectMany(c => GroupCommit.Select(g => (c, g))).Select((cg, index) => new Configuration(index, cg.c, cg.g))];
    }

    /// <summary>One concurrency with group commit off or on; <see cref="Index"/> is its place in the order, the thread ids' <c>config</c>.</summary>
    internal sealed record Configuration(int Index, int Concurrency, string GroupCommitName)
    {
        /// <summary>The spec's group commit when on: the store's defaults (256 saves a flush, 4 flushes at once).</summary>
        public static readonly GroupCommitOptions GroupCommitOn = new();

        /// <summary>The store's option: unset when off, the store's default.</summary>
        public GroupCommitOptions? GroupCommit => GroupCommitName == "on" ? GroupCommitOn : null;

        public override string ToString() => $"{Concurrency} at once, group commit {GroupCommitName}";
    }

    /// <summary>One run: when it started and finished, its ten measured waits summed, in <see cref="Stopwatch"/> ticks, and whether it completed as it should.</summary>
    internal readonly record struct RunRecord(long Started, long Finished, long Waited, bool Completed);

    /// <summary>What one worker ran, in order: its runs, and each run's ten calls' lateness in µs.</summary>
    internal sealed class WorkerLog
    {
        public List<RunRecord> Runs { get; } = [];

        public List<int> LateMicroseconds { get; } = [];

        public string? FirstFailure { get; set; }
    }

    /// <summary>One configuration's figures in one repeat, and what its checks found.</summary>
    internal sealed record Measured(
        int Repeat,
        double WindowSeconds,
        double CheckpointsPerSecond,
        double RunsPerSecond,
        long RunsStarted,
        long RunsInWindow,
        double[] RunMs,
        double[] CostMs,
        double[] LateMs,
        double CountStartMs,
        double CountEndMs,
        StoreMeter.Snapshot Store,
        long[] Collections,
        double CpuSeconds,
        long NotCompleted,
        string? FirstNotCompleted,
        long BadThreads,
        string? FirstBadThread,
        long RowsAtEnd,
        long StoreBytes,
        double CheckSeconds)
    {
        /// <summary>This repeat's figures for <c>engine_specific</c>: what explains the row's.</summary>
        public OrderedDictionary<string, object?> Describe(Configuration configuration) => new()
        {
            ["config"] = (long)configuration.Index,
            ["repeat"] = (long)Repeat,
            ["concurrency"] = (long)configuration.Concurrency,
            ["group_commit"] = configuration.GroupCommitName,
            ["window_s"] = WindowSeconds,
            ["checkpoints_per_s"] = CheckpointsPerSecond,
            ["runs_per_s"] = RunsPerSecond,
            ["runs_started"] = RunsStarted,
            ["runs_finished_in_window"] = RunsInWindow,
            ["runs_measured"] = (long)RunMs.Length,
            ["count_start_ms"] = CountStartMs,
            ["count_end_ms"] = CountEndMs,
            ["store_saves_in_window"] = Store.Saved,
            ["store_saves_per_s"] = Store.Saved / WindowSeconds,
            ["flushes_in_window"] = Store.Flushes,
            ["flush_size_mean"] = Store.Flushes == 0 ? null : (double)Store.FlushedSaves / Store.Flushes,
            ["flush_size_max"] = Store.Flushes == 0 ? null : Store.FlushMax,
            ["flush_wait_mean_ms"] = Store.WaitCount == 0 ? null : Store.WaitMicroseconds / 1000.0 / Store.WaitCount,
            ["flush_wait_max_ms"] = Store.WaitCount == 0 ? null : Store.WaitMaxMicroseconds / 1000.0,
            ["save_retries_in_window"] = Store.SaveRetries,
            ["gen0_collections"] = Collections[0],
            ["gen1_collections"] = Collections[1],
            ["gen2_collections"] = Collections[2],
            ["process_cpu_cores"] = CpuSeconds / WindowSeconds,
            ["store_bytes_at_end"] = StoreBytes,
            ["check_s"] = CheckSeconds,
        };
    }
}
