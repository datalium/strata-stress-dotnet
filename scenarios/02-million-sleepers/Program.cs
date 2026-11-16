// Scenario 2, Million sleepers (SCENARIOS.md, "2. Million sleepers").
//
// A million threads run to an interrupt boundary and suspend there, their checkpoints in Postgres. The process's
// memory is sampled as they pile up: a suspended thread is a row in the store, not an object in the process, so the
// memory shouldn't grow with them. Then a second process, started from this binary in `resume` mode, opens a new
// store on the same database, lists the threads waiting at the boundary, and resumes every one with an approval.
//
// The checks are about correctness only: every thread suspended and is listed, every resume completed with the
// right resolution, and the store ends with every thread resumed. The memory figures are the result, never a
// check: a quick run's thousand threads are too few to show anything above the noise.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Storage.Postgres;
using Datalium.Strata.Stress.Harness;
using Datalium.Strata.Stress.Sleepers;

const string Usage =
    "dotnet run -c Release --project scenarios/02-million-sleepers -- [--quick] [--json PATH] [--seed N] " +
    "[--threads N] [--in-flight N] [--milestones N,N,...] [--warmup N]";

return await Scenario.RunAsync(Sleepers.Name, Usage, () => Sleepers.RunAsync(args));

namespace Datalium.Strata.Stress.Sleepers
{
    internal static class Sleepers
    {
        public const string Name = "02-million-sleepers";

        /// <summary>The spec's store: group commit on, with its defaults (256 saves a flush, 4 flushes at once).</summary>
        private static readonly GroupCommitOptions GroupCommit = new();

        public static async Task<int> RunAsync(string[] argv)
        {
            var args = ScenarioArgs.Parse(argv, "threads", "in-flight", "milestones", "warmup");
            var settings = Settings.From(args);
            return args.Positional switch
            {
                [] => await SuspendAsync(args, settings),
                ["resume", var figuresPath] => await ResumeAsync(settings, figuresPath),
                _ => throw new UsageException($"unknown mode '{string.Join(' ', args.Positional)}': only the scenario starts a mode"),
            };
        }

