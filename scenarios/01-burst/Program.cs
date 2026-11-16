// Scenario 1, Burst (SCENARIOS.md, "1. Burst").
//
// Thousands of runs started at the same moment, each fanning out to eight parallel model calls. Every model call
// is a simulated wait of 20-80 ms (the harness's SimulatedModel), so a run's critical path is the model's, and
// what's left of the run's time is the engine's overhead plus the time a ready handler waits for a thread. A
// control row with no latency at all shows the engine's own cost.
//
// The checks are about correctness only: every run completed, and every answer is right. How fast it went is a
// result, not a check.

using System.Diagnostics;
using System.Globalization;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Stress.Burst;
using Datalium.Strata.Stress.Harness;

const string Usage =
    "dotnet run -c Release --project scenarios/01-burst -- [--quick] [--json PATH] [--seed N] " +
    "[--runs N,N,...] [--latency-ms MIN-MAX] [--control-runs N] [--warmup N]";

return await Scenario.RunAsync(Burst.Name, Usage, () => Burst.RunAsync(args));

namespace Datalium.Strata.Stress.Burst
{
    internal static class Burst
    {
        public const string Name = "01-burst";

        public static async Task<int> RunAsync(string[] argv)
        {
            var args = ScenarioArgs.Parse(argv, "runs", "latency-ms", "control-runs", "warmup");
            var runs = args.Longs("runs", [1_000, 10_000, 100_000, 1_000_000], [100]);
            var latency = ParseLatency(args.String("latency-ms", "20-80", "20-80"));
            var controlRuns = args.Long("control-runs", 10_000, 100);
            var warmup = args.Long("warmup", 1_000, 10);
            if (runs.Count == 0 || runs.Any(r => r is < 1 or > int.MaxValue / BurstGraph.Branches))
            {
                throw new UsageException($"--runs takes whole numbers from 1 to {int.MaxValue / BurstGraph.Branches:N0}");
            }

            if (controlRuns is < 0 or > int.MaxValue / BurstGraph.Branches || warmup is < 0 or > int.MaxValue / BurstGraph.Branches)
            {
                throw new UsageException("--control-runs and --warmup take a whole number, 0 or more");
            }

            var report = new Report(Name, args);
            report.Parameters["runs"] = runs;
            report.Parameters["latency_min_ms"] = latency.MinMicroseconds / 1000.0;
            report.Parameters["latency_max_ms"] = latency.MaxMicroseconds / 1000.0;
            report.Parameters["control_runs"] = controlRuns;
            report.Parameters["warmup"] = warmup;
            report.Columns(
                new Column("runs", "Runs"),
                new Column("latency_min_ms", "Model latency", Numeric: false, Text: LatencyText),
                new Column("wall_s", "Wall s"),
                new Column("runs_per_s", "Runs/s"),
                new Column("run_p50_ms", "Run P50 ms"),
                new Column("run_p99_ms", "Run P99 ms"),
                new Column("run_p999_ms", "Run P99.9 ms"),
                new Column("overhead_p50_us", "Overhead P50 µs"),
                new Column("overhead_p99_us", "Overhead P99 µs"),
                new Column("overhead_p999_us", "Overhead P99.9 µs"),
                new Column("timer_late_p99_ms", "Timer late P99 ms"),
                new Column("start_spread_ms", "Start spread ms"),
                Column.Mebibytes("peak_resident_bytes", "Peak resident MiB"));

            // The rows: each --runs value at the scenario's latency, then the control, with no latency.
            var rows = runs.Select(r => (Runs: (int)r, Latency: latency)).ToList();
            if (controlRuns > 0)
            {
                rows.Add(((int)controlRuns, LatencyRange.None));
            }

            var store = new InMemoryCheckpointStore();
            var verifier = new Verifier();
            var engineRows = new List<OrderedDictionary<string, object?>>();
            for (var row = 0; row < rows.Count; row++)
            {
                var (count, rowLatency) = rows[row];
                var model = new SimulatedModel(args.Seed, rowLatency);
                Console.Error.WriteLine($"{Name}: row {row}, {count:N0} runs, model latency {Describe(rowLatency)}");

                // 1. The warm-up, the same way as the row, with its own thread ids. Not counted.
                if (warmup > 0)
                {
                    await new BurstRow(model, $"warmup-{row}-", (int)warmup, store, verifier).RunAsync();
                }

                // 2. Everything the row needs is allocated before the sample, so the row's figures are the runs'.
                var burst = new BurstRow(model, $"burst-{row}-", count, store, verifier);
                MemorySample.Take();
                var gen0 = GC.CollectionCount(0);
                var gen1 = GC.CollectionCount(1);
                var gen2 = GC.CollectionCount(2);
                var allocated = GC.GetTotalAllocatedBytes(precise: true);

                // 3 and 4. Start every run at once, and sample the resident memory while they're in flight.
                long peak;
                using (var sampler = PeakResidentSampler.Start())
                {
                    await burst.RunAsync();
                    peak = sampler.Stop();
                }

                var allocatedPerRun = (double)(GC.GetTotalAllocatedBytes(precise: true) - allocated) / count;
                report.Row(burst.Figures(peak));
                engineRows.Add(new()
                {
                    ["runs"] = (long)count,
                    ["latency_min_ms"] = rowLatency.MinMicroseconds / 1000.0,
                    ["latency_max_ms"] = rowLatency.MaxMicroseconds / 1000.0,
                    ["allocated_bytes_per_run"] = allocatedPerRun,
                    ["gen0_collections"] = (long)(GC.CollectionCount(0) - gen0),
                    ["gen1_collections"] = (long)(GC.CollectionCount(1) - gen1),
                    ["gen2_collections"] = (long)(GC.CollectionCount(2) - gen2),
                });
            }

            // 5. The checks, over every run of every row (the warm-ups' too).
            report.Check("every run completed", verifier.NotCompleted == 0, verifier.NotCompletedDetail);
            report.Check("every answer is right", verifier.Wrong == 0, verifier.WrongDetail);
            report.EngineSpecific["rows"] = engineRows;
            return await report.FinishAsync();
        }

