# 4. Durable firehose

**Claim.** Many concurrent durable runs, each checkpointing after every node, write tens of thousands of checkpoints a second to Postgres, and group commit lifts that further the more runs there are.

The spec is [SCENARIOS.md, "4. Durable firehose"](../../SCENARIOS.md#4-durable-firehose). In short:

- **The graph.** `stress-firehose` v1 is ten async nodes in a line, `N0` to `N9`, compiled with `CheckpointPolicy.EveryNode`. A run writes eleven checkpoints: `running` before `N0` and after `N0` to `N8`, then `completed` after `N9`. Node `Ni` is model call `i`, a simulated wait of 2–10 ms, seeded so the Rust twin waits the same, and sets `step = i + 1`. The state carries a 1,024-character payload that doesn't compress, so a checkpoint is about the benchmarks' size.
- **The closed loop.** `concurrency` workers, each running one run after another, a new thread each time. Worker `w`'s `n`th run is run number `n × concurrency + w`, which seeds its latencies, so every configuration draws the same ones.
- **The configurations.** 16, 64, 256 and 1,024 runs at once, each with group commit off (the store's default) and on (its defaults: up to 256 saves a flush, 4 flushes at once), in that order: `16 off`, `16 on`, `64 off`, and so on. All eight once, then all eight again, three times in all, so a drift of the machine spreads across them.
- **The window.** Each configuration starts from an empty store, runs 5 s to warm up, then 20 s measured: the checkpoints a second are the rows `SELECT count(*) FROM checkpoints` counts at the window's end, minus those at its start, over the time between the two counts.
- **The store.** Postgres, deltas and offload off, the connection pool at Npgsql's default of 100. No leases: a run's eleven saves are its only round trips.

## Run it

```bash
docker compose up -d --wait                                            # Postgres, on tmpfs
dotnet run -c Release --project scenarios/04-durable-firehose            # full: 8 configurations × 3 repeats, about 11 min
dotnet run -c Release --project scenarios/04-durable-firehose -- --quick # 16 at once, off and on, 2 s each, for CI; measures nothing
```

It needs Postgres, and says how to start it if it can't reach it (exit code 2). `STRATA_STRESS_POSTGRES_URL` points it at another one, but **it drops Strata's checkpoint store from that database before every configuration**, the same tables any Strata app keeps there, so use one you can lose. It leaves the last configuration's tables in place, to be looked at.

| Option | Full | Quick |
|:--|:--|:--|
| `--concurrency` (runs at once) | `16,64,256,1024` | `16` |
| `--group-commit` | `off,on` | `off,on` |
| `--repeats` | `3` | `1` |
| `--warmup-s` | `5` | `0` |
| `--measure-s` | `20` | `2` |

`--seed N` changes every latency, and `--json PATH` says where the JSON goes (default `artifacts/results/04-durable-firehose.json`). The exit code is 0 when every check passes, 1 when one fails, and 2 for a bad option or a Postgres it can't reach.

## What the figures mean

A row per configuration, over its three repeats:

- **`checkpoints_per_s_min`, `_median`, `_max`.** The rows the window added to `checkpoints`, divided by its length, across the repeats. `parameters.checkpoints_per_s_by_repeat` has each repeat's own. With three repeats the median is the second; with an even number, nearest rank takes the lower of the two middle ones.
- **`runs_per_s_median`.** Runs that finished inside the window, a second, the repeats' median.
- **`run_p50_ms`, `run_p99_ms`.** A run's time, from just before `RunAsync` to its return, over the runs that started and finished inside the window, every repeat's together.
- **`checkpoint_cost_p50_ms`, `_p99_ms`.** A run's time minus its ten measured waits: what its eleven checkpoints and the engine cost it. Under load, most of it is a save's wait: for a connection from the pool with group commit off, for a flush with it on.
- **`timer_late_p50_ms`, `_p99_ms`.** How late each of those runs' model calls woke: its measured wait minus the latency it asked for (see SCENARIOS.md, "The simulated model").

The JSON adds, under `engine_specific.by_repeat`, one object per configuration and repeat, in the order they ran: how long each count took (it is a sequential scan of the table), what the store's own telemetry (`Datalium.Strata.Storage`'s meter) counted in the window (saves committed, group commit's flushes, their mean and largest size, how long saves waited for one, saves repeated after a transient failure), the process's CPU and the collections in the window, the store's size at the end, and how long the checks took. The store's count of saves committed is a cross-check on the table's count: the two agree to within a percent, the difference being the saves committed between a count's snapshot and the telemetry's read.

## Real runs

Three full runs, on 2026-11-15 between 22:06 and 22:38 UTC, with version 0.6 of the spec (three repeats of 20 s), on the same machine and the same Postgres, emptied before every configuration:

- **Machine.** A desktop with an Intel Core i9-12900KF (8 performance and 8 efficiency cores, 24 logical) and 128 GB of RAM, running Windows 11 Pro 10.0.26300. It had the usual desktop apps open, so it wasn't a quiet benchmark machine.
- **Software.** .NET 10.0.15 with server GC, Strata `0.9.0-preview.2` from nuget.org. Postgres 16.15 (`postgres:16-alpine`) on tmpfs, in Docker Desktop 29.9.3's Linux VM (24 CPUs, 62.7 GiB), on the same machine.

Checkpoints a second, each run's median of three repeats, and the lowest and highest of all nine:

| Runs at once | Group commit | Run 1 | Run 2 | Run 3 | All 9 repeats | On ÷ off, medians |
|---:|:--|--:|--:|--:|:--|:--|
| 16 | off | 1,383 | 1,378 | 1,382 | 1,354–1,397 | |
| 16 | on | 1,225 | 1,223 | 1,258 | 1,219–1,261 | 0.89–0.91 |
| 64 | off | 6,178 | 6,272 | 5,833 | 5,809–6,283 | |
| 64 | on | 4,027 | 3,886 | 3,706 | 3,641–4,429 | 0.62–0.65 |
| 256 | off | 7,204 | 7,191 | 6,990 | 6,979–7,295 | |
| 256 | on | 6,565 | 6,565 | 6,968 | 6,459–7,291 | 0.91–1.00 |
| 1,024 | off | 7,048 | 7,172 | 6,929 | 6,605–7,195 | |
| 1,024 | on | 17,939 | 17,979 | 17,433 | 17,348–18,110 | 2.51–2.55 |

| Figure | Range over the three runs |
|:--|:--|
| Run P50, off: 16 / 64 / 256 / 1,024 at once | 127–128 / 112–116 / 383–391 / 1,550–1,589 ms |
| Run P50, on: 16 / 64 / 256 / 1,024 at once | 141–144 / 157–176 / 402–433 / 630–650 ms |
| Checkpoints' cost P50, off | 35–36 / 53–56 / 315–322 / 1,482–1,520 ms |
| Checkpoints' cost P50, on | 38 / 62–90 / 304–330 / 523–543 ms |
| Timer late P50, off / on | −0.35 to 2.52 / 2.22 to 4.66 ms |
| Group commit's mean flush, 16 / 64 / 256 / 1,024 at once (`engine_specific`) | 2.1–2.3 / 4.5–5.2 / 11.7–12.1 / 98–109 saves |
| A save's mean wait for its flush to start | 1.0–1.4 ms, 5.0–5.7 ms at 1,024 |
| The process's CPU in the window, off / on | 0.6–0.8 at 16 and 2.2–2.5 from 64 up / 0.2–0.6 cores |
| Saves repeated after a transient failure | 0 |
| Checks | 3 of 3 in each run |
| Whole run | 10.5–10.7 min |

The third, as it printed:

```text
## 04-durable-firehose: Strata 0.9.0-preview.2, .NET 10.0.15, 12th Gen Intel(R) Core(TM) i9-12900KF, 24 logical cores

| Runs at once | Group commit | Checkpoints/s (min–max) | Runs/s | Run P50 ms | Run P99 ms | Checkpoints' cost P50 ms | Checkpoints' cost P99 ms | Timer late P50 ms | Timer late P99 ms |
|---:|:---|---:|---:|---:|---:|---:|---:|---:|---:|
| 16 | off | 1,382 (1,377–1,383) | 126 | 127 | 160 | 35.1 | 52.0 | 2.44 | 12.3 |
| 16 | on | 1,258 (1,224–1,261) | 114 | 141 | 161 | 38.1 | 50.2 | 4.37 | 10.6 |
| 64 | off | 5,833 (5,813–6,275) | 530 | 116 | 169 | 55.7 | 103 | -0.27 | 6.36 |
| 64 | on | 3,706 (3,641–4,185) | 337 | 176 | 339 | 89.9 | 232 | 2.22 | 11.9 |
| 256 | off | 6,990 (6,979–7,295) | 637 | 391 | 512 | 322 | 437 | 0.25 | 9.04 |
| 256 | on | 6,968 (6,710–7,291) | 634 | 402 | 651 | 304 | 527 | 2.54 | 13.9 |
| 1,024 | off | 6,929 (6,605–7,168) | 631 | 1,589 | 2,011 | 1,520 | 1,935 | 0.28 | 9.42 |
| 1,024 | on | 17,433 (17,348–17,546) | 1,587 | 650 | 819 | 543 | 707 | 3.79 | 14.1 |

Checks: 3 passed, 0 failed.
- PASS every run completed
- PASS every completed thread has exactly eleven checkpoints, the last completed with step 10
- PASS the store holds no other checkpoint: eleven rows for each run started
```

**Before the sizes were cut.** Three earlier full runs the same day (20:19 to 21:41 UTC), at the first draft's five repeats of 30 s, took 24.5 to 24.7 minutes each and gave the same picture. Their medians, without and with group commit: 1,372–1,382 and 1,239–1,245 at 16 runs at once, 6,069–6,142 and 3,380–3,605 at 64, 6,715–7,303 and 6,480–6,954 at 256, 6,651–7,241 and 17,404–17,541 at 1,024. Their five repeats agreed to within 10% at most configurations, which is why the spec went to three of 20 s. They also tried to report the bytes allocated a run; the counter, read while hundreds of runs allocated, came out at zero or below, so it was removed.

**[RESULTS.md](../../RESULTS.md) replaces these figures.** It has this scenario's full runs of 2026-11-16, on Windows and in a Linux container on the same machine, with the same code. The runs above are the ones the scenario was built and sized with.

## What it shows

- **On Windows, the claim holds at 1,024 runs at once, and not below.** With group commit, 1,024 runs write 17,300 to 18,100 checkpoints a second, about 2.5 times what they write without it, and a run takes 630 to 650 ms at the median instead of 1.55 to 1.59 s. At 16, 64 and 256 runs at once, group commit is slower: by about 10% at 16, by 35 to 38% at 64, and by up to 9% at 256 (even in one run of three). "Tens of thousands of checkpoints a second" isn't reached either: 18,100 at best. This is the .NET engine's figure, on Windows; the store alone doesn't slow down with group commit on Linux (below), and the Rust twin's figure comes with STR-94.
- **Without group commit, Postgres stops at about 7,000 checkpoints a second.** From 16 runs to 64 the rate goes up 4.4 times, nearly in proportion, and then stays there at 256 and 1,024 (6,605 to 7,295 in every repeat), as `benchmarks/RESULTS.md` found ("Postgres stops scaling at about 7,000 saves a second"). Past that point a run's extra time is its saves queueing for one of the pool's 100 connections: at 1,024 runs, its eleven checkpoints cost it about 1.5 s, against 35 ms at 16.
- **Group commit gains only where saves pile up.** It flushes up to 4 batches at once, each on one connection, and a flush takes whatever saves are queued when it starts. At 1,024 runs about 100 are queued, and 4 connections do the work of 100. At 64 runs, with each run's save 2 to 10 ms after its last, a flush finds 4 or 5. Here, on Windows, 4 flushes at once then do less than 64 single saves on 64 connections; on Linux, with the store alone, they do as much (below). `benchmarks/RESULTS.md` measured 64 writers saving back to back, with no wait between saves, and found group commit faster (15,438 against 8,860); with the spec's latency, the same 64 are slower with it here.
- **Why the small flushes are that slow is not known** ([datalium/strata#40](https://github.com/datalium/strata/issues/40)). At 64 runs at once, flushes of 4 or 5 saves complete 756 to 891 times a second, at most 4 at a time: about 5 ms each if all four are always busy. A run's eleven checkpoints cost it 62 to 90 ms instead of 53 to 56 ms. Side experiments on the same machine, outside the scenario, narrowed it down without finding it. They used a scratch Postgres with `pg_stat_statements` and a small program of 64 writers saving to the store alone, without the engine, 2 to 10 ms apart, which reproduced it (3,000 to 3,300 saves a second with group commit, 5,400 to 5,700 without). Postgres executes a flush's statement in 0.43 ms on average (6.8 ms at most), its locks in 0.02 ms. A round trip to Postgres takes 0.25 ms, and a lone save 1.25 to 1.33 ms, with or without group commit. The process is mostly idle (0.2 to 0.6 cores with group commit, 2.2 to 2.5 without from 64 runs up), its thread pool has almost no queue, and keeping a core busy with a spinning thread changed nothing. With group commit on, the process's timers also wake later (2.2 to 4.7 ms at the median, against −0.4 to 2.5 off). So the time goes in the process, while it waits, on the .NET store's flush path or in Npgsql under it. `benchmarks/RESULTS.md` also reports that the .NET group commit varies from run to run, cause unknown; here it varies little, and is slow.
- **The slowdown needs .NET on Windows.** Later the same day, the same store-only program ran three times on Windows and three times in a Linux container (Ubuntu 24.04, the same .NET 10.0.15, in Docker Desktop's VM on the same machine). On Windows it saved 2,959 to 3,887 times a second with group commit and 5,382 to 5,787 without. In the container, reaching Postgres inside the VM, it saved 7,576 to 7,774 with group commit and 7,325 to 7,636 without. Reaching Postgres through the host's forwarded port, as the Windows process does, it saved 4,878 to 4,925 with group commit and 3,475 to 3,945 without. Its timers woke on time with group commit on (−0.5 to 0.3 ms at the median, against 2.8 to 3.4 on Windows). So the slowdown isn't the store's design, Postgres or the network path: it is something .NET does on Windows, cause still unknown. The whole scenario has since run in a Linux container on the same machine ([RESULTS.md](../../RESULTS.md)), and there group commit is level with none at 16 and 64 runs at once, and 2.1 and 4.2 times faster at 256 and 1,024.
- **A negative timer lateness is a wait that ended early.** `Task.Delay` takes whole milliseconds and drops the fraction, so a call that asks for 5.9 ms can wake after 4.9. At 64 runs without group commit the median wake was 0.3 ms early. The handlers charge the run with the wait they measured, so the checkpoints' cost isn't affected. A lone process waiting 5 ms on this machine took 15.5 ms at the median, Windows' default timer tick; under the scenario's load the timers woke far closer, so how late a timer wakes here depends on what else the process and the machine are doing.
- **Counting the rows doesn't disturb the window.** The count is a sequential scan of the table, up to about 450,000 rows (614 MB of `checkpoints` at the end of 1,024 runs with group commit), and took up to 220 ms. Postgres takes its snapshot when the count starts, just after the scenario notes the time, so its length doesn't move the window. The store's own count of saves committed, from its telemetry, agrees with the table's to within a percent in every repeat. The checks read every thread's log, up to 41,664 threads a configuration, in at most 2.6 s.

## What it doesn't show

- **A store on disk.** Postgres keeps its data on tmpfs, so a commit's fsync costs almost nothing. On a disk each commit waits for one, and group commit, which shares one commit among up to 256 saves, would gain much more than it does here, where what it saves is round trips and transactions.
- **Postgres's limits.** One Postgres, in a VM on the same machine as the runs, with its settings at the image's defaults (`max_connections` raised to 300). A tuned server, or one on its own machine, would take more.
- **Deltas, offload, leases.** All off, as the spec says. A run with leases makes more round trips; deltas would shrink each `running` row, which here holds the same 1 KiB payload eleven times.
- **The listing, a purge or a recovery.** The scenario never lists threads, purges or recovers, so the listing's collation mismatch ([datalium/strata#38](https://github.com/datalium/strata/issues/38)) and the sequential recovery ([datalium/strata#39](https://github.com/datalium/strata/issues/39)) don't arise here. Its checks read each thread's log by its primary key (`GetLogAsync`), and count the table's rows.
- **A real model's latency.** A run's waits sum to about 60 ms. Against a real model's seconds, the checkpoints' cost would be a smaller share of a run.
- **Linux on bare metal.** These runs were on Windows, against a Postgres in a Linux VM. [RESULTS.md](../../RESULTS.md) measures the scenario in a Linux container on the same machine as well, as SCENARIOS.md's open question on Windows timers asks: there the claim holds from 256 runs at once.