        /// <summary>
        /// Phase 1, in the scenario's own process: suspend every thread, sampling at each milestone; then start
        /// phase 2 and wait for it; then check the store and write the one report.
        /// </summary>
        private static async Task<int> SuspendAsync(ScenarioArgs args, Settings settings)
        {
            var report = new Report(Name, args);
            report.Parameters["threads"] = settings.Threads;
            report.Parameters["in_flight"] = (long)settings.InFlight;
            report.Parameters["milestones"] = settings.Milestones;
            report.Parameters["warmup"] = settings.Warmup;
            report.Parameters["latency_min_ms"] = settings.Model.Latency.MinMicroseconds / 1000.0;
            report.Parameters["latency_max_ms"] = settings.Model.Latency.MaxMicroseconds / 1000.0;
            report.Columns(
                new Column("suspended", "Suspended"),
                Column.Mebibytes("resident_bytes", "Resident MiB"),
                Column.Mebibytes("store_bytes", "Store MiB"));

            await using var postgres = await StressPostgres.ConnectAsync();
            postgres.Describe(report.Parameters, GroupCommit);

            // 1. Empty the store, then the warm-up: threads to the boundary and on to completion. Not counted.
            await postgres.DropCheckpointStoreAsync();
            var outcomes = new Outcomes();
            var samples = new List<Sample>();
            var suspendTicks = 0L;
            long checkpointBytes;
            ListedThreads waiting;
            await using (var store = await postgres.OpenCheckpointStoreAsync(GroupCommit))
            {
                var graph = SleeperGraph.Build(settings.Model);
                Console.Error.WriteLine($"{Name}: warm-up, {settings.Warmup:N0} threads suspended and resumed");
                await Concurrently.ForEachAsync(0, settings.Warmup, settings.InFlight, async ticket =>
                {
                    var threadId = SleeperGraph.ThreadId(SleeperGraph.WarmupPrefix, ticket);
                    if (await SuspendOneAsync(graph, store, settings.Seed, threadId, ticket, outcomes))
                    {
                        await ResumeOneAsync(graph, store, threadId, ticket, outcomes);
                    }
                });

                // 2 and 3. Suspend every thread, at most --in-flight at once, pausing at each milestone to sample.
                var next = 0L;
                foreach (var milestone in settings.Milestones.Append(settings.Threads))
                {
                    if (milestone > next)
                    {
                        Console.Error.WriteLine($"{Name}: suspending threads {next:N0} to {milestone - 1:N0}");
                        var started = Stopwatch.GetTimestamp();
                        var from = next;
                        await Concurrently.ForEachAsync(from, milestone, settings.InFlight, ticket =>
                            SuspendOneAsync(graph, store, settings.Seed, SleeperGraph.ThreadId(SleeperGraph.SleeperPrefix, ticket), ticket, outcomes));
                        suspendTicks += Stopwatch.GetTimestamp() - started;
                        next = milestone;
                    }

                    // The threads' own number closes the loop; it is sampled only when it is a milestone.
                    if (samples.Count < settings.Milestones.Count)
                    {
                        var memory = MemorySample.Take();
                        var storeBytes = await postgres.CheckpointStoreBytesAsync();
                        samples.Add(new Sample(next, memory, storeBytes));
                        Console.Error.WriteLine(
                            $"{Name}: {next:N0} suspended, resident {memory.ResidentBytes / 1048576.0:N1} MiB, store {storeBytes / 1048576.0:N1} MiB");
                    }
                }

                // One thread's interrupted checkpoint, as JSON (as datalium/strata's benchmarks measure it).
                var sample = await store.LoadAsync(SleeperGraph.ThreadId(SleeperGraph.SleeperPrefix, 0));
                checkpointBytes = sample is null ? 0 : Encoding.UTF8.GetByteCount(sample.ToWireJson().ToJsonString());

                Console.Error.WriteLine($"{Name}: listing the threads waiting at the boundary");
                waiting = await ListedThreads.ListAsync(store, CheckpointThreadState.Interrupted, settings.Threads);

                // 4. Dispose of the store (which flushes what group commit holds) and the graph.
            }

            // Phase 2, in a process of its own.
            var resumed = await RunResumeProcessAsync(args);

            // After phase 2: what the store holds now, through a new store of this process's.
            Console.Error.WriteLine($"{Name}: checking the store after the resumes");
            ListedThreads stillWaiting, resumedThreads;
            await using (var store = await postgres.OpenCheckpointStoreAsync(GroupCommit))
            {
                stillWaiting = await ListedThreads.ListAsync(store, CheckpointThreadState.Interrupted, settings.Threads);
                resumedThreads = await ListedThreads.ListAsync(store, CheckpointThreadState.Resumed, settings.Threads);
            }

            foreach (var sample in samples)
            {
                report.Row(new()
                {
                    ["suspended"] = sample.Suspended,
                    ["resident_bytes"] = sample.Memory.ResidentBytes,
                    ["store_bytes"] = sample.StoreBytes,
                });
            }

            // Per thread: from the second milestone to the last, so what the first threads fill once isn't spread.
            var (second, last) = (samples[1], samples[^1]);
            var added = last.Suspended - second.Suspended;
            var perThread = last.Memory.Since(second.Memory, added);
            report.Summary["threads"] = settings.Threads;
            report.Summary["suspend_per_s"] = settings.Threads / Ticks.ToSeconds(suspendTicks);
            report.Summary["resident_per_thread_bytes"] = perThread.ResidentBytes;
            report.Summary["store_per_thread_bytes"] = (double)(last.StoreBytes - second.StoreBytes) / added;
            report.Summary["checkpoint_bytes"] = (long)checkpointBytes;
            report.Summary["list_s"] = resumed?.ListSeconds;
            report.Summary["resume_per_s"] = resumed is null ? null : resumed.Completed / resumed.ResumeSeconds;
            report.Summary["resume_process_resident_bytes"] = resumed?.ResidentBytes;

            report.EngineSpecific["milestones"] = samples.Select(s => new OrderedDictionary<string, object?>
            {
                ["suspended"] = s.Suspended,
                ["managed_heap_bytes"] = s.Memory.ManagedHeapBytes,
                ["private_bytes"] = s.Memory.PrivateBytes,
            }).ToList();
            report.EngineSpecific["managed_heap_per_thread_bytes"] = perThread.ManagedHeapBytes;
            report.EngineSpecific["private_per_thread_bytes"] = perThread.PrivateBytes;
            report.EngineSpecific["resume_process_managed_heap_bytes"] = resumed?.ManagedHeapBytes;
            report.EngineSpecific["resume_process_private_bytes"] = resumed?.PrivateBytes;

            // The checks. The warm-up's threads are resumed too, so the "resumed" listing is checked for the
            // sleepers, with the warm-up's set aside.
            report.Check(
                "after phase 1, the store lists every thread waiting at the boundary, and no other",
                outcomes.NotInterrupted == 0 && waiting.IsExactly(settings.Threads, warmups: 0),
                Join(outcomes.NotInterruptedDetail, waiting.Detail(settings.Threads, warmups: 0)));
            report.Check(
                "phase 2 listed every waiting thread, and no other",
                resumed is not null && resumed.Listed.IsExactly(settings.Threads, warmups: 0),
                resumed is null ? ResumeProcessFailed : resumed.Listed.Detail(settings.Threads, warmups: 0));
            report.Check(
                "every resume completed",
                resumed is not null && outcomes.NotCompleted == 0 && resumed.NotCompleted == 0 && resumed.Completed == settings.Threads,
                resumed is null
                    ? ResumeProcessFailed
                    : Join(outcomes.NotCompletedDetail, resumed.NotCompletedDetail, resumed.Completed == settings.Threads ? null : $"{resumed.Completed:N0} of {settings.Threads:N0} completed"));
            report.Check(
                "every resolution is right",
                resumed is not null && outcomes.Wrong == 0 && resumed.Wrong == 0,
                resumed is null ? ResumeProcessFailed : Join(outcomes.WrongDetail, resumed.WrongDetail));
            report.Check(
                "after phase 2, no thread waits at the boundary",
                stillWaiting.Total == 0,
                stillWaiting.Total == 0 ? null : $"{stillWaiting.Total:N0} still wait, {stillWaiting.Warmups:N0} of them warm-up threads");
            report.Check(
                "after phase 2, the store lists every thread as resumed",
                resumedThreads.IsExactly(settings.Threads, warmups: settings.Warmup),
                resumedThreads.Detail(settings.Threads, warmups: settings.Warmup));
            return await report.FinishAsync();
        }

