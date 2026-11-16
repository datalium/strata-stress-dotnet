# 3. Kill it mid-storm

**Claim.** A worker process killed while hundreds of runs are in flight loses no run and charges nothing twice. Another worker finds what the dead one left, once its leases run out, and finishes it, at least once and charged once.

The spec is [SCENARIOS.md, "3. Kill it mid-storm"](../../SCENARIOS.md#3-kill-it-mid-storm). In short:

- **The graph.** `stress-storm` v1 is `Validate`, `Charge`, `Ship`, compiled with `CheckpointPolicy.EveryNode`: a `running` checkpoint before the first node and after every node, and `completed` at the end. Each node makes one model call: a simulated wait of 5–20 ms, seeded so the Rust twin waits the same.
- **The charge.** `Charge` charges the order in a Postgres ledger after its model call, under the node's `NodeContext.IdempotencyKey` (`order-000042/Charge#2`). The ledger deduplicates by that key, as a real payment API does. A kill can land between the charge and the node's checkpoint, and then the recovery runs `Charge` again with the same key. Every node also appends a row to `stress_storm_attempts` before its model call, so every node run, from every worker, shows.
- **The workers.** Each is a child process, started from this binary in `worker k` mode. It runs every order under a lease (owner `worker-k`, TTL 2 s), at most 256 at once, and skips any order that already has a checkpoint. Every 250 ms it calls `RecoverStaleAsync` under its own lease, one pass at a time, which recovers each `running` thread that no live lease holds.
- **The driver.** The scenario's own process. It never runs a graph. It starts worker 0, and at a quarter, a half and three quarters of the orders kills the worker outright (`Process.Kill(entireProcessTree: true)`, `TerminateProcess` on Windows). Then it lists the threads the dead worker left `running` (the orphans) and starts the next worker at once. Before each kill after the first, it waits for the previous kill's orphans to complete, so each kill's recovery is measured on its own.
- **The store.** Postgres, group commit off (the store's default), deltas and offload off.

## Run it

```bash
docker compose up -d --wait                                         # Postgres, on tmpfs
dotnet run -c Release --project scenarios/03-kill-mid-storm            # full: 200,000 orders, 3 kills
dotnet run -c Release --project scenarios/03-kill-mid-storm -- --quick # 300 orders, 1 kill, for CI; measures nothing
```

It needs Postgres, and says how to start it if it can't reach it (exit code 2). `STRATA_STRESS_POSTGRES_URL` points it at another one, but **it starts by dropping Strata's checkpoint store from that database**, the same tables any Strata app keeps there, and its own two tables, so use one you can lose.

| Option | Full | Quick |
|:--|:--|:--|
| `--orders` | `200000` | `300` |
| `--kills` | `3` | `1` |
| `--in-flight` (runs at once, per worker) | `256` | `32` |
| `--lease-ttl-ms` | `2000` | `2000` |

`--seed N` changes every latency, and `--json PATH` says where the JSON goes (default `artifacts/results/03-kill-mid-storm.json`). The exit code is 0 when every check passes, 1 when one fails, and 2 for a bad option or a Postgres it can't reach. A worker that completes nothing for two minutes is stopped, and the checks report what it left.

## What the figures mean

- **A row per kill.** `completed_at_kill` is the count the worker last reported, at most 100 ms old. `orphans` are the threads the store had `running` once the killed process had exited. `first_recovered_ms` and `all_recovered_ms` run from the kill to the first and the last orphan completed. The worker that recovers an orphan notes the moment its recovery returned, on the wall clock both processes share, and reports it to the driver.
- **`lost` and `duplicate_charges`.** Orders whose thread isn't `completed` at the end, and orders with more than one ledger row. Both must be 0.
- **`charge_attempts` and `recovered_node_runs`.** Every `Charge` run in `stress_storm_attempts`, and every node run with `IsRecovery` set: "at least once", made visible. `IsRecovery` is set only on the node a recovery starts with, so `recovered_node_runs` is about one per orphan.
- **`wall_s` and `orders_per_s`.** From worker 0's start to the last worker's exit, the recoveries' waits included.
- **`timer_late_p50_ms` and `timer_late_p99_ms`.** How late each model call's timer woke, over every call of every worker. A killed worker's last 100 ms of calls don't reach the driver.

The JSON adds, under `engine_specific`, what the recovery and the listings cost: the workers' `RecoverStaleAsync` passes (how many, their median and longest, and those that recovered nothing), how many threads the passes recovered and skipped, and how long each of the driver's orphan listings took.

## Real runs

Three full runs, on 2026-11-15 between 15:36 and 15:48 UTC, with version 0.5 of the spec. The same build, the same machine and the same Postgres, emptied by each run:

- **Machine.** A desktop with an Intel Core i9-12900KF (8 performance and 8 efficiency cores, 24 logical) and 128 GB of RAM, running Windows 11 Pro 10.0.26300. It had the usual desktop apps open, so it wasn't a quiet benchmark machine.
- **Software.** .NET 10.0.15 with server GC, Strata `0.9.0-preview.2` from nuget.org. Postgres 16.15 (`postgres:16-alpine`) on tmpfs, in Docker Desktop 29.9.3's Linux VM with 62.7 GiB of memory, on the same machine.

| Figure | Run 1 | Run 2 | Run 3 | Range |
|:--|--:|--:|--:|:--|
| Orphans, kills 1 / 2 / 3 | 154 / 158 / 152 | 171 / 148 / 174 | 160 / 142 / 151 | 142–174 |
| `first_recovered_ms`, kills 1 / 2 / 3 | 2,378 / 2,362 / 2,425 | 2,415 / 2,333 / 2,441 | 2,400 / 2,471 / 2,391 | 2,333–2,471 |
| `all_recovered_ms`, kills 1 / 2 / 3, in s | 40.5 / 41.5 / 40.1 | 53.3 / 37.2 / 63.5 | 41.0 / 38.9 / 38.8 | 37–64 s |
| `lost` | 0 | 0 | 0 | 0 |
| `duplicate_charges` | 0 | 0 | 0 | 0 |
| `charge_attempts` (for 200,000 charges) | 200,152 | 200,170 | 200,153 | |
| `recovered_node_runs` | 464 | 493 | 453 | one per orphan |
| `orders_per_s` | 855 | 894 | 878 | 855–894 |
| `timer_late_p50_ms` / `p99` | 0.83 / 11.6 | 0.75 / 11.2 | 0.92 / 11.5 | |
| Idle recovery pass, median (`engine_specific`) | 2,209 ms | 2,052 ms | 2,167 ms | 2.1–2.2 s |
| Orphan listing, kills 1 / 2 / 3 (`engine_specific`) | 137 / 74 / 91 ms | 129 / 92 / 102 ms | 111 / 87 / 84 ms | 74–137 ms |
| Checks | 6 of 6 | 6 of 6 | 6 of 6 | |
| Whole run | 4m11s | 4m01s | 4m04s | 4.0–4.2 min |

The first, as it printed:

```text
## 03-kill-mid-storm: Strata 0.9.0-preview.2, .NET 10.0.15, 12th Gen Intel(R) Core(TM) i9-12900KF, 24 logical cores

| Kill | Completed at kill | Orphans | First recovered ms | All recovered ms |
|---:|---:|---:|---:|---:|
| 1 | 50,043 | 154 | 2,378 | 40,481 |
| 2 | 100,022 | 158 | 2,362 | 41,491 |
| 3 | 150,089 | 152 | 2,425 | 40,128 |

- `orders`: 200,000
- `kills`: 3
- `lost`: 0
- `duplicate_charges`: 0
- `charges`: 200,000
- `charge_attempts`: 200,152
- `recovered_node_runs`: 464
- `lease_ttl_ms`: 2,000
- `wall_s`: 234
- `orders_per_s`: 855
- `timer_late_p50_ms`: 0.83
- `timer_late_p99_ms`: 11.6

Checks: 6 passed, 0 failed.
- PASS no order lost: every thread completed, with exactly one completed checkpoint
- PASS no order charged twice, and every order charged
- PASS every order's Charge runs used one idempotency key, the ledger's
- PASS every order's final charge_id is the ledger's
- PASS every kill left at least one orphan
- PASS no lease left held
```

**[RESULTS.md](../../RESULTS.md) replaces these figures.** It has this scenario's full runs of 2026-11-16, on Windows and in a Linux container on the same machine, with the same code. The runs above are the ones the scenario was built and sized with.

## What it shows

- **Nothing lost, nothing charged twice: the claim holds.** In nine kills across three runs, each with 142 to 174 threads in flight, every one of the 200,000 orders ended `completed` with exactly one `completed` checkpoint and one ledger row. Every re-run of `Charge` used the original's idempotency key, every final `charge_id` was the ledger's, and no lease was left held.
- **The case the claim is about happened, many times.** A kill that lands after the ledger write and before `Charge`'s checkpoint is the one that would charge twice without the key. The spec's tables don't record when a charge was made, so a query run by hand on run 3's tables, after the run, compared each ledger row's transaction id (Postgres's `xmin`) with that of the recovery's `Charge` attempt. For 94 of the 167 orphans whose recovery started at `Charge`, the ledger row came first: the dead worker had charged, and the recovery sent the same key and got that charge back. For the other 73, the dead worker died before charging. Runs 1 and 2 weren't queried.
- **At least once, made visible.** Recoveries started at `Validate` 156 times, at `Charge` 167 times and at `Ship` 130 times in run 3. `recovered_node_runs` equals the orphans in every run, so no live worker lost a lease to a recovery: every recovered thread was a dead worker's. `charge_attempts` exceeds 200,000 by the orphans whose `Charge` attempt row was written before the kill: 153 of run 3's 167 that recovered at `Charge`; the other 14 died before their attempt row committed.
- **The first orphan completes a fraction of a second after the leases run out.** 2.33 to 2.47 s after the kill, with a 2 s TTL: the lease's expiry, up to 250 ms to the next recovery pass, and the remaining nodes.
- **Recovering them all is slow: one thread at a time, at a run's speed under load** ([datalium/strata#39](https://github.com/datalium/strata/issues/39)). `RecoverStaleAsync` recovers the threads it finds one after another, and each recovery is a run like any other, at a run's latency while the new worker has 256 orders in flight: about 290 to 300 ms (256 in flight at 855 to 894 orders a second). So 142 to 174 orphans took 37 to 64 s, about 250 to 350 ms each. With 20,000 orders, the spec's first size, the next worker finished every order before the first kill's orphans were recovered, so a second kill found nothing in flight; the spec went to 200,000 for that reason. Two recoveries, run 2's kills 1 and 3 (53 and 64 s), were slower than the others (37 to 41 s), and why isn't known. An application that needs faster recovery would run several recovery loops at once, which the spec doesn't do: their conditional claims keep two loops from recovering the same thread.
- **The storm is Postgres-bound, not model-bound.** A run's model calls wait about 37 ms in all (three of 5–20 ms, with timers that woke 0.75 to 0.92 ms late at the median and 11 to 12 ms at the 99th percentile), yet a run takes about 290 to 300 ms. The rest is its eleven or so round trips to Postgres (the lease, four checkpoints, three attempts, the charge, the release, the start's check for a checkpoint), each a transaction with group commit off, and their wait for one of the pool's 100 connections.
- **Each recovery pass checks every live thread before it gets to the orphans** (#39 too). It lists `running` threads newest first, so the live worker's 256 threads come before the dead one's. It loads each one and reads its lease before skipping it. A pass that recovered nothing took 2.1 to 2.2 s at the median, so the sweep, not the 250 ms interval, sets the pace between passes.
- **Listing the running threads reads every checkpoint in the store** ([datalium/strata#38](https://github.com/datalium/strata/issues/38)). The driver listed each kill's orphans in 74 to 137 ms, with no trend from 50,000 orders to 150,000. On run 3's final store (200,000 threads, 800,453 checkpoints, 401 MB), `EXPLAIN ANALYZE` of the listing `RecoverStaleAsync` makes, with 150 threads marked `running` in a transaction that was rolled back, showed why: the join's collation mismatch makes Postgres scan all 800,453 checkpoints to find 150 rows (45 ms). With the join's collation matched, it used the primary key and took 1 ms. Here that is a few percent of a pass, which the per-thread checks dominate. It grows with the store, though, so a store that keeps its history pays it on every pass. The scenario doesn't purge, so the purge's own mismatch doesn't arise here.

## What it doesn't show

- **Several workers at once.** One worker runs at a time, and the next starts only after the kill. Several workers contending for leases is the stronger demo, and an open question in the spec.
- **A store on disk.** Postgres keeps its data on tmpfs, so no fsync sets the pace. On a disk, every checkpoint costs a commit's fsync, and the storm is slower.
- **A crash of Postgres or of the machine.** The worker dies; the database and the machine don't. What a commit in flight at the kill does is Postgres's: it lands or it doesn't, and the lease fencing makes either safe.
- **A slow worker taken for a dead one.** A live worker's leases are renewed every third of the TTL. A worker too slow to renew loses its threads to the recovery, which is at-least-once too, but no run here was slow enough to show it.
- **Linux on bare metal.** These runs were on Windows, against a Postgres in a Linux VM. [RESULTS.md](../../RESULTS.md) also measures the scenario in a Linux container on the same machine: the claim holds there too, and the storm is 1.5 times faster.
