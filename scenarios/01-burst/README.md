# 1. Burst

**Claim.** Thousands of runs started at the same moment, each fanning out to eight parallel model calls, complete with an engine overhead of microseconds a run. The run's time is the model's.

The spec is [SCENARIOS.md, "1. Burst"](../../SCENARIOS.md#1-burst). In short:

- **The graph.** `stress-burst` v1 is `Plan`, then `Research`, then `Answer`. `Research` is a parallel group of eight async branches, `b0` to `b7`, whose findings are merged in declared order.
- **The model.** Every node and branch makes one model call, ten a run: a simulated wait of 20–80 ms, seeded so the Rust twin waits the same.
- **The store.** The in-memory store with the default policy (`Boundaries`), under which a completed run writes nothing.
- **The procedure.** Each row hands every run to the thread pool at once (`Task.Run`), then waits for all of them. A control row with no latency at all shows the engine's own cost.

## Run it

```bash
dotnet run -c Release --project scenarios/01-burst            # full: 1,000, 10,000, 100,000 and 1,000,000 runs, then a 10,000-run control
dotnet run -c Release --project scenarios/01-burst -- --quick # 100 runs and a 100-run control, for CI; measures nothing
```

It needs no service. The full run takes about 25 seconds and peaks at 22–26 GiB of resident memory, in the 1,000,000-run row. On a smaller machine, leave that row out with `--runs 1000,10000,100000` (about 2.6 GiB).

| Option | Full | Quick |
|:--|:--|:--|
| `--runs` | `1000,10000,100000,1000000` | `100` |
| `--latency-ms` | `20-80` | `20-80` |
| `--control-runs` (latency 0; `0` leaves the row out) | `10000` | `100` |
| `--warmup` (runs before each row, not counted) | `1000` | `10` |

`--seed N` changes every latency, and `--json PATH` says where the JSON goes (default `artifacts/results/01-burst.json`). The exit code is 0 when both checks pass, 1 when one fails, and 2 for a bad option.

## What the columns mean

- **Run P50, P99, P99.9.** From the moment a run was handed to the thread pool to the moment its `RunAsync` returned. The model's latency alone puts its critical path at about 175 ms on average (`Plan`, the slowest of eight branches, `Answer`).
- **Overhead.** A run's time minus its critical path: `Plan`'s wait, plus the longest branch's wait, plus `Answer`'s wait, each as measured. It holds Strata's own work, the run's wait for the pool to start it, and the time a ready node or branch waited for a thread. When the process can't keep up, it is mostly queueing.
- **Timer late.** A model call's measured wait minus the latency it asked for. The wait ends when the handler runs again, so this is the timer's lateness plus the time the handler then waited for a thread. On Windows the timer alone is about 7 ms late at the median (its tick is 15.6 ms). Under load, waiting for a thread is most of it.
- **Start spread.** How long the loop took to hand every run to the pool: the first to the last.
- **Peak resident.** The highest resident memory sampled every 100 ms during the row, from a thread of its own. It is the process's, not the runs': it includes the garbage not yet collected, the server GC's heaps, and whatever earlier rows left behind.

The JSON adds `run_p90_ms`, `run_max_ms`, `overhead_max_us`, `timer_late_p50_ms`, `model_calls` and `model_calls_per_s`. Under `engine_specific` it adds each row's allocated bytes per run and gen 0, 1 and 2 collections.

## A real run

Measured on 2026-11-14, with version 0.3 of the spec:

- **Machine.** A desktop with an Intel Core i9-12900KF (8 performance and 8 efficiency cores, 24 logical) and 128 GB of RAM, running Windows 11 Pro 10.0.26300. It had the usual desktop apps open, so it wasn't a quiet benchmark machine.
- **Software.** .NET 10.0.15 with server GC, and Strata `0.9.0-preview.2` from nuget.org.

The fourth of four full runs, as it printed:

```text
## 01-burst: Strata 0.9.0-preview.2, .NET 10.0.15, 12th Gen Intel(R) Core(TM) i9-12900KF, 24 logical cores

| Runs | Model latency | Wall s | Runs/s | Run P50 ms | Run P99 ms | Run P99.9 ms | Overhead P50 µs | Overhead P99 µs | Overhead P99.9 µs | Timer late P99 ms | Start spread ms | Peak resident MiB |
|---:|:---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | 20–80 ms | 0.26 | 3,862 | 197 | 243 | 259 | 399 | 6,401 | 8,572 | 19.9 | 0.29 | 68.2 |
| 10,000 | 20–80 ms | 0.51 | 19,573 | 424 | 479 | 495 | 11,535 | 21,699 | 49,566 | 182 | 27.8 | 310 |
| 100,000 | 20–80 ms | 2.10 | 47,732 | 1,760 | 2,071 | 2,079 | 390,207 | 963,998 | 964,430 | 580 | 90.6 | 1,946 |
| 1,000,000 | 20–80 ms | 15.8 | 63,225 | 15,106 | 15,697 | 15,752 | 2,606,202 | 4,343,557 | 4,423,293 | 6,786 | 720 | 26,733 |
| 10,000 | none | 0.02 | 500,450 | 7.04 | 13.7 | 13.8 | 7,039 | 13,738 | 13,782 |  | 1.28 | 24,409 |

Checks: 2 passed, 0 failed.
- PASS every run completed
- PASS every answer is right
```

The four runs differ a lot. Their ranges, with the overhead in milliseconds throughout:

| Runs | Latency | Runs/s | Run P50 ms | Run P99.9 ms | Overhead P50 ms | Overhead P99 ms | Overhead P99.9 ms | Timer late P50 ms | Start spread ms | Peak resident GiB | Allocated a run |
|---:|:--|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | 20–80 ms | 3,624–3,862 | 187–199 | 247–261 | 0.40–0.50 | 5.2–8.8 | 8.6–10.4 | 6.7–7.2 | 0.14–0.29 | 0.06–0.07 | 30.9–31.0 KB |
| 10,000 | 20–80 ms | 18,479–26,054 | 307–455 | 383–540 | 1.6–11.5 | 14–26 | 44–77 | 72–146 | 22–28 | 0.27–0.31 | 31.2–32.1 KB |
| 100,000 | 20–80 ms | 41,723–58,089 | 1,040–2,167 | 1,629–2,280 | 390–628 | 758–1,667 | 761–1,667 | 73–406 | 77–100 | 1.2–2.6 | 31.2–31.7 KB |
| 1,000,000 | 20–80 ms | 55,424–66,862 | 14,353–16,180 | 14,834–17,930 | 1,412–5,541 | 2,394–6,513 | 2,400–6,554 | 3,389–5,844 | 720–796 | 21.9–26.1 | 31.1–31.3 KB |
| 10,000 | none | 500,450–721,600 | 6.0–7.0 | 12.7–13.8 | 6.0–7.0 | 12.6–13.7 | 12.7–13.8 | | 1.2–1.3 | 20.3–25.0 | 18.9 KB |

**[RESULTS.md](../../RESULTS.md) replaces these figures.** It has this scenario's full runs of 2026-11-16, on Windows and in a Linux container on the same machine, with the same code. The runs above are the ones the scenario was built and sized with.

## What it shows

- **A thousand runs at once: the claim holds.** A run's median time is 187–199 ms, the model's: a critical path of about 175 ms, plus Windows' timer waking each call about 7 ms late. The median overhead is 0.40–0.50 ms, the wait for the pool included, and the P99.9 is 8.6–10.4 ms.
- **Ten thousand: runs start to queue.** The median run takes 307–455 ms. After its timer fires, a handler waits 72–146 ms at the median for a thread to resume it ("Timer late").
- **A hundred thousand and a million: the process is saturated, and the claim doesn't hold.** Every run starts within 0.1 s and 0.8 s, but the process gets through only 42,000–67,000 runs a second (420,000–670,000 model calls), so the runs queue: 1.0–2.2 s at the median with 100,000, and 14–16 s with a million. At these sizes the overhead and "Timer late" columns are queueing, not a run's cost.
- **Where the ceiling comes from: partly the waits, mostly not.** A side experiment, outside the scenario and run once each, gave the same ten waits per run to the pool in plain async code, with no Strata, on the same machine. It got through 128,000 runs a second with 100,000 runs and 206,000 with a million (a median run of 0.64 s and 4.1 s). With a cancellable token per run and a linked one per group, as an engine needs, it got through 97,000 and 181,000. So the bare waits saturate too, and Strata's runs get through at a half to a third of their rate (a half with 100,000 runs, a third with a million). Without waits, in the control row, Strata does 500,000–720,000 runs a second. So the cost seems to come from how the engine's work and the waits combine. We haven't found what it is. Profiling it is [datalium/strata#37](https://github.com/datalium/strata/issues/37), whose program reproduces the gap without this repository.
- **The control row now uses every core.** It gets through 500,000–720,000 runs a second. Its 10,000 runs, queued at once, wait up to 14 ms for the pool, so its overhead column is queueing too. On one thread, the spec's version 0.2 of this scenario measured 7.0–7.8 µs a run.
- **Memory.** With a million runs in flight at once, the process peaks at 22–26 GiB of resident memory. That's the process's figure, garbage and the GC's heaps included, not what a run holds. A run allocates 31 KB with latency and 18.9 KB without, at every size.
- **The noise is large.** At 100,000 runs, the median run took 1.0 s in one run and 2.2 s in another. We haven't explained the spread.

## What it doesn't show

- **Durability.** The in-memory store under `Boundaries` writes nothing, so there's no checkpoint cost here. That is scenario 4's subject.
- **A real model.** Every call is a timer. A provider's latency, errors and rate limits aren't in it.
- **Linux on bare metal.** On Windows a timer wakes on a 15.6 ms tick. [RESULTS.md](../../RESULTS.md) also measures the scenario in a Linux container on the same machine, where timers wake on time: a thousand runs then take 174–177 ms at the median, and the burst stops at the same rate as on Windows.
- **Steady load.** Each row is one burst. A service that receives runs continuously is closer to scenario 4.
- **Memory per run.** The peak is the process's resident memory, with garbage and the GC's own heaps, not what a run holds.