        private const string ResumeProcessFailed = "the resume process didn't report (see its output above)";

        /// <summary>Starts phase 2, waits for it, and reads its figures; <see langword="null"/> if it didn't report.</summary>
        private static async Task<ResumeFigures?> RunResumeProcessAsync(ScenarioArgs args)
        {
            var figuresPath = Path.Combine(Path.GetTempPath(), $"strata-stress-{Name}-{Guid.NewGuid():N}.json");
            try
            {
                Console.Error.WriteLine($"{Name}: starting the resume process");
                using var child = ChildProcess.StartSelf(["resume", figuresPath, .. args.Forward()]);
                await child.WaitForExitAsync();
                if (child.ExitCode == 2)
                {
                    throw new EnvironmentException("the resume process couldn't run: see its message above");
                }

                if (child.ExitCode != 0 || !File.Exists(figuresPath))
                {
                    Console.Error.WriteLine($"{Name}: the resume process exited with code {child.ExitCode}");
                    return null;
                }

                await using var file = File.OpenRead(figuresPath);
                return await JsonSerializer.DeserializeAsync<ResumeFigures>(file);
            }
            finally
            {
                File.Delete(figuresPath);
            }
        }

        /// <summary>
        /// Phase 2, in a child process: a new store on the same database; list the waiting threads page by page;
        /// resume each, at most --in-flight at once; report to the parent through a file.
        /// </summary>
        private static async Task<int> ResumeAsync(Settings settings, string figuresPath)
        {
            // 1. A new store, and a graph built from scratch: nothing is shared with phase 1 but the database.
            await using var postgres = await StressPostgres.ConnectAsync();
            await using var store = await postgres.OpenCheckpointStoreAsync(GroupCommit);
            var graph = SleeperGraph.Build(settings.Model);

            // 2. List the threads waiting at the boundary, until none are left to list.
            Console.Error.WriteLine($"{Name} (resume): listing the threads waiting at the boundary");
            var started = Stopwatch.GetTimestamp();
            var listed = await ListedThreads.ListAsync(store, CheckpointThreadState.Interrupted, settings.Threads, keepTickets: true);
            var listSeconds = Ticks.ToSeconds(Stopwatch.GetTimestamp() - started);
            var tickets = listed.TakeTickets();

            // 3. Resume each one with the approval, at most --in-flight at once, until all have completed.
            Console.Error.WriteLine($"{Name} (resume): {listed.Total:N0} listed in {listSeconds:N1} s; resuming them");
            var outcomes = new Outcomes();
            started = Stopwatch.GetTimestamp();
            await Concurrently.ForEachAsync(0, tickets.Length, settings.InFlight, i =>
            {
                var ticket = tickets[i];
                return ResumeOneAsync(graph, store, SleeperGraph.ThreadId(SleeperGraph.SleeperPrefix, ticket), ticket, outcomes);
            });
            var resumeSeconds = Ticks.ToSeconds(Stopwatch.GetTimestamp() - started);

            // The process's memory when its last thread completed, the list of tickets let go first.
            tickets = [];
            var memory = MemorySample.Take();

            // 4. Report to the parent, and exit.
            var figures = new ResumeFigures(
                listed, listSeconds, outcomes.Completed, outcomes.NotCompleted, outcomes.NotCompletedDetail, outcomes.Wrong, outcomes.WrongDetail,
                resumeSeconds, memory.ResidentBytes, memory.PrivateBytes, memory.ManagedHeapBytes);
            await using (var file = File.Create(figuresPath))
            {
                await JsonSerializer.SerializeAsync(file, figures);
            }

            Console.Error.WriteLine($"{Name} (resume): {outcomes.Completed:N0} completed in {resumeSeconds:N1} s");
            return 0;
        }

