# Stress scenarios: the spec

**Status:** Draft, version 0.6, 2026-11-15. **Ticket:** [STR-95](https://datalium.atlassian.net/browse/STR-95). **Framework:** `0.9.0-preview.2`. **Output format:** `strata-stress/1`.

The one spec both stress demos implement: this repository (.NET, [STR-93](https://datalium.atlassian.net/browse/STR-93)) and `strata-stress-rust-preview` (Rust, [STR-94](https://datalium.atlassian.net/browse/STR-94)). It lives here first and is copied there. **The two copies stay identical:** a change lands in both repositories, as a pair of pull requests, and moves the version above.

The sizes and latencies below are targets, not measurements. They are settled after the first measured run (see [Open questions](#open-questions)), and until then every one of them may move.

## What the scenarios are

Four scenarios, each making one claim and checking it:

| # | Scenario | The claim | What it measures |
|:--|:--|:--|:--|
| 1 | [Burst](#1-burst) | Thousands of runs at once, each fanning out, cost the engine microseconds a run | Throughput, run latency (P50, P99, P99.9), engine overhead per run |
| 2 | [Million sleepers](#2-million-sleepers) | A suspended thread holds nothing in the process, and another process can resume it | Process memory and store bytes per thread, suspend and resume rates |
| 3 | [Kill it mid-storm](#3-kill-it-mid-storm) | A worker killed under load loses no run and charges nothing twice | Runs lost, duplicate charges, recovery time |
| 4 | [Durable firehose](#4-durable-firehose) | Checkpoints to Postgres scale with concurrency, more so with group commit | Checkpoints a second and run latency, group commit off and on |

What they are not:

- **They measure Strata, not a model provider.** Every model call is simulated: a wait of a known latency (see [The simulated model](#the-simulated-model)). No API key and no network, besides Postgres on the same machine.
- **They are not a race.** The same workload runs in both engines so that each engine's costs can be stated next to the other's. Where one costs more, the results say so and say why, as `datalium/strata` `benchmarks/RESULTS.md` does.
- **They are synthetic.** They are not the "load test from a real app"; StrataOps is the real app.
- **A scenario is not a microbenchmark.** `benchmarks/RESULTS.md` measures the engine's costs one at a time. The scenarios put those costs under load, together, and show what they add up to.

## Common rules

### Running a scenario

One command per scenario, from the repository root:

```bash
dotnet run -c Release --project scenarios/01-burst -- [--quick] [--json PATH] [--seed N] [scenario options]
cargo run --release -p burst -- [--quick] [--json PATH] [--seed N] [scenario options]
```

| Option | Meaning | Default |
|:--|:--|:--|
| `--quick` | The small sizes, for CI. Checks that the scenario still runs; it measures nothing | off |
| `--json PATH` | Where the JSON goes | `artifacts/results/<scenario>.json` |
| `--seed N` | The seed of every simulated latency (an unsigned 64-bit integer) | `1` |
| Scenario options | Override one size or setting, named as in the scenario's "Sizes" table (e.g. `--runs 1000,10000`) | the table's |

A list takes values separated by commas. An option a scenario doesn't know is a usage error.

**Exit codes:** `0` when every check passed, `1` when a check failed (the run still finishes and writes its output), `2` for a usage error or a service that can't be reached (no output is written).

A scenario that needs a child process (2 and 3) starts it itself, from the same binary, with a mode word before the options (e.g. `worker 2`). Nobody runs a mode by hand.

### Build and runtime

| | .NET | Rust |
|:--|:--|:--|
| Strata | `Datalium.Strata*` `0.9.0-preview.2` from nuget.org | `datalium-strata*` `=0.9.0-preview.2` from crates.io |
| Build | `-c Release` | `--release` |
| Runtime | .NET 10, server GC, concurrent GC, tiered PGO (the defaults otherwise) | tokio's multi-threaded runtime, one worker thread per logical core |
| Threads | The thread pool's defaults | tokio's defaults |

No tuning beyond this table. A setting that would help one engine is a finding to report, not a change to make quietly.

### The simulated model

A **model call** is a node handler that waits a latency and returns a deterministic answer. It doesn't go through `Datalium.Strata.AI` or `strata-ai`: in .NET that layer runs on `Microsoft.Extensions.AI`, which is not Strata's code, and the scenarios are about the engine and the stores (see [Open questions](#open-questions)).

The latency of a call is a pure function of the seed, the run and the call, so both engines draw the same latencies in the same places:

```text
mix(x):  z = x + 0x9E3779B97F4A7C15
         z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
         z = (z ^ (z >> 27)) * 0x94D049BB133111EB
         return z ^ (z >> 31)                      (SplitMix64; unsigned 64-bit, wrapping)

hash(seed, run, call) = mix(mix(mix(seed) ^ run) ^ call)

latency_us(seed, run, call, min_us, max_us) =
    min_us                                         if min_us == max_us
    min_us + hash(seed, run, call) % (max_us - min_us + 1)   otherwise
```

`run` is the run's number within the scenario (0, 1, 2, ...), and `call` is the call's number within the run, which each scenario fixes. A latency range is written `20–80 ms` and includes both ends.

Test vectors, which both harnesses' tests check:

| Input | Output |
|:--|:--|
| `mix(0)` | `0xE220A8397B1DCDAF` |
| `mix(1)` | `0x910A2DEC89025CC1` |
| `mix(0xFFFFFFFFFFFFFFFF)` | `0xE4D971771B652C20` |
| `hash(1, 0, 0)` | `12793040940332582595` |
| `hash(1, 0, 1)` | `7806873273932414515` |
| `hash(1, 1, 0)` | `6301985355436268297` |
| `hash(1, 9999, 9)` | `1449011636669827163` |
| `hash(42, 123456789, 3)` | `17597405645336132696` |
| `latency_us(1, 0, 0, 20000, 80000)` | `60310` |
| `latency_us(1, 0, 1, 20000, 80000)` | `28742` |
| `latency_us(1, 1, 0, 20000, 80000)` | `73339` |
| `latency_us(1, 9999, 9, 20000, 80000)` | `79191` |
| `latency_us(42, 123456789, 3, 20000, 80000)` | `70528` |

**The wait** is the runtime's own timer: `Task.Delay` in .NET, `tokio::time::sleep` in Rust. A latency of zero returns at once, without a timer. A timer wakes late, by up to the operating system's timer tick (about 15.6 ms on Windows unless something raises the resolution), so:

- the handler measures how long it really waited, with the monotonic clock (`Stopwatch`, `std::time::Instant`), and the scenario charges it with that, not with the latency it asked for;
- every scenario with latency reports how late its timers woke (`timer_late_*`), so a difference between the engines' timers shows up as what it is. Scenario 2 is the exception, and says why.

The measured wait ends when the handler runs again, so `timer_late_*` is how late the handler got going again: the timer's own lateness, plus the time the handler then waited for a thread. Under load the second is most of it. Both engines measure it the same way.

### Warm-up

Each scenario says what it runs before it measures. A warm-up uses its own thread ids (prefix `warmup-`), is never counted, and is the same in both engines. Its purpose is the JIT and the connection pool, so it is the same in quick runs, only smaller.

### Percentiles

From the whole sample, sorted: **nearest rank**, the value at rank ⌈p/100 · n⌉ counting from 1, with no interpolation and no histogram. P50, P90, P99, P99.9, the maximum and the mean. With fewer than 1,000 values the P99.9 is the maximum; the JSON still reports it.

### Memory

A memory sample is taken after a full, blocking, compacting collection in .NET (twice, with pending finalizers in between), and as it stands in Rust. Both report **resident memory** (`resident_bytes`: the working set on Windows, RSS elsewhere). .NET adds its managed heap and private bytes under `engine_specific`; Rust adds its allocator's figures there if it counts them.

Memory "per thread" or "per run" is the growth between two samples divided by what was added between them. A scenario names the two samples.

### Services

Scenarios 2, 3 and 4 use Postgres 16, from this repository's `docker-compose.yml` or a CI service on the same ports. The URL is `STRATA_STRESS_POSTGRES_URL`, default `postgres://strata:strata@localhost:5432/strata`; Redis, which no scenario uses yet, is `STRATA_STRESS_REDIS_URL`, default `redis://localhost:6379`. A scenario that can't reach a service exits with code 2 and says how to start it.

**Every scenario starts from an empty store**: it removes the store's tables, and its own, before it begins, and leaves them in place afterwards to be looked at. Only one scenario runs against a database at a time.

The JSON records the Postgres server version and the store's settings, with the same keys in every scenario that uses Postgres, in `parameters`:

| Key | What it is |
|:--|:--|
| `postgres_version` | The server's `server_version`, such as `16.15` |
| `group_commit` | `"on"` or `"off"` |
| `group_commit_max_batch`, `group_commit_max_flushes` | Group commit's saves a flush and flushes at once; present only when it is on |
| `pool_size` | The connection pool's maximum size |

It never records a password.

**The store's size** is `pg_total_relation_size` of the checkpoint store's four tables, `checkpoints`, `checkpoint_threads`, `checkpoint_leases` and `checkpoint_blobs`, summed: their indexes and TOAST included.

### Output

A run prints a Markdown table to standard output and writes the same figures as JSON. A quick run says, above its table, that it measures nothing.

The JSON is one object:

```json
{
  "format": "strata-stress/1",
  "scenario": "01-burst",
  "quick": false,
  "seed": 1,
  "started_at": "2026-11-20T14:03:11.402Z",
  "finished_at": "2026-11-20T14:05:40.117Z",
  "environment": {
    "engine": "dotnet",
    "strata_version": "0.9.0-preview.2",
    "runtime": ".NET 10.0.8",
    "os": "Microsoft Windows 10.0.26300",
    "cpu": "12th Gen Intel(R) Core(TM) i9-12900KF",
    "logical_cores": 24,
    "memory_bytes": 68719476736,
    "gc": "server, Interactive",
    "build": "release"
  },
  "parameters": { "...": "what the run was asked to do" },
  "rows": [ { "...": "one configuration's figures" } ],
  "summary": { "...": "the run's single figures" },
  "checks": [ { "name": "every run completed", "passed": true } ],
  "engine_specific": { "...": "figures only this engine has" }
}
```

- **Keys are snake_case and carry their unit:** `_ms`, `_us`, `_s`, `_bytes`, `_per_s`. Counts have no suffix. Times are floating-point; counts are integers.
- **The keys of `parameters`, `rows` and `summary` are this spec's**, listed per scenario, and are the same in both engines, so one script can read either. `engine` tells them apart. `environment.runtime` is `rustc 1.91.1` or similar in Rust, and `gc` is absent there.
- **No host name, user name, path or password.** The JSON is committed next to `RESULTS.md` in a public repository.
- A change to the keys moves `format` to `strata-stress/2`, in both engines at once.

### Checks

A scenario checks its claim on every run, quick ones included: a run that was lost, an answer that is wrong, a charge made twice. A failed check prints `FAIL` with what it found, and makes the exit code 1.

Only correctness is checked. How fast something went is a result, never a check: a slow machine must not fail CI, and a fast one must not hide a regression behind a threshold.

## 1. Burst

**Claim.** Thousands of runs started at the same moment, each fanning out to eight parallel model calls, complete with an engine overhead of microseconds a run. The run's time is the model's.

### Graph and state

Graph `stress-burst`, version `1`:

```text
Plan ──▶ Research ──▶ Answer
          (parallel group: b0, b1, ..., b7, merged in declared order)
```

| Node | Handler | Model call | Writes |
|:--|:--|:--|:--|
| `Plan` | async | call `0` | `plan = "plan-{run}"` |
| `Research` | a parallel group of 8 async branches, `b0` to `b7`, and a reducer | branch `bi` is call `1 + i` | branch `bi` returns the finding `"f{i}-{run}"`; the reducer appends them in declared order |
| `Answer` | async | call `9` | `answer = "answer-{run}-{findings.length}"` |

State (JSON field names): `run` (unsigned integer), `plan` (string), `findings` (list of strings), `answer` (string). Each handler also records how long its wait took, outside the state, in memory the scenario preallocates.

Checkpoints: the in-memory store, with the default policy (`Boundaries`), under which a run that completes writes no checkpoint, so this scenario writes nothing to the store. The store's cost is scenario 4's subject, not this one's.

### Latency

Each model call: `20–80 ms`. The mean critical path is about 175 ms: `Plan`, the slowest of eight branches, `Answer`. A control row runs with no latency at all, which measures the engine and nothing else.

### Sizes

| Option | Full | Quick |
|:--|:--|:--|
| `--runs` | `1000,10000,100000,1000000` | `100` |
| `--latency-ms` | `20-80` | `20-80` |
| `--control-runs` (latency 0) | `10000` | `100` |
| `--warmup` | `1000` | `10` |

`parameters`: `runs` (the list), `latency_min_ms`, `latency_max_ms`, `control_runs` and `warmup`. A `--control-runs` of `0` leaves the control row out.

### Procedure

For each row (each `--runs` value at the scenario's latency, then the control):

1. Run the warm-up: `--warmup` runs, at the row's latency, the same way as below. Not counted.
2. Take a memory sample. Note the time `t0`.
3. **Start every run at once:** in a loop, hand run 0, 1, ... to the runtime's thread pool, each as one work item that calls `RunAsync` (`Task.Run` in .NET, `tokio::spawn` in Rust), recording the time just before each is handed over (`started_i`). Then wait for all of them, recording the time each one's `RunAsync` returns (`finished_i`). A run's time so includes its wait for the pool to start it, as a server's request does.
4. While the runs are in flight, sample the resident memory every 100 ms, without a collection, and keep the highest.
5. Check every result.

Thread ids: `burst-{row}-{run}`, where `row` is the row's position (0, 1, ...).

### Measured

Per row:

| Key | What it is |
|:--|:--|
| `runs` | Runs in the row |
| `latency_min_ms`, `latency_max_ms` | The model's latency range |
| `model_calls` | `runs × 10` |
| `wall_s` | From the first `started_i` to the last `finished_i` |
| `start_spread_ms` | From the first `started_i` to the last: how long it took to start them all |
| `runs_per_s` | `runs / wall_s` |
| `model_calls_per_s` | `model_calls / wall_s` |
| `run_p50_ms`, `run_p90_ms`, `run_p99_ms`, `run_p999_ms`, `run_max_ms` | `finished_i − started_i` |
| `overhead_p50_us`, `overhead_p99_us`, `overhead_p999_us`, `overhead_max_us` | A run's time minus its critical path: `Plan`'s wait + the longest branch's wait + `Answer`'s wait, each as measured |
| `timer_late_p50_ms`, `timer_late_p99_ms` | Each call's measured wait minus the latency it asked for. Absent in the control row |
| `peak_resident_bytes` | The highest resident memory sampled during the row |

**What the overhead includes**, and the results must say so: the engine's own work (a run's setup, a node's transition, a branch's task, the reducer) and the time a run waits for the pool to start it, and a ready node or branch for a thread, when ten thousand runs are ready at once. The second is the runtime's scheduler under load; under a burst it is most of the tail. The control row, with no latency, shows the first on its own. The time a handler waits for a thread to resume it after its model call is in the measured wait, so in `timer_late_*`, not in the overhead (see [The simulated model](#the-simulated-model)).

`engine_specific` (.NET): `allocated_bytes_per_run` (`GC.GetTotalAllocatedBytes` across the row, divided by its runs) and `gen0_collections`, `gen1_collections`, `gen2_collections` across the row.

### Checks

- Every run completed (`Completed`, not `Failed` or `Interrupted`).
- Every run's `findings` is `["f0-{run}", ..., "f7-{run}"]`, in that order, and its `answer` is `"answer-{run}-8"`.

### Reference

`benchmarks/RESULTS.md`: a run's setup costs about 0.2 µs in .NET and 0.9 µs in Rust; a branch of a parallel group about 0.6 µs and 1.5 µs. The Rust demo's ticket asks for the branch cost to be explained, not hidden ([STR-107](https://datalium.atlassian.net/browse/STR-107)).

## 2. Million sleepers

**Claim.** A thread suspended at a boundary holds nothing in the process: a million of them grow its memory by nothing measurable, they cost their checkpoint in the store and no more, and a fresh process finds them and resumes them all.

### Graph and state

Graph `stress-sleepers`, version `1`:

```text
Intake ──▶ [interrupt boundary] ──▶ Resolve
```

| Node | Handler | Model call | Writes |
|:--|:--|:--|:--|
| `Intake` | async | call `0` | `intake = "intake-{ticket}"` |
| *(boundary after `Intake`)* | | | the thread waits for a human |
| `Resolve` | async | call `1` | `resolution = "resolved-{ticket}-{reviewer}"` |

State (JSON field names): `ticket` (unsigned integer), `payload` (string), `intake` (string), `resolution` (string, absent until `Resolve`). The `payload` is 1,024 characters that don't compress: the lower-case hex, 16 digits each, of `hash(seed, ticket, 1000 + k)` for `k` from 0 to 63, joined. It stands for the conversation a real thread would carry, and makes the checkpoint about the size of the benchmarks' (1,251 bytes there).

Thread ids: `sleeper-{ticket}`, the ticket zero-padded to 7 digits. Resumed with an approval from reviewer `stress`. The warm-up's threads are `warmup-{ticket}`, zero-padded the same way, for tickets `0` to `warmup − 1`, on the same graph with the same approval.

Store: Postgres, group commit on with its defaults (256 saves a flush, 4 flushes at once), checkpoint policy `Boundaries`: one `interrupted` checkpoint when a thread suspends. A resume saves it again stamped `resumed_at`, and a run that completes writes nothing, so after phase 2 a thread's state is `resumed` and the store doesn't hold its `resolution`. The resumes' results do, and phase 2 checks them.

### Latency

Each model call: `1–5 ms`. The claim is about a thread that isn't running, so the latency is small.

### Sizes

| Option | Full | Quick |
|:--|:--|:--|
| `--threads` | `1000000` | `1000` |
| `--in-flight` (runs at once) | `256` | `32` |
| `--milestones` (memory samples, in threads suspended; three or more, ascending, from `0` to `--threads`) | `0,10000,100000,1000000` | `0,100,1000` |
| `--warmup` | `1000` | `10` |

`parameters`: `threads`, `in_flight`, `milestones` (the list), `warmup`, `latency_min_ms`, `latency_max_ms`, and the store's keys (see [Services](#services)).

### Procedure

The scenario runs in two processes, so that the threads really outlive the process that suspended them.

**Phase 1, suspend** (the scenario's own process):

1. Empty the store. Run the warm-up: `--warmup` threads to the boundary and on to completion. Not counted.
2. Start threads 0, 1, ..., `threads − 1`, at most `--in-flight` at once, each running until it suspends at the boundary.
3. At each milestone, stop starting threads, wait for those in flight, take a memory sample and the store's size (see [Services](#services)), then go on. The time spent sampling is not counted in the suspend rate.
4. Load `sleeper-0000000`'s checkpoint back from the store, for `checkpoint_bytes`.
5. Dispose of the store and the graph.

**Phase 2, resume** (a child process the scenario starts, `resume` mode):

1. Open a new store on the same database.
2. List the threads waiting at a boundary, with the store's thread listing (state `interrupted`), 1,000 to a page (the largest page both engines allow, `ThreadQuery.MaxLimit` and `MAX_LIMIT`), following the cursor until none is left. Note how long it took.
3. Resume each one with the approval, at most `--in-flight` at once, until all have completed.
4. Let go of the list of threads, take a memory sample, report its figures to the parent through a file, and exit. The parent writes the one report.

### Measured

A row per milestone:

| Key | What it is |
|:--|:--|
| `suspended` | Threads suspended at this milestone |
| `resident_bytes` | Resident memory at the sample |
| `store_bytes` | The store's tables, summed |

Summary:

| Key | What it is |
|:--|:--|
| `threads` | Threads suspended and resumed |
| `suspend_per_s` | Threads suspended a second, sampling pauses excluded |
| `resident_per_thread_bytes` | Resident growth from the second milestone to the last, divided by the threads between them. From the second, not the first, so that what the first threads fill once (the connection pool, the JIT, buffers) isn't spread over a million |
| `store_per_thread_bytes` | The same, for the store's size |
| `checkpoint_bytes` | The wire JSON of `sleeper-0000000`'s `interrupted` checkpoint, loaded back from the store, in UTF-8 bytes, as `benchmarks/RESULTS.md` measures it |
| `list_s` | Phase 2's listing of the waiting threads |
| `resume_per_s` | Threads resumed and completed a second |
| `resume_process_resident_bytes` | Phase 2's resident memory after its last thread completed, sampled as in [Memory](#memory) once it has let go of its list of threads |

**No `timer_late_*`.** Recording each wait in phase 1 would make the process hold bytes for each suspended thread, which is what the scenario measures, so this scenario doesn't report how late its timers woke.

`engine_specific` (.NET): `managed_heap_bytes` and `private_bytes` at each milestone; `managed_heap_per_thread_bytes` and `private_per_thread_bytes` the same way as resident; and phase 2's `resume_process_managed_heap_bytes` and `resume_process_private_bytes`.

### Checks

- After phase 1, the store lists exactly the `threads` sleepers waiting at the boundary, each once, and no other thread.
- Phase 2 listed exactly the `threads` sleepers, each once, every resume returned `Completed`, and every resumed state's `resolution` is `"resolved-{ticket}-stress"`.
- After phase 2, no thread waits at the boundary, and the store lists exactly the `threads` sleepers as `resumed`, each once, plus the `warmup` warm-up threads, which were resumed too, and no other thread.

Each check lists the threads with the store's thread listing, as phase 2 does, so the checks test what the claim says: what the store lists.

There is deliberately no check on memory. "Holds nothing" is the result the table shows, measured, and a quick run's thousand threads are too few to show anything above the noise.

### Reference

`benchmarks/RESULTS.md`, against SQLite with ten thousand threads: a heap growth of 0 bytes per thread in Rust and nothing measurable in .NET, and 1,596 bytes per thread on disk. This scenario takes it to a million, with the store out of process and the resume in another process.

## 3. Kill it mid-storm

**Claim.** A worker process killed while hundreds of runs are in flight loses no run and charges nothing twice. Another worker finds what the dead one left, once its leases run out, and finishes it, at least once and charged once.

### Graph and state

Graph `stress-storm`, version `1`, compiled with the checkpoint policy `EveryNode`:

```text
Validate ──▶ Charge ──▶ Ship
```

| Node | Handler | Model call | Writes |
|:--|:--|:--|:--|
| `Validate` | async | call `0` | `validated = true` |
| `Charge` | async | call `1`, then the charge | `charge_id`: what the ledger returned for the node's idempotency key |
| `Ship` | async | call `2` | `shipped = true` |

State (JSON field names): `order` (unsigned integer), `amount_cents` (integer, `100 + order % 10000`), `validated` (bool), `charge_id` (string, absent until `Charge`), `shipped` (bool).

Thread ids: `order-{order}`, zero-padded to 6 digits. Every run holds a lease: owner `worker-{k}`, where `k` counts the worker processes from 0, with a TTL of `--lease-ttl-ms`.

**The charge** is the effect outside the state, and the thing a crash must not do twice. It is a Postgres table that deduplicates by the node's idempotency key, as a real payment API does:

```sql
CREATE TABLE stress_storm_ledger (
    idempotency_key text PRIMARY KEY,
    thread_id       text NOT NULL,
    charge_id       text NOT NULL,          -- 'ch_' || the order, zero-padded to 6 digits
    amount_cents    bigint NOT NULL
);
-- Charge: INSERT ... ON CONFLICT (idempotency_key) DO NOTHING, then SELECT charge_id by the key.

CREATE TABLE stress_storm_attempts (       -- every node run, from every worker, appended
    thread_id       text NOT NULL,
    node            text NOT NULL,
    idempotency_key text NOT NULL,
    worker          text NOT NULL,
    is_recovery     boolean NOT NULL,
    at              timestamptz NOT NULL DEFAULT now()
);
```

Every node appends to `stress_storm_attempts` first, before its model call, with its `NodeContext.IdempotencyKey` and `IsRecovery` (Rust: the same fields of the node's context), so a node run killed at any point is in the table. `Charge` writes to the ledger after its model call, so a kill can land between the charge and the node's checkpoint: the case at-least-once exists for.

Store: Postgres, group commit **off** (`GroupCommit` unset, the store's default), deltas and offload off, the connection pool at its default size. The claim is about the crash, not about an optimisation of the store's, which scenario 4 measures.

### Latency

Each model call: `5–20 ms`.

### Warm-up

None. Every worker is a fresh process, as a restarted worker is, so its JIT and its connection pool are part of what its recovery costs. A warm-up in the driver wouldn't warm the workers.

### Sizes

| Option | Full | Quick |
|:--|:--|:--|
| `--orders` | `200000` | `300` |
| `--kills` | `3` | `1` |
| `--in-flight` (runs at once, per worker) | `256` | `32` |
| `--lease-ttl-ms` | `2000` | `2000` |

`parameters`: `orders`, `kills`, `in_flight`, `lease_ttl_ms`, `recover_interval_ms` (250), `latency_min_ms`, `latency_max_ms`, and the store's keys (see [Services](#services)).

### Procedure

The scenario's own process is the **driver**: it never runs a graph. It starts **workers**, one at a time, as child processes (`worker {k}` mode), and kills them.

A worker:

1. Opens the store and the ledger.
2. **Recovers**, every 250 ms until it exits: `RecoverStaleAsync` (Rust: the same call), under its own lease, with its other options the defaults. One pass at a time: a pass starts 250 ms after the previous one started, or as soon as it returns if it took longer. A thread whose dead owner's lease is still live is skipped, and taken on a later pass.
3. **Starts** orders 0, 1, ..., `orders − 1`, at most `--in-flight` at once, skipping any order whose thread already has a checkpoint (those are either done, or the recovery's). A start refused because a dead worker's lease is still live is tried again after the TTL, looking for a checkpoint again first.
4. Prints a progress line with its count of completed orders every 100 ms: the orders whose thread it has seen `completed`, whether its own run completed it, its recovery did, or it skipped the order because it was already done. It also tells the driver, for each thread its recovery completed, when the recovery returned, on the wall clock (`DateTime.UtcNow`, `SystemTime::now()`), which the two processes share.
5. Exits with code 0 when every order's thread has completed.

The driver:

1. Empties the store and the two tables. Starts worker 0.
2. For each kill `j` from 1 to `kills`: waits until the workers' completed count reaches `orders × j / (kills + 1)` (whole-number division), and, from the second kill on, until the previous kill's orphans have all completed, so that each kill's recovery is measured on its own. Then it **kills the worker's process outright** (`Process.Kill(entireProcessTree: true)`; `SIGKILL` on Linux and macOS, `TerminateProcess` on Windows), noting the time on the wall clock just before. Once the process has exited, it lists the threads the store has `running` (the orphans), with the store's thread listing, 1,000 to a page, and starts the next worker at once.
3. Watches the orphans of each kill until all have completed.
4. When the last worker exits, checks everything below.

### Measured

A row per kill:

| Key | What it is |
|:--|:--|
| `kill` | 1, 2, ... |
| `completed_at_kill` | Orders completed when the worker was killed: the count it last reported |
| `orphans` | Threads the store had `running` just after the kill |
| `first_recovered_ms` | From the kill to the first orphan completed: the earliest time a worker reported for one of the kill's orphans |
| `all_recovered_ms` | From the kill to the last orphan completed, the same way |

Summary:

| Key | What it is |
|:--|:--|
| `orders` | Orders run |
| `kills` | Workers killed |
| `lost` | Orders whose thread isn't `completed` at the end. **Must be 0** |
| `duplicate_charges` | Orders with more than one ledger row. **Must be 0** |
| `charges` | Ledger rows. Must equal `orders` |
| `charge_attempts` | `Charge` runs in `stress_storm_attempts` |
| `recovered_node_runs` | Node runs with `is_recovery` true: the "at least once" made visible |
| `lease_ttl_ms` | The TTL, which bounds `first_recovered_ms` from below |
| `wall_s` | From worker 0's start to the last worker's exit |
| `orders_per_s` | `orders / wall_s` |
| `timer_late_p50_ms`, `timer_late_p99_ms` | As in scenario 1, over every model call of every worker, killed ones included: each worker sends the driver its calls' figures as it goes |

### Checks

- `lost` is 0: every order's thread is `completed`, with exactly one `completed` checkpoint.
- `duplicate_charges` is 0 and `charges` is `orders`.
- Every order's `Charge` runs, in `stress_storm_attempts`, used one idempotency key: a re-run used the original's.
- Every order's final `charge_id` is the ledger's for that order.
- Every kill left at least one orphan. A kill that left none didn't land mid-storm, and the run didn't test what it claims.
- No lease is left held at the end.

### Reference

`strata-demos-dotnet` and `strata-demos-rust` demo 20 show one crash and one recovery, with a fake clock. This scenario runs hundreds at once, with real time and real kills. It is a small version of StrataOps' chaos suite.

## 4. Durable firehose

**Claim.** Many concurrent durable runs, each checkpointing after every node, write tens of thousands of checkpoints a second to Postgres, and group commit lifts that further the more runs there are.

### Graph and state

Graph `stress-firehose`, version `1`, compiled with the checkpoint policy `EveryNode`: ten async nodes in a line, `N0` to `N9`. Node `Ni` is model call `i` and sets `step = i + 1`. A run writes eleven checkpoints: one before `N0`, one after each node but the last, and `completed` (the same count as the benchmarks' durable run).

State (JSON field names): `run` (unsigned integer), `step` (integer), `payload` (string: the same 1,024 characters as scenario 2's, from `hash(seed, run, 1000 + k)`).

**The run number** is `n × concurrency + worker`, counted from 0 in each configuration and repeat, where `worker` is the worker's number (0 to `concurrency − 1`) and `n` counts its runs from 0. It is the state's `run`, the payload's and the latency's `run`, so a worker draws the same latencies in both engines, and every configuration the same ones.

Thread ids: `firehose-{config}-{repeat}-{worker}-{n}`, where `config` is the configuration's position in the order below (0, 1, ...), `repeat` counts from 0, and `n` counts the worker's runs from 0.

Store: Postgres, group commit **off** (`GroupCommit` unset) or **on** with its defaults (256 saves a flush, 4 flushes at once), deltas and offload off, the connection pool at its default size (recorded in `parameters`).

### Latency

Each model call: `2–10 ms`: a step of a fast agent, a tool call, a small model. A run's handlers wait about 60 ms in all, so the checkpoints' cost shows against something.

### Sizes

| Option | Full | Quick |
|:--|:--|:--|
| `--concurrency` (runs at once) | `16,64,256,1024` | `16` |
| `--group-commit` | `off,on` | `off,on` |
| `--repeats` | `3` | `1` |
| `--warmup-s` | `5` | `0` |
| `--measure-s` | `20` | `2` |

`parameters`: `concurrency` (the list), `group_commit` (the list, such as `["off", "on"]`), `repeats`, `warmup_s`, `measure_s`, `latency_min_ms`, `latency_max_ms`, `postgres_version`, `pool_size`, `group_commit_max_batch` and `group_commit_max_flushes` (present when `group_commit` lists `on`), and `checkpoints_per_s_by_repeat`: one object per configuration, in their order, with `concurrency`, `group_commit` and `checkpoints_per_s` (a list, one per repeat). In this scenario `group_commit` is the list, not one of the strings [Services](#services) gives it.

### Procedure

A configuration is one concurrency with group commit off or on. Their order is each concurrency in the order `--concurrency` gives, and for each, group commit in the order `--group-commit` gives: `16 off`, `16 on`, `64 off`, and so on. The scenario runs every configuration once, in that order, then every configuration again, `--repeats` times in all, so that a drift of the machine spreads across configurations instead of landing on one.

For each configuration and repeat:

1. Empty the store's tables. Open a store with the configuration's group commit.
2. Start `concurrency` workers, each running one run after another, a new thread each time, until told to stop (a closed loop).
3. After `--warmup-s`, count the store's checkpoint rows (`SELECT count(*) FROM checkpoints`) and note the time just before the count was sent. After `--measure-s` more, count them again the same way. Then stop the workers and let their runs finish.
4. A run counts toward the latency figures (`run_*`, `checkpoint_cost_*`, `timer_late_*`) if it started and finished inside the window, and toward `runs_per_s_median` if it finished inside it.
5. Check what the store holds (see [Checks](#checks-4)) before the next configuration empties it.

### Measured

A row per configuration:

| Key | What it is |
|:--|:--|
| `concurrency` | Runs at once |
| `group_commit` | `off` or `on` |
| `repeats` | Repeats measured |
| `checkpoints_per_s_min`, `checkpoints_per_s_median`, `checkpoints_per_s_max` | Rows the window added, divided by its length, across the repeats |
| `runs_per_s_median` | Runs completed in the window a second, the repeats' median |
| `run_p50_ms`, `run_p99_ms` | A run's time, over every repeat's runs together |
| `checkpoint_cost_p50_ms`, `checkpoint_cost_p99_ms` | A run's time minus its ten measured waits: what its eleven checkpoints and the engine cost it |
| `timer_late_p50_ms`, `timer_late_p99_ms` | As in scenario 1 |

`parameters` also has every repeat's own `checkpoints_per_s`, per configuration, so the range can be checked.

### Checks

- Every run completed.
- Every completed thread has exactly eleven checkpoints, the last `completed` with `step` 10.
- The store holds no other checkpoint: its `checkpoints` table has eleven rows for each run started, and no more.

The second reads each thread's log through the store (`GetLogAsync`, `get_log`), after each configuration and repeat; the third counts the table's rows.

### Reference

`benchmarks/RESULTS.md`, 64 workers each running the same ten-node line with no latency, against Postgres: 8,860 checkpoints a second in .NET without group commit and 15,438 with it; 6,952 and 19,527 in Rust. It also reports that the .NET group commit varies from run to run (5,900 to 16,200 saves a second with 64 writers, cause unknown), which is why this scenario repeats and reports a range ([STR-100](https://datalium.atlassian.net/browse/STR-100)).

## What must be the same in both engines

The workload, so that the results compare:

- the graphs: their ids and versions, node names, edges, parallel group, boundary and checkpoint policy;
- the state's JSON field names and values, and the thread ids;
- the latency function, the call numbers and the latency ranges;
- the sizes, the warm-ups, the procedures and the order of the configurations;
- the Postgres tables the scenarios own, and the store settings;
- the percentile method, the memory samples' moments, and the JSON keys of `parameters`, `rows` and `summary`;
- the checks.

What may differ: the code, the engine's idioms (a `ValueTask` or a `Future`), `engine_specific`, and the runtime's own settings in [Build and runtime](#build-and-runtime).

## Open questions

To settle after the first measured run of scenario 1 ([STR-97](https://datalium.atlassian.net/browse/STR-97)), and the first of each other scenario:

| Question | Draft answer |
|:--|:--|
| The final sizes and latencies | The tables above, measured first on the `RESULTS.md` machine; cut a size that takes over about ten minutes, raise one that ends in seconds |
| Scenario 2's listing time | Listing in the Postgres store at `0.9.0-preview.2` costs each page every matching thread past the cursor ([datalium/strata#38](https://github.com/datalium/strata/issues/38)), so listing a million threads takes minutes and a full run goes well over ten. Keep a million: the slow listing is a finding, and the scenario's README says so. Measure again once the store is fixed |
| Windows timers | If one engine's timers wake much later than the other's (`timer_late_*`), measure both on Linux as well, on the same machine, and report both |
| More than one worker in scenario 3 | One worker at a time in version 1. Several, contending for leases, is the stronger demo; add it if version 1 is too easy |
| A Redis row in scenario 4 | Redis has no group commit (`benchmarks/RESULTS.md`). A row without it, as a reference, if the video needs one |
| Model calls through `Datalium.Strata.AI` / `strata-ai` | Not in version 1: in .NET it would measure `Microsoft.Extensions.AI` too. A fifth scenario could show the AI layer's cost on its own |

## Changes

| Version | Date | Change |
|:--|:--|:--|
| 0.1 | 2026-11-14 | First draft |
| 0.2 | 2026-11-14 | Scenario 1: a run that completes under `Boundaries` writes no checkpoint, so the burst writes nothing to the store and its overhead has no final checkpoint. A fourth row of 1,000,000 runs, since the first three took seconds. Its `parameters` keys, and `--control-runs 0`. What `timer_late_*` includes: the handler's wait for a thread after its timer fires. Open questions on how scenario 1 starts a run, and on scenario 2's policy (for the same reason as the first change) |
| 0.3 | 2026-11-14 | Scenario 1 hands each run to the thread pool (`Task.Run`, `tokio::spawn`), so both engines start runs the same way and the control row uses every core. Scenario 2 keeps `Boundaries` and checks the resumes' results, not the store, for the resolution; the store check is `resumed` threads. Both open questions are closed |
| 0.5 | 2026-11-15 | Scenario 3: the store (group commit off), no warm-up, its `parameters` keys. Each node appends its attempt first, before its model call. The worker's recovery passes don't overlap, what its completed count counts, and it reports when each recovery completed. A kill waits for the previous kill's orphans as well, the orphans are listed once the process has exited, and how `completed_at_kill` and the recovery times are measured. `timer_late_*` in its summary, as the common rules ask. `--orders` from 20,000 to 200,000: in the first measured runs, `RecoverStaleAsync` recovered a kill's 150 orphans one at a time, at a run's latency under load (about 300 ms, Postgres-bound), so a recovery took 20 to 40 s ([datalium/strata#39](https://github.com/datalium/strata/issues/39)), and with 20,000 orders the next worker finished every order before the next kill could land |
| 0.6 | 2026-11-15 | Scenario 4: the run number (`n × concurrency + worker`, from 0 in each configuration and repeat), what `config` is in a thread id, the order of the configurations, and its `parameters` keys, where `group_commit` is the list. How the window counts rows, which runs `runs_per_s_median` and the latency figures count, that the store is checked before the next configuration empties it, and a third check, that the store holds no other checkpoint. `--repeats` from 5 to 3 and `--measure-s` from 30 to 20: in the first three measured runs a full run took 24.5 minutes, and the five repeats of each configuration agreed to within 10% at most configurations |
| 0.4 | 2026-11-14 | Scenario 2: the warm-up's thread ids, and the last check counts the warm-up's `resumed` threads apart from the sleepers. Its `parameters` keys, and the store's keys every Postgres scenario shares. No `timer_late_*`, and why. Pages of 1,000, the four tables the store's size sums, which checkpoint `checkpoint_bytes` measures, and phase 2's memory sample after it lets go of its list. The checks list through the store's listing. The `tmpfs` question is closed: the first full run's store fit (scenario 2's README has the run). A new open question on the listing's time |