        /// <summary><c>20-80</c>, or <c>50</c> for a fixed latency, in milliseconds.</summary>
        private static LatencyRange ParseLatency(string text)
        {
            var parts = text.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length is 1 or 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
                && long.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var max)
                && max >= min)
            {
                return LatencyRange.Milliseconds(min, max);
            }

            throw new UsageException($"--latency-ms takes MIN-MAX in whole milliseconds, e.g. 20-80, not '{text}'");
        }

        private static string Describe(LatencyRange latency) => latency == LatencyRange.None ? "none" : latency.ToString();

        private static string LatencyText(OrderedDictionary<string, object?> row) =>
            row["latency_max_ms"] is 0.0
                ? "none"
                : string.Create(CultureInfo.InvariantCulture, $"{row["latency_min_ms"]}–{row["latency_max_ms"]} ms");
    }

    /// <summary>One row's runs: started all at once, then awaited, each one's times recorded.</summary>
    internal sealed class BurstRow
    {
        private readonly SimulatedModel _model;
        private readonly string _prefix;
        private readonly ICheckpointStore _store;
        private readonly Verifier _verifier;
        private readonly Timings _timings;
        private readonly CompiledGraph<BurstState> _graph;
        private readonly Task[] _tasks;

        public BurstRow(SimulatedModel model, string prefix, int runs, ICheckpointStore store, Verifier verifier)
        {
            _model = model;
            _prefix = prefix;
            _store = store;
            _verifier = verifier;
            _timings = new Timings(runs);
            _graph = BurstGraph.Build(model, _timings);
            _tasks = new Task[runs];
        }

        /// <summary>
        /// Hands run 0, 1, ... to the thread pool, each as one work item that calls <c>RunAsync</c>, then waits for all
        /// of them. A run's start is when it was handed over, so its time includes its wait for the pool.
        /// </summary>
        public async Task RunAsync()
        {
            for (var run = 0; run < _tasks.Length; run++)
            {
                var r = run;
                _timings.Started[r] = Stopwatch.GetTimestamp();
                _tasks[r] = Task.Run(() => RunOneAsync(r));
            }

            await Task.WhenAll(_tasks);
        }

        private async Task RunOneAsync(int run)
        {
            var threadId = _prefix + run.ToString(CultureInfo.InvariantCulture);
            var initial = BurstState.Start((ulong)run);
            GraphExecutionResult<BurstState>? result = null;
            Exception? error = null;
            try
            {
                result = await _graph.RunAsync(threadId, initial, _store);
            }
            catch (Exception e)
            {
                error = e;
            }

            _timings.Finished[run] = Stopwatch.GetTimestamp();
            _verifier.Verify(threadId, (ulong)run, result, error);
        }

        /// <summary>The row's figures, keyed as SCENARIOS.md names them.</summary>
        public OrderedDictionary<string, object?> Figures(long peakResidentBytes)
        {
            var t = _timings;
            var runs = t.Runs;
            var firstStart = t.Started.Min();
            var lastStart = t.Started.Max();
            var lastFinish = t.Finished.Max();
            var wall = Ticks.ToSeconds(lastFinish - firstStart);

            var runTimes = new long[runs];
            var overhead = new double[runs];
            for (var r = 0; r < runs; r++)
            {
                runTimes[r] = t.Finished[r] - t.Started[r];
                var slowestBranch = 0L;
                for (var b = 0; b < BurstGraph.Branches; b++)
                {
                    slowestBranch = Math.Max(slowestBranch, t.Branch[r * BurstGraph.Branches + b]);
                }

                overhead[r] = Ticks.ToMicroseconds(runTimes[r] - (t.Plan[r] + slowestBranch + t.Answer[r]));
            }

            var run = Distribution.OfTicks(runTimes);
            var over = Distribution.Of(overhead);
            var latency = _model.Latency;
            var modelCalls = (long)runs * BurstGraph.CallsPerRun;
            var figures = new OrderedDictionary<string, object?>
            {
                ["runs"] = (long)runs,
                ["latency_min_ms"] = latency.MinMicroseconds / 1000.0,
                ["latency_max_ms"] = latency.MaxMicroseconds / 1000.0,
                ["model_calls"] = modelCalls,
                ["wall_s"] = wall,
                ["start_spread_ms"] = Ticks.ToMilliseconds(lastStart - firstStart),
                ["runs_per_s"] = runs / wall,
                ["model_calls_per_s"] = modelCalls / wall,
                ["run_p50_ms"] = run.P50,
                ["run_p90_ms"] = run.P90,
                ["run_p99_ms"] = run.P99,
                ["run_p999_ms"] = run.P999,
                ["run_max_ms"] = run.Max,
                ["overhead_p50_us"] = over.P50,
                ["overhead_p99_us"] = over.P99,
                ["overhead_p999_us"] = over.P999,
                ["overhead_max_us"] = over.Max,
            };

            if (latency != LatencyRange.None)
            {
                var late = TimerLate();
                figures["timer_late_p50_ms"] = late.P50;
                figures["timer_late_p99_ms"] = late.P99;
            }

            figures["peak_resident_bytes"] = peakResidentBytes;
            return figures;
        }

        /// <summary>Each call's measured wait minus the latency it asked for, in milliseconds.</summary>
        private Distribution TimerLate()
        {
            var t = _timings;
            var late = new double[(long)t.Runs * BurstGraph.CallsPerRun];
            var i = 0;
            for (var r = 0; r < t.Runs; r++)
            {
                var run = (ulong)r;
                late[i++] = Late(t.Plan[r], run, 0);
                for (var b = 0; b < BurstGraph.Branches; b++)
                {
                    late[i++] = Late(t.Branch[r * BurstGraph.Branches + b], run, (ulong)(1 + b));
                }

                late[i++] = Late(t.Answer[r], run, BurstGraph.Branches + 1);
            }

            return Distribution.Of(late);

            double Late(long measured, ulong run, ulong call) =>
                Ticks.ToMilliseconds(measured) - _model.LatencyMicroseconds(run, call) / 1000.0;
        }
    }

    /// <summary>Checks every run's result as it returns, and keeps the first of each kind of failure to show.</summary>
    internal sealed class Verifier
    {
        private int _notCompleted;
        private int _wrong;
        private string? _firstNotCompleted;
        private string? _firstWrong;

        public int NotCompleted => Volatile.Read(ref _notCompleted);

        public int Wrong => Volatile.Read(ref _wrong);

        public string? NotCompletedDetail => NotCompleted == 0 ? null : $"{NotCompleted:N0} didn't; the first: {_firstNotCompleted}";

        public string? WrongDetail => Wrong == 0 ? null : $"{Wrong:N0} wrong; the first: {_firstWrong}";

        public void Verify(string threadId, ulong run, GraphExecutionResult<BurstState>? result, Exception? error)
        {
            if (result is not GraphExecutionResult<BurstState>.Completed { State: var state })
            {
                if (Interlocked.Increment(ref _notCompleted) == 1)
                {
                    _firstNotCompleted = $"{threadId} {(error is null ? $"returned {result}" : $"threw {error.GetType().Name}: {error.Message}")}";
                }

                return;
            }

            if (!IsRight(state, run) && Interlocked.Increment(ref _wrong) == 1)
            {
                _firstWrong = $"{threadId} has findings [{string.Join(", ", state.Findings)}] and answer '{state.Answer}'";
            }
        }

        /// <summary>Findings <c>f0-{run}</c> to <c>f7-{run}</c>, in that order, and the answer <c>answer-{run}-8</c>.</summary>
        private static bool IsRight(BurstState state, ulong run)
        {
            if (state.Findings.Count != BurstGraph.Branches || state.Answer != BurstGraph.ExpectedAnswer(run, BurstGraph.Branches))
            {
                return false;
            }

            for (var i = 0; i < BurstGraph.Branches; i++)
            {
                if (state.Findings[i] != BurstGraph.Finding(i, run))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
