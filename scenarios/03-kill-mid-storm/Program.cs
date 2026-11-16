// Scenario 3, Kill it mid-storm (SCENARIOS.md, "3. Kill it mid-storm").
//
// Hundreds of orders are in flight in a worker process, each Validate -> Charge -> Ship, checkpointed after every
// node (EveryNode) under a lease. The driver, this process, kills the worker outright, lists the threads it left
// `running`, and starts the next worker at once. The new worker recovers them once the dead one's leases run out,
// and Charge runs again where it may have run already, with the same idempotency key, so the ledger charges once.
//
// The checks are about correctness only: no order lost, none charged twice, every re-run under the original's key,
// every final charge id the ledger's, every kill landed mid-storm, and no lease left held. How long the recovery
// took is the result, never a check.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Storage.Postgres;
using Datalium.Strata.Stress.Harness;
using Datalium.Strata.Stress.Storm;

const string Usage =
    "dotnet run -c Release --project scenarios/03-kill-mid-storm -- [--quick] [--json PATH] [--seed N] " +
    "[--orders N] [--kills N] [--in-flight N] [--lease-ttl-ms N]";

return await Scenario.RunAsync(Storm.Name, Usage, () => Storm.RunAsync(args));

namespace Datalium.Strata.Stress.Storm
{
    internal static class Storm
    {
        public const string Name = "03-kill-mid-storm";

        /// <summary>The spec's store: group commit off (<c>GroupCommit</c> unset, the store's default).</summary>
        public static readonly GroupCommitOptions? GroupCommit = null;

        /// <summary>How often a worker starts a recovery pass.</summary>
        public static readonly TimeSpan RecoverInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>How often the driver looks at the worker's progress and the orphans it is waiting for.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

        /// <summary>A worker that completes nothing for this long is stuck: the driver kills it and checks what there is.</summary>
        private static readonly TimeSpan Stall = TimeSpan.FromMinutes(2);

        public static async Task<int> RunAsync(string[] argv)
        {
            var args = ScenarioArgs.Parse(argv, "orders", "kills", "in-flight", "lease-ttl-ms");
            var settings = Settings.From(args);
            return args.Positional switch
            {
                [] => await DriveAsync(args, settings),
                ["worker", var k] when int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var index) =>
                    await Worker.RunAsync(settings, index),
                _ => throw new UsageException($"unknown mode '{string.Join(' ', args.Positional)}': only the scenario starts a mode"),
            };
        }