        /// <summary>Runs a thread to the boundary; says whether it suspended there.</summary>
        private static async Task<bool> SuspendOneAsync(
            CompiledGraph<SleeperState> graph, ICheckpointStore store, ulong seed, string threadId, long ticket, Outcomes outcomes)
        {
            try
            {
                var result = await graph.RunAsync(threadId, SleeperState.Start(seed, (ulong)ticket), store);
                return outcomes.Suspended(threadId, result, null);
            }
            catch (Exception e)
            {
                return outcomes.Suspended(threadId, null, e);
            }
        }

        private static async Task ResumeOneAsync(
            CompiledGraph<SleeperState> graph, ICheckpointStore store, string threadId, long ticket, Outcomes outcomes)
        {
            try
            {
                var result = await graph.ResumeAsync(threadId, HumanSignal.Approve(SleeperGraph.Reviewer), store);
                outcomes.Resumed(threadId, (ulong)ticket, result, null);
            }
            catch (Exception e)
            {
                outcomes.Resumed(threadId, (ulong)ticket, null, e);
            }
        }

        private static string? Join(params string?[] details)
        {
            var present = details.Where(d => d is not null).ToList();
            return present.Count == 0 ? null : string.Join("; ", present);
        }
    }

    /// <summary>The run's sizes, from the command line or the spec's table.</summary>
    internal sealed record Settings(long Threads, int InFlight, IReadOnlyList<long> Milestones, long Warmup, ulong Seed, SimulatedModel Model)
    {
        /// <summary>Each model call: 1–5 ms.</summary>
        public static readonly LatencyRange Latency = LatencyRange.Milliseconds(1, 5);