        /// <summary>The driver: start worker 0, kill each worker in turn mid-storm, start the next, then check everything.</summary>
        private static async Task<int> DriveAsync(ScenarioArgs args, Settings settings)
        {
            var report = new Report(Name, args);
            report.Parameters["orders"] = settings.Orders;
            report.Parameters["kills"] = settings.Kills;
            report.Parameters["in_flight"] = (long)settings.InFlight;
            report.Parameters["lease_ttl_ms"] = settings.LeaseTtlMs;
            report.Parameters["recover_interval_ms"] = (long)RecoverInterval.TotalMilliseconds;
            report.Parameters["latency_min_ms"] = Settings.Latency.MinMicroseconds / 1000.0;
            report.Parameters["latency_max_ms"] = Settings.Latency.MaxMicroseconds / 1000.0;
            report.Columns(
                new Column("kill", "Kill"),
                new Column("completed_at_kill", "Completed at kill"),
                new Column("orphans", "Orphans"),
                new Column("first_recovered_ms", "First recovered ms"),
                new Column("all_recovered_ms", "All recovered ms"));

            await using var postgres = await StressPostgres.ConnectAsync();
            postgres.Describe(report.Parameters, GroupCommit);

            // 1. Empty the store and the two tables, and create them, before any worker starts.
            await postgres.DropCheckpointStoreAsync();
            await Ledger.ResetAsync(postgres.DataSource);
            await using var store = await postgres.OpenCheckpointStoreAsync(GroupCommit);

            var feed = new Feed();
            var kills = new List<Kill>();
            var exitCodes = new List<int>();
            string? stuck = null;
            Console.Error.WriteLine($"{Name}: {settings.Orders:N0} orders, {settings.Kills} kill(s), {settings.InFlight} in flight per worker");
            var wallStarted = Stopwatch.GetTimestamp();
            var worker = WorkerProcess.Start(0, args, feed);
            try
            {
                // 2. Each kill: at its share of the orders, once the previous kill's orphans have completed.
                for (var j = 1; j <= settings.Kills && stuck is null; j++)
                {
                    var threshold = settings.Orders * j / (settings.Kills + 1);
                    var previous = kills.Count > 0 ? kills[^1] : null;
                    var (ready, problem) = await WaitAsync(worker, async () =>
                        worker.Completed >= threshold && (previous is null || await previous.WatchAsync(feed, store)));
                    // Stuck or failed; or done with every order before the kill could land, which a check reports.
                    if (!ready || worker.Completed >= settings.Orders)
                    {
                        stuck = problem;
                        break;
                    }

                    var completedAtKill = worker.Completed;
                    var killedAt = DateTime.UtcNow;
                    worker.Kill();
                    await worker.ExitedAsync();
                    exitCodes.Add(worker.ExitCode);

                    // The orphans: what the store has running now that the worker is gone.
                    var listingStarted = Stopwatch.GetTimestamp();
                    var orphans = await ListRunningAsync(store);
                    var listingMs = Ticks.ToMilliseconds(Stopwatch.GetTimestamp() - listingStarted);
                    kills.Add(new Kill(j, completedAtKill, killedAt, orphans, listingMs));
                    Console.Error.WriteLine($"{Name}: killed worker-{j - 1} at {completedAtKill:N0} completed; {orphans.Count:N0} orphans (listed in {listingMs:N1} ms); starting worker-{j}");
                    var dead = worker;
                    worker = WorkerProcess.Start(j, args, feed);
                    dead.Dispose();
                }

                // 3 and 4. The last worker finishes every order, the last kill's orphans with them, and exits.
                if (stuck is null)
                {
                    (_, stuck) = await WaitAsync(worker, () => Task.FromResult(false));
                }
                if (!worker.HasExited)
                {
                    worker.Kill();
                }

                await worker.ExitedAsync();
                exitCodes.Add(worker.ExitCode);
            }
            finally
            {
                if (!worker.HasExited)
                {
                    worker.Kill();
                }

                worker.Dispose();
            }

            var wallSeconds = Ticks.ToSeconds(Stopwatch.GetTimestamp() - wallStarted);
            if (exitCodes[^1] == 2)
            {
                throw new EnvironmentException("a worker couldn't run: see its message above");
            }

            foreach (var kill in kills)
            {
                await kill.WatchAsync(feed, store, lookInStore: true);
            }

            Console.Error.WriteLine($"{Name}: checking every order");
            var outcome = await Outcome.CheckAsync(postgres, store, settings.Orders);

            foreach (var kill in kills)
            {
                report.Row(new()
                {
                    ["kill"] = (long)kill.Number,
                    ["completed_at_kill"] = kill.CompletedAtKill,
                    ["orphans"] = (long)kill.Orphans.Count,
                    ["first_recovered_ms"] = kill.FirstRecoveredMs,
                    ["all_recovered_ms"] = kill.AllRecoveredMs,
                });
            }

            var late = feed.LatenessMilliseconds();
            var timerLate = Distribution.Of(late);
            report.Summary["orders"] = settings.Orders;
            report.Summary["kills"] = (long)kills.Count;
            report.Summary["lost"] = outcome.Lost;
            report.Summary["duplicate_charges"] = outcome.DuplicateCharges;
            report.Summary["charges"] = outcome.Charges;
            report.Summary["charge_attempts"] = outcome.ChargeAttempts;
            report.Summary["recovered_node_runs"] = outcome.RecoveredNodeRuns;
            report.Summary["lease_ttl_ms"] = settings.LeaseTtlMs;
            report.Summary["wall_s"] = wallSeconds;
            report.Summary["orders_per_s"] = settings.Orders / wallSeconds;
            report.Summary["timer_late_p50_ms"] = timerLate.P50;
            report.Summary["timer_late_p99_ms"] = timerLate.P99;

            // What the recovery and the listings cost: the passes every worker ran, and the driver's orphan listings.
            var passes = feed.Passes();
            var idle = passes.Where(p => p.Recovered == 0).Select(p => p.Milliseconds).ToArray();
            var all = passes.Select(p => p.Milliseconds).ToArray();
            var allPasses = Distribution.Of(all);
            var idlePasses = Distribution.Of(idle);
            report.EngineSpecific["recovery_passes"] = (long)passes.Count;
            report.EngineSpecific["recovery_pass_p50_ms"] = allPasses.P50;
            report.EngineSpecific["recovery_pass_max_ms"] = allPasses.Max;
            report.EngineSpecific["idle_recovery_passes"] = (long)idle.Length;
            report.EngineSpecific["idle_recovery_pass_p50_ms"] = idlePasses.P50;
            report.EngineSpecific["idle_recovery_pass_p99_ms"] = idlePasses.P99;
            report.EngineSpecific["recovered_by_passes"] = passes.Sum(p => (long)p.Recovered);
            report.EngineSpecific["skipped_by_passes"] = passes.Sum(p => (long)p.Skipped);
            report.EngineSpecific["orphan_listing_ms"] = kills.Select(k => k.ListingMs).ToList();
            report.EngineSpecific["orphans_seen_completed_unreported"] = kills.Sum(k => (long)k.Unreported);
            report.EngineSpecific["model_calls_reported"] = (long)late.Length;
            report.EngineSpecific["worker_exit_codes"] = exitCodes.Select(c => (long)c).ToList();

            // The checks.
            var lastWorker = exitCodes[^1] == 0 ? null : $"the last worker exited with code {exitCodes[^1]}";
            report.Check(
                "no order lost: every thread completed, with exactly one completed checkpoint",
                outcome.Lost == 0 && outcome.NotOneCompleted == 0 && stuck is null,
                Join(stuck, lastWorker, outcome.LostDetail, outcome.NotOneCompletedDetail));
            report.Check(
                "no order charged twice, and every order charged",
                outcome.DuplicateCharges == 0 && outcome.Charges == settings.Orders && outcome.UnknownCharges == 0,
                outcome.DuplicateCharges == 0 && outcome.Charges == settings.Orders && outcome.UnknownCharges == 0
                    ? null
                    : $"{outcome.Charges:N0} ledger rows for {settings.Orders:N0} orders, {outcome.DuplicateCharges:N0} orders charged more than once, {outcome.UnknownCharges:N0} rows for no order{(outcome.FirstDuplicate is null ? string.Empty : $" (the first charged twice: {outcome.FirstDuplicate})")}");
            report.Check(
                "every order's Charge runs used one idempotency key, the ledger's",
                outcome.KeyMismatches == 0,
                outcome.KeyMismatchDetail);
            report.Check(
                "every order's final charge_id is the ledger's",
                outcome.WrongChargeIds == 0,
                outcome.WrongChargeIdDetail);
            var empty = kills.Where(k => k.Orphans.Count == 0).Select(k => k.Number).ToList();
            report.Check(
                "every kill left at least one orphan",
                kills.Count == settings.Kills && empty.Count == 0,
                kills.Count < settings.Kills
                    ? $"only {kills.Count} of {settings.Kills} kills happened"
                    : empty.Count == 0 ? null : $"kill(s) {string.Join(", ", empty)} left no thread running");
            report.Check(
                "no lease left held",
                outcome.LeasesHeld == 0,
                outcome.LeasesHeld == 0 ? null : $"{outcome.LeasesHeld:N0} threads still held; the first: {outcome.FirstHeld}");
            return await report.FinishAsync();
        }

        /// <summary>
        /// Waits until <paramref name="ready"/> holds, or the worker exits, or it completes nothing for
        /// <see cref="Stall"/>. Not ready: with why when the worker exited with an error or was stuck, with
        /// <see langword="null"/> when it exited with code 0, done with every order.
        /// </summary>
        private static async Task<(bool Ready, string? Problem)> WaitAsync(WorkerProcess worker, Func<Task<bool>> ready)
        {
            var lastCompleted = -1L;
            var lastChange = Stopwatch.GetTimestamp();
            while (true)
            {
                if (await ready())
                {
                    return (true, null);
                }

                if (worker.HasExited)
                {
                    await worker.ExitedAsync();
                    return (false, worker.ExitCode == 0 ? null : $"worker-{worker.Index} exited with code {worker.ExitCode}");
                }

                var completed = worker.Completed;
                if (completed != lastCompleted)
                {
                    lastCompleted = completed;
                    lastChange = Stopwatch.GetTimestamp();
                }
                else if (Stopwatch.GetElapsedTime(lastChange) > Stall)
                {
                    worker.Kill();
                    return (false, $"worker-{worker.Index} completed nothing for {Stall.TotalMinutes:N0} minutes at {completed:N0} completed, and was stopped");
                }

                await Task.Delay(PollInterval);
            }
        }