        public static Settings From(ScenarioArgs args)
        {
            var threads = args.Long("threads", 1_000_000, 1_000);
            var inFlight = args.Long("in-flight", 256, 32);
            var milestones = args.Longs("milestones", [0, 10_000, 100_000, 1_000_000], [0, 100, 1_000]);
            var warmup = args.Long("warmup", 1_000, 10);
            if (threads is < 1 or > SleeperGraph.MaxThreads)
            {
                throw new UsageException($"--threads takes a whole number from 1 to {SleeperGraph.MaxThreads:N0}");
            }

            if (inFlight is < 1 or > 100_000)
            {
                throw new UsageException("--in-flight takes a whole number from 1 to 100,000");
            }

            if (warmup is < 0 or > SleeperGraph.MaxThreads)
            {
                throw new UsageException($"--warmup takes a whole number from 0 to {SleeperGraph.MaxThreads:N0}");
            }

            // The per-thread figures run from the second milestone to the last, so there must be three.
            if (milestones.Count < 3 || milestones.Zip(milestones.Skip(1)).Any(m => m.First >= m.Second) || milestones[0] < 0 || milestones[^1] > threads)
            {
                throw new UsageException("--milestones takes three or more counts of threads, ascending, from 0 to --threads");
            }

            return new Settings(threads, (int)inFlight, milestones, warmup, args.Seed, new SimulatedModel(args.Seed, Latency));
        }
    }

    /// <summary>One milestone: the threads suspended, the process's memory, and the store's size.</summary>
    internal sealed record Sample(long Suspended, MemorySample Memory, long StoreBytes);

    /// <summary>What phase 2 reports to phase 1, through a file.</summary>
    internal sealed record ResumeFigures(
        ListedThreads Listed,
        double ListSeconds,
        long Completed,
        long NotCompleted,
        string? NotCompletedDetail,
        long Wrong,
        string? WrongDetail,
        double ResumeSeconds,
        long ResidentBytes,
        long PrivateBytes,
        long ManagedHeapBytes);

    /// <summary>
    /// The threads a listing found in one state: which sleepers, once each; the warm-up's, set aside; and any other
    /// id. Checking "exactly the threads" means every sleeper listed once and nothing else.
    /// </summary>
    internal sealed record ListedThreads(long Total, long Sleepers, long Missing, long Duplicates, long Warmups, long Others, string? FirstOther)
    {
        private long[]? _tickets;

        public bool IsExactly(long threads, long warmups) =>
            Sleepers == threads && Missing == 0 && Duplicates == 0 && Warmups == warmups && Others == 0;

        /// <summary>What the listing found, when it isn't exactly <see cref="IsExactly"/>'s; <see langword="null"/> when it is.</summary>
        public string? Detail(long threads, long warmups) =>
            IsExactly(threads, warmups)
                ? null
                : $"listed {Total:N0}: {Sleepers:N0} of {threads:N0} sleepers, {Missing:N0} missing, {Duplicates:N0} twice, " +
                  $"{Warmups:N0} warm-up (expected {warmups:N0}), {Others:N0} other{(FirstOther is null ? string.Empty : $" (the first: {FirstOther})")}";

        /// <summary>The sleepers' tickets in the order listed, if kept; the listing lets go of them.</summary>
        public long[] TakeTickets()
        {
            var tickets = _tickets ?? [];
            _tickets = null;
            return tickets;
        }

        /// <summary>Every thread in <paramref name="state"/>, page by page (the most a page may hold), until none are left.</summary>
        public static async Task<ListedThreads> ListAsync(PostgresCheckpointStore store, CheckpointThreadState state, long threads, bool keepTickets = false)
        {
            var seen = new bool[threads];
            var tickets = keepTickets ? new List<long>((int)threads) : null;
            long total = 0, sleepers = 0, duplicates = 0, warmups = 0, others = 0;
            string? firstOther = null;
            var query = new ThreadQuery { States = new HashSet<CheckpointThreadState> { state }, Limit = ThreadQuery.MaxLimit };
            while (true)
            {
                var page = await store.ListThreadsAsync(query);
                foreach (var thread in page.Threads)
                {
                    total++;
                    var ticket = SleeperGraph.SleeperTicket(thread.ThreadId);
                    if (ticket >= 0 && ticket < threads)
                    {
                        if (seen[ticket])
                        {
                            duplicates++;
                        }
                        else
                        {
                            seen[ticket] = true;
                            sleepers++;
                            tickets?.Add(ticket);
                        }
                    }
                    else if (thread.ThreadId.StartsWith(SleeperGraph.WarmupPrefix, StringComparison.Ordinal))
                    {
                        warmups++;
                    }
                    else
                    {
                        others++;
                        firstOther ??= thread.ThreadId;
                    }
                }

                if (page.NextCursor is null)
                {
                    break;
                }

                query = query with { Cursor = page.NextCursor };
            }

            return new ListedThreads(total, sleepers, threads - sleepers, duplicates, warmups, others, firstOther) { _tickets = tickets?.ToArray() };
        }
    }

    /// <summary>
    /// Checks every suspend and resume as it returns, and keeps the first of each kind of failure to show. Counters
    /// only: phase 1 must keep nothing per thread, or its memory would be the scenario's own.
    /// </summary>
    internal sealed class Outcomes
    {
        private long _notInterrupted;
        private long _completed;
        private long _notCompleted;
        private long _wrong;
        private string? _firstNotInterrupted;
        private string? _firstNotCompleted;
        private string? _firstWrong;

        public long NotInterrupted => Interlocked.Read(ref _notInterrupted);

        public long Completed => Interlocked.Read(ref _completed);

        public long NotCompleted => Interlocked.Read(ref _notCompleted);

        public long Wrong => Interlocked.Read(ref _wrong);

        public string? NotInterruptedDetail => NotInterrupted == 0 ? null : $"{NotInterrupted:N0} didn't suspend; the first: {_firstNotInterrupted}";

        public string? NotCompletedDetail => NotCompleted == 0 ? null : $"{NotCompleted:N0} didn't complete; the first: {_firstNotCompleted}";

        public string? WrongDetail => Wrong == 0 ? null : $"{Wrong:N0} wrong; the first: {_firstWrong}";

        /// <summary>A run to the boundary must return <c>Interrupted</c>. Says whether it did.</summary>
        public bool Suspended(string threadId, GraphExecutionResult<SleeperState>? result, Exception? error)
        {
            if (result is GraphExecutionResult<SleeperState>.Interrupted)
            {
                return true;
            }

            if (Interlocked.Increment(ref _notInterrupted) == 1)
            {
                _firstNotInterrupted = Describe(threadId, result, error);
            }

            return false;
        }

        /// <summary>A resume must return <c>Completed</c>, with the resolution <c>resolved-{ticket}-stress</c>.</summary>
        public void Resumed(string threadId, ulong ticket, GraphExecutionResult<SleeperState>? result, Exception? error)
        {
            if (result is not GraphExecutionResult<SleeperState>.Completed { State: var state })
            {
                if (Interlocked.Increment(ref _notCompleted) == 1)
                {
                    _firstNotCompleted = Describe(threadId, result, error);
                }

                return;
            }

            Interlocked.Increment(ref _completed);
            if ((state.Ticket != ticket || state.Resolution != SleeperGraph.ExpectedResolution(ticket, SleeperGraph.Reviewer))
                && Interlocked.Increment(ref _wrong) == 1)
            {
                _firstWrong = $"{threadId} has ticket {state.Ticket} and resolution '{state.Resolution}'";
            }
        }

        private static string Describe(string threadId, object? result, Exception? error) =>
            $"{threadId} {(error is null ? $"returned {result}" : $"threw {error.GetType().Name}: {error.Message}")}";
    }
}