        /// <summary>The threads the store lists as <c>running</c>, 1,000 to a page, as orders.</summary>
        private static async Task<List<long>> ListRunningAsync(PostgresCheckpointStore store)
        {
            var orders = new List<long>();
            var query = new ThreadQuery { States = new HashSet<CheckpointThreadState> { CheckpointThreadState.Running }, Limit = ThreadQuery.MaxLimit };
            while (true)
            {
                var page = await store.ListThreadsAsync(query);
                orders.AddRange(page.Threads.Select(t => StormGraph.Order(t.ThreadId)).Where(o => o >= 0));
                if (page.NextCursor is null)
                {
                    return orders;
                }

                query = query with { Cursor = page.NextCursor };
            }
        }

        private static string? Join(params string?[] details)
        {
            var present = details.Where(d => d is not null).ToList();
            return present.Count == 0 ? null : string.Join("; ", present);
        }
    }

    /// <summary>The run's sizes, from the command line or the spec's table.</summary>
    internal sealed record Settings(long Orders, int Kills, int InFlight, long LeaseTtlMs, ulong Seed, SimulatedModel Model)
    {
        /// <summary>Each model call: 5–20 ms.</summary>
        public static readonly LatencyRange Latency = LatencyRange.Milliseconds(5, 20);

        public TimeSpan LeaseTtl => TimeSpan.FromMilliseconds(LeaseTtlMs);

        public static Settings From(ScenarioArgs args)
        {
            var orders = args.Long("orders", 200_000, 300);
            var kills = args.Long("kills", 3, 1);
            var inFlight = args.Long("in-flight", 256, 32);
            var ttl = args.Long("lease-ttl-ms", 2_000, 2_000);
            if (orders is < 1 or > StormGraph.MaxOrders)
            {
                throw new UsageException($"--orders takes a whole number from 1 to {StormGraph.MaxOrders:N0}");
            }

            // Each kill waits for a share of the orders, orders × j / (kills + 1), which must be at least one order apart.
            if (kills < 0 || kills >= orders || kills > 1_000)
            {
                throw new UsageException("--kills takes a whole number from 0 to 1,000, and fewer than --orders");
            }

            if (inFlight is < 1 or > 100_000)
            {
                throw new UsageException("--in-flight takes a whole number from 1 to 100,000");
            }

            if (ttl is < 1 or > 3_600_000)
            {
                throw new UsageException("--lease-ttl-ms takes a whole number from 1 to 3,600,000");
            }

            return new Settings(orders, (int)kills, (int)inFlight, ttl, args.Seed, new SimulatedModel(args.Seed, Latency));
        }
    }

    /// <summary>
    /// One kill: when, at what count, the orphans it left, and when each was seen completed. A worker reports each
    /// thread its recovery completed, on the wall clock. An orphan the store has completed before its report
    /// arrives counts as completed at once, and takes its reported time when the report comes; one never reported
    /// (its last commit landed after the kill, or its worker's last report was lost) keeps the moment the driver
    /// saw it in the store.
    /// </summary>
    internal sealed class Kill(int number, long completedAtKill, DateTime killedAt, List<long> orphans, double listingMs)
    {
        /// <summary>How often the watch looks in the store for orphans no worker reported.</summary>
        private static readonly TimeSpan StoreLookInterval = TimeSpan.FromSeconds(1);

        private readonly Dictionary<long, DateTime> _completed = [];
        private readonly HashSet<long> _unreported = [];
        private long _lookedInStore = Stopwatch.GetTimestamp();

        public int Number => number;

        public long CompletedAtKill => completedAtKill;

        public List<long> Orphans => orphans;

        public double ListingMs => listingMs;

        /// <summary>Orphans seen completed in the store that no worker reported.</summary>
        public int Unreported => _unreported.Count;

        public double? FirstRecoveredMs => _completed.Count == 0 ? null : (_completed.Values.Min() - killedAt).TotalMilliseconds;

        public double? AllRecoveredMs => _completed.Count < orphans.Count || _completed.Count == 0 ? null : (_completed.Values.Max() - killedAt).TotalMilliseconds;

        /// <summary>
        /// Notes the orphans completed since the last look, from the workers' reports, and from the store once a
        /// second (or when <paramref name="lookInStore"/>) for those not reported; says whether all have completed.
        /// </summary>
        public async Task<bool> WatchAsync(Feed feed, PostgresCheckpointStore store, bool lookInStore = false)
        {
            if (Stopwatch.GetElapsedTime(_lookedInStore) >= StoreLookInterval)
            {
                lookInStore = true;
                _lookedInStore = Stopwatch.GetTimestamp();
            }

            foreach (var order in _unreported.ToList())
            {
                if (feed.RecoveredAt(order) is { } reported)
                {
                    _completed[order] = reported;
                    _unreported.Remove(order);
                }
            }

            foreach (var order in orphans)
            {
                if (_completed.ContainsKey(order))
                {
                    continue;
                }

                if (feed.RecoveredAt(order) is { } at)
                {
                    _completed[order] = at;
                }
                else if (lookInStore && await store.LoadAsync(StormGraph.ThreadId(order)) is { Status: CheckpointStatus.Completed })
                {
                    _completed[order] = feed.RecoveredAt(order) ?? DateTime.UtcNow;
                    if (feed.RecoveredAt(order) is null)
                    {
                        _unreported.Add(order);
                    }
                }
            }

            return _completed.Count == orphans.Count;
        }
    }

    /// <summary>What every worker has reported, killed ones included.</summary>
    internal sealed class Feed
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<long, DateTime> _recovered = [];
        private readonly List<long> _lateMicroseconds = [];
        private readonly List<Pass> _passes = [];

        public DateTime? RecoveredAt(long order)
        {
            lock (_lock)
            {
                return _recovered.TryGetValue(order, out var at) ? at : null;
            }
        }

        public double[] LatenessMilliseconds()
        {
            lock (_lock)
            {
                return [.. _lateMicroseconds.Select(us => us / 1000.0)];
            }
        }

        public List<Pass> Passes()
        {
            lock (_lock)
            {
                return [.. _passes];
            }
        }

        /// <summary>Takes one of <see cref="WorkerLine"/>'s lines; says whether it was one.</summary>
        public bool Take(string line, out long? progress)
        {
            progress = null;
            var parts = line.Split(' ');
            lock (_lock)
            {
                switch (parts)
                {
                    case ["progress", var n]:
                        progress = long.Parse(n, CultureInfo.InvariantCulture);
                        return true;
                    case ["recovered", var order, var ticks]:
                        _recovered.TryAdd(long.Parse(order, CultureInfo.InvariantCulture), new DateTime(long.Parse(ticks, CultureInfo.InvariantCulture), DateTimeKind.Utc));
                        return true;
                    case ["late", var values]:
                        _lateMicroseconds.AddRange(values.Split(',').Select(v => long.Parse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)));
                        return true;
                    case ["pass", var ms, var recovered, var skipped]:
                        _passes.Add(new Pass(
                            double.Parse(ms, CultureInfo.InvariantCulture),
                            int.Parse(recovered, CultureInfo.InvariantCulture),
                            int.Parse(skipped, CultureInfo.InvariantCulture)));
                        return true;
                    default:
                        return false;
                }
            }
        }
    }

    /// <summary>A worker's process, started from this binary, its reports read as they come.</summary>
    internal sealed class WorkerProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task _reading;
        private long _completed;

        private WorkerProcess(int index, Process process, Feed feed)
        {
            Index = index;
            _process = process;
            _reading = Task.Run(() => ReadAsync(feed));
        }

        public int Index { get; }

        /// <summary>The orders the worker last said it has seen completed.</summary>
        public long Completed => Interlocked.Read(ref _completed);

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public static WorkerProcess Start(int index, ScenarioArgs args, Feed feed) =>
            new(index, ChildProcess.StartSelf(["worker", index.ToString(CultureInfo.InvariantCulture), .. args.Forward()], redirectStandardOutput: true), feed);

        /// <summary>Kills the process outright: <c>TerminateProcess</c> on Windows, <c>SIGKILL</c> elsewhere.</summary>
        public void Kill() => _process.Kill(entireProcessTree: true);

        /// <summary>Waits for the process to exit and for the last of its reports.</summary>
        public async Task ExitedAsync()
        {
            await _process.WaitForExitAsync();
            await _reading;
        }

        public void Dispose() => _process.Dispose();

        private async Task ReadAsync(Feed feed)
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                try
                {
                    if (!feed.Take(line, out var progress))
                    {
                        Console.Error.WriteLine($"worker-{Index}: {line}");
                    }
                    else if (progress is { } n)
                    {
                        Interlocked.Exchange(ref _completed, n);
                    }
                }
                catch (FormatException)
                {
                    // A line cut short by the kill.
                }
            }
        }
    }

    /// <summary>What the store and the two tables hold at the end, order by order: what the checks check.</summary>
    internal sealed record Outcome(
        long Lost,
        string? LostDetail,
        long NotOneCompleted,
        string? NotOneCompletedDetail,
        long Charges,
        long DuplicateCharges,
        string? FirstDuplicate,
        long UnknownCharges,
        long ChargeAttempts,
        long RecoveredNodeRuns,
        long KeyMismatches,
        string? KeyMismatchDetail,
        long WrongChargeIds,
        string? WrongChargeIdDetail,
        long LeasesHeld,
        string? FirstHeld)
    {
        private const int InFlight = 64;

        public static async Task<Outcome> CheckAsync(StressPostgres postgres, PostgresCheckpointStore store, long orders)
        {
            // Every order's log: its latest checkpoint, its completed checkpoints, and its final charge_id.
            var latest = new CheckpointStatus?[orders];
            var completedCount = new int[orders];
            var finalChargeId = new string?[orders];
            var held = new bool[orders];
            await Concurrently.ForEachAsync(0, orders, InFlight, async order =>
            {
                var threadId = StormGraph.ThreadId(order);
                var entries = new List<Checkpoint>();
                long? after = null;
                while (true)
                {
                    var page = await store.GetLogAsync(threadId, after, ThreadQuery.MaxLimit);
                    entries.AddRange(page.Entries.Select(e => e.Checkpoint));
                    if (page.Entries.Count < ThreadQuery.MaxLimit)
                    {
                        break;
                    }

                    after = page.Entries[^1].Sequence;
                }

                if (entries.Count > 0)
                {
                    latest[order] = entries[^1].Status;
                    completedCount[order] = entries.Count(c => c.Status == CheckpointStatus.Completed);
                    if (entries[^1].FrozenState is { } state)
                    {
                        finalChargeId[order] = state.Deserialize<StormState>()?.ChargeId;
                    }
                }

                held[order] = await store.GetLeaseAsync(threadId) is not null;
            });

            var lost = Enumerable.Range(0, (int)orders).Where(o => latest[o] != CheckpointStatus.Completed).ToList();
            var notOne = Enumerable.Range(0, (int)orders).Where(o => completedCount[o] != 1).ToList();

            // The ledger, by order.
            var charges = await Ledger.ChargesAsync(postgres.DataSource);
            var byOrder = charges.GroupBy(c => StormGraph.Order(c.ThreadId)).ToDictionary(g => g.Key, g => g.ToList());
            var duplicates = byOrder.Where(g => g.Key >= 0 && g.Value.Count > 1).Select(g => g.Key).Order().ToList();
            var unknown = byOrder.Where(g => g.Key < 0 || g.Key >= orders).Sum(g => (long)g.Value.Count);

            // Every Charge run of an order used one key, and it is the key the ledger charged under.
            var attempts = await Ledger.AttemptsAsync(postgres.DataSource);
            var chargeKeys = attempts.Where(a => a.Node == "Charge").GroupBy(a => StormGraph.Order(a.ThreadId))
                .ToDictionary(g => g.Key, g => g.Select(a => a.IdempotencyKey).Distinct().ToList());
            var keyMismatches = new List<string>();
            for (var order = 0L; order < orders; order++)
            {
                var keys = chargeKeys.GetValueOrDefault(order) ?? [];
                var ledgerKeys = byOrder.GetValueOrDefault(order)?.Select(c => c.IdempotencyKey).ToList() ?? [];
                if (keys.Count != 1 || ledgerKeys.Count != 1 || keys[0] != ledgerKeys[0])
                {
                    keyMismatches.Add($"{StormGraph.ThreadId(order)} ran Charge under [{string.Join(", ", keys)}] and the ledger has [{string.Join(", ", ledgerKeys)}]");
                }
            }

            // Every final charge_id is the ledger's for that order.
            var wrongIds = new List<string>();
            for (var order = 0L; order < orders; order++)
            {
                var ledgerIds = byOrder.GetValueOrDefault(order)?.Select(c => c.ChargeId).Distinct().ToList() ?? [];
                if (finalChargeId[order] is not { } id || ledgerIds.Count != 1 || ledgerIds[0] != id || id != StormGraph.ChargeId((ulong)order))
                {
                    wrongIds.Add($"{StormGraph.ThreadId(order)} ends with charge_id '{finalChargeId[order]}' and the ledger has [{string.Join(", ", ledgerIds)}]");
                }
            }

            var heldOrders = Enumerable.Range(0, (int)orders).Where(o => held[o]).ToList();
            return new Outcome(
                lost.Count,
                lost.Count == 0 ? null : $"{lost.Count:N0} orders not completed; the first: {StormGraph.ThreadId(lost[0])}, {(latest[lost[0]] is { } s ? $"latest {s}" : "no checkpoint")}",
                notOne.Count,
                notOne.Count == 0 ? null : $"{notOne.Count:N0} orders without exactly one completed checkpoint; the first: {StormGraph.ThreadId(notOne[0])}, with {completedCount[notOne[0]]}",
                charges.Count,
                duplicates.Count,
                duplicates.Count == 0 ? null : StormGraph.ThreadId(duplicates[0]),
                unknown,
                attempts.Count(a => a.Node == "Charge"),
                attempts.Count(a => a.IsRecovery),
                keyMismatches.Count,
                keyMismatches.Count == 0 ? null : $"{keyMismatches.Count:N0} orders; the first: {keyMismatches[0]}",
                wrongIds.Count,
                wrongIds.Count == 0 ? null : $"{wrongIds.Count:N0} orders; the first: {wrongIds[0]}",
                heldOrders.Count,
                heldOrders.Count == 0 ? null : StormGraph.ThreadId(heldOrders[0]));
        }
    }
}
