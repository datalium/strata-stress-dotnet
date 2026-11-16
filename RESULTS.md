# Stress results

The four scenarios of [SCENARIOS.md](SCENARIOS.md), measured at full size on one machine: on Windows, and in a Linux container on the same machine. How to run them: [README.md](README.md). Each run's JSON and its printed output are in [`results/`](results/), and every figure below comes from them.

## Measured on

- **When:** 2026-11-16, from 11:22 to 17:26 UTC, in one session. Each scenario's runs alternated between Windows and Linux, run by run, so that a drift of the machine spread across both.
- **Runs:** scenario 1 five times on each, scenario 2 three times, scenario 3 five times, scenario 4 three times (each with its spec's three repeats of every configuration), and one more scenario 4 run in Linux through the host's forwarded port (see "Stores"). 33 runs in all.
- **Version:** Strata `0.9.0-preview.2` from nuget.org, spec version 0.6, this repository at `c73f7b0` with the Linux runner added (no scenario code changed).
- **Machine:** Intel Core i9-12900KF (8 performance and 8 efficiency cores, 24 logical), 128 GB of RAM, Windows 11 Pro 10.0.26300.
- **Windows:** .NET 10.0.15 (SDK 10.0.204), server GC, concurrent GC, tiered PGO.
- **Linux:** Ubuntu 24.04.5 in a container ([`linux/Dockerfile`](linux/Dockerfile): `mcr.microsoft.com/dotnet/sdk:10.0`, pinned by digest, SDK 10.0.404 and the same .NET 10.0.15), with the same build settings. It ran in Docker Desktop 29.9.3's WSL 2 VM on the same machine: kernel 6.18.58.1-microsoft-standard-WSL2, 24 virtual CPUs, 62.7 GiB. **It is not Linux on bare metal**: it is a VM that Windows schedules, with half the machine's memory.
- **Postgres:** 16.15 (`postgres:16-alpine`) from [`docker-compose.yml`](docker-compose.yml), data on tmpfs, `max_connections` 300, in the same VM. Restarted, empty, before every run of scenarios 2 to 4. The Windows process reaches it through Docker Desktop's forwarded port (`localhost:5432`); the Linux container over the compose network, inside the VM.
- **Not a quiet machine.** A development session was open throughout. Nothing else heavy ran on purpose, but the slowest percentiles carry whatever did.
- **The machine under load:** [a Task Manager screenshot](results/task-manager-windows.png), taken on Windows during these runs.

Windows' timer ticks every 15.6 ms unless something raises its resolution; nothing in the scenarios does. Linux's timers are high-resolution.

## The claims

| # | Claim | Windows | Linux | Open |
|:--|:--|:--|:--|:--|
| 1 | Thousands of runs at once, each fanning out, cost the engine microseconds a run | **Holds at 1,000 runs at once** (median overhead 0.43–0.69 ms, the wait for the pool included). **Not at 10,000 or more**: the runs queue | The same: 0.45–0.62 ms at 1,000, queueing from 10,000 | [#37](https://github.com/datalium/strata/issues/37): why the burst stops at 50,000–64,000 runs a second. **The claim waits on it** |
| 2 | A suspended thread holds nothing in the process, and another process can resume it | **Holds**: 8–10 bytes of resident memory a thread, a million resumed by a fresh process | **Holds**: 8–18 bytes a thread, a million resumed | [#38](https://github.com/datalium/strata/issues/38): listing a million threads takes 9.5 to 10 minutes. A cost, not the claim |
| 3 | A worker killed under load loses no run and charges nothing twice | **Holds** in 15 kills: none lost, none charged twice | **Holds** in 15 kills | [#39](https://github.com/datalium/strata/issues/39): recovering a kill's orphans takes 38–61 s. A cost, not the claim |
| 4 | Checkpoints to Postgres scale with concurrency, more so with group commit (spec: "tens of thousands of checkpoints a second") | **Holds only at 1,024 runs at once** (×2.5, 17,200–17,800 a second). Below it, group commit is slower than none (×0.56–0.93). Tens of thousands: not reached | **Holds from 256 runs at once** (×2.1, then ×4.2: 38,500–45,000 a second at 1,024). At 16 and 64, group commit is level with none (×0.97–1.00) | [#40](https://github.com/datalium/strata/issues/40): the slowdown is .NET on Windows. **The Windows claim waits on it** |

The claims are the spec's, as written. Where one fails, this file says where, and the issue that explains it or should. A figure an open issue could change is listed under [Open issues](#open-issues), and is measured again when the issue is closed.

## What it shows

1. **Every check passed, in all 33 runs.** No order was lost and none was charged twice, in 30 kills of a worker with 136 to 192 runs in flight. Six times, a million suspended threads were listed by a fresh process and resumed, each with the right resolution. Every burst run's eight findings came back in order, and every durable run left exactly its eleven checkpoints.
2. **A suspended thread holds nothing in the process.** From 10,000 suspended threads to a million, resident memory grew by 8 to 10 bytes a thread on Windows and 8 to 18 on Linux, and the managed heap by −2.0 to 2.0 bytes: it stayed at 9.0 to 10.4 MiB with a million threads waiting. A thread costs the store 1,887 to 1,900 bytes, for a 1,329-byte checkpoint.
3. **A thousand runs at once take the model's time.** The model's waits put a run's critical path at about 175 ms. A median run took 174–177 ms on Linux and 187–199 ms on Windows, where each timer woke 6.8–7.3 ms late at the median, against 0.9–1.4 ms on Linux. The engine's median overhead, the wait for the pool included, was 0.43 to 0.69 ms on both.
4. **The burst stops at the same rate on both.** With a million runs at once, the process gets through 51,000–64,000 runs a second on Windows and 50,000–57,000 on Linux, and the runs queue: a median run takes 15 to 19 s. Without the model's waits it does 0.3 to 1.25 million. So the ceiling of [#37](https://github.com/datalium/strata/issues/37) isn't Windows' timer: it is in how the engine's work and the waits combine, as the issue says, and it is still unexplained.
5. **On Linux, group commit lifts the firehose, the more the more runs there are.** 256 runs at once write 21,700–22,100 checkpoints a second with it and 9,200–10,700 without (×2.1); 1,024 write 38,500–45,000 with it and 10,100–10,600 without (×4.2). At 16 and 64 runs, saves don't pile up, and group commit is level with none.
6. **On Windows, it lifts the firehose only at 1,024 runs at once, and slows it below.** ×0.91–0.92 at 16, ×0.56–0.66 at 64, ×0.89–0.93 at 256, and ×2.45–2.56 at 1,024 (17,200–17,800 a second). With the same flushes as Linux (4.5 to 5.1 saves each at 64 runs on both), each one takes longer on Windows, and the process's timers wake 2.5 to 4.5 ms late at the median with group commit on, against −0.3 to 3.0 ms without (on Linux, −0.7 to 0.4 with either). The whole scenario now confirms what the store-only repro of [#40](https://github.com/datalium/strata/issues/40) found: the slowdown needs .NET on Windows.
7. **Without group commit, Postgres stops at about 7,000 checkpoints a second from Windows, and about 10,500 from Linux.** In both, from 256 runs up, a run's extra time is its saves waiting for one of the pool's 100 connections: at 1,024 runs, its eleven checkpoints cost it 1.5–1.6 s on Windows and 1.0 s on Linux, against 34–36 and 16–17 ms at 16.
8. **The path to Postgres costs as much as the OS.** The Linux run through the host's forwarded port, as the Windows process reaches Postgres, stopped at 4,700 checkpoints a second without group commit and reached 17,200–17,500 with it at 1,024 runs: Windows' figure at 1,024, from a process whose timers woke on time. Below 1,024 that run and Windows differ; at 1,024 the forwarded port seems to set the limit. One run, so a pointer, not a finding.
9. **Linux runs the Postgres scenarios faster.** The storm got through 1,330–1,340 orders a second on Linux against 854–888 on Windows; the sleepers suspended 16,900–17,000 threads a second against 7,300–7,600, and resumed 12,600–12,800 against 9,100–10,400. The OS's timers and the path to Postgres both differ, and only scenario 4's extra run separates them (finding 8).
10. **Recovering a dead worker's runs is slow, one thread at a time** ([#39](https://github.com/datalium/strata/issues/39)). The first orphan completes 2.3 to 2.6 s after the kill, with a 2 s lease. All 136 to 192 took 38–61 s on Windows and 27–41 s on Linux, and a recovery pass that found nothing took 2.1–2.2 s at the median on Windows and 1.25–1.26 s on Linux: it loads and checks every live thread before the orphans.
11. **Listing a million waiting threads takes 9.5 to 10 minutes** ([#38](https://github.com/datalium/strata/issues/38)): 568–577 s on Windows and 576–589 s on Linux. It is Postgres's work, the same from either, and most of scenario 2's 40 to 42 minutes.

## 1. Burst

Each run of five, `--runs 1000,10000,100000,1000000` at 20–80 ms, then a 10,000-run control with no latency. The ranges are the five runs'. Overhead is a run's time minus its critical path, the wait for the pool included; at 10,000 runs and more it is mostly queueing (SCENARIOS.md, "What the overhead includes").

| Runs | Latency | OS | Runs/s | Run P50 ms | Run P99.9 ms | Overhead P50 ms | Overhead P99 ms | Overhead P99.9 ms | Timer late P50 ms | Timer late P99 ms | Start spread ms | Peak resident GiB | Allocated a run |
|---:|:--|:--|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | 20–80 ms | Windows | 3,803–3,857 | 187–199 | 259–263 | 0.43–0.69 | 2.8–11.6 | 8.6–16.1 | 6.8–7.3 | 15.8–16.1 | 0.1–0.2 | 0.07 | 30.9–31.3 KB |
| 1,000 | 20–80 ms | Linux | 4,068–4,196 | 174–177 | 238–246 | 0.45–0.62 | 5.3–10.3 | 11.4–14.1 | 0.9–1.4 | 11.0–16.2 | 0.2–0.3 | 0.07 | 31.3–31.7 KB |
| 10,000 | 20–80 ms | Windows | 18,042–29,216 | 264–456 | 326–536 | 1.19–5.10 | 11.7–21.2 | 38.3–63.4 | 32–130 | 72–192 | 16.9–27.7 | 0.28–0.32 | 31.8–32.2 KB |
| 10,000 | 20–80 ms | Linux | 18,382–22,530 | 285–377 | 420–529 | 9.80–12.33 | 116–292 | 122–294 | 30–54 | 83–107 | 14.6–66.2 | 0.28–0.34 | 31.7–32.2 KB |
| 100,000 | 20–80 ms | Windows | 36,614–53,438 | 1,457–1,893 | 1,836–2,706 | 550–1,124 | 660–2,130 | 660–2,130 | 123–410 | 224–696 | 78–94 | 1.65–2.71 | 31.1–31.2 KB |
| 100,000 | 20–80 ms | Linux | 31,539–43,754 | 1,394–2,886 | 2,189–3,109 | 410–1,221 | 650–2,236 | 651–2,236 | 90–970 | 268–1,184 | 79–243 | 1.26–2.46 | 31.2–31.3 KB |
| 1,000,000 | 20–80 ms | Windows | 51,495–63,628 | 15,106–18,789 | 15,694–19,375 | 2,007–6,699 | 3,125–8,816 | 3,129–8,820 | 3,129–7,173 | 4,133–8,797 | 500–860 | 21.6–26.2 | 31.2–31.3 KB |
| 1,000,000 | 20–80 ms | Linux | 50,474–57,330 | 16,006–19,118 | 17,323–19,699 | 2,202–2,966 | 3,668–4,494 | 3,696–4,511 | 5,897–7,209 | 6,960–9,280 | 541–1,136 | 21.0–26.0 | 31.2–31.3 KB |
| 10,000 | none | Windows | 776,156–1,242,884 | 3.5–6.3 | 6.8–12.0 | 3.55–6.33 | 6.5–11.9 | 6.8–12.0 | | | 1.2–1.5 | 20.7–25.3 | 18.9 KB |
| 10,000 | none | Linux | 278,825–1,254,584 | 3.3–15.5 | 6.4–33.5 | 3.30–15.54 | 6.4–33.4 | 6.4–33.5 | | | 1.4–2.5 | 20.8–25.8 | 18.9–19.0 KB |

- **At 1,000 runs, Linux is closer to the model's time** by Windows' timer: 6.8–7.3 ms late a call at the median on Windows, 0.9–1.4 ms on Linux, and a critical path of three calls. The overhead is the same on both.
- **At 10,000, Linux's tail is longer.** Its overhead P99 is 116–292 ms against 12–21 ms on Windows, while its median run is about the same (285–377 against 264–456 ms). Not explained.
- **At 100,000 and a million, both are saturated** at about the same rate, and the run times, the overhead and "Timer late" are queueing ([#37](https://github.com/datalium/strata/issues/37)). A run allocates 31 KB, on both and at every size; the peak of 21 to 26 GiB at a million is the process's, garbage and the server GC's heaps included, not what a run holds.
- **The control row varies widely**, and on Linux it got faster with every run: 279,000, 580,000, 966,000, 1,154,000 and 1,255,000 runs a second, in order. On Windows it ranged over 776,000 to 1,243,000 with no trend. Its 10,000 runs queue for the pool at once, so its overhead is mostly that queue. Neither is explained, and nor is why Windows' control row is faster than in the scenario's first runs, on 2026-11-14, with the same scenario code (500,000 to 722,000).

## 2. Million sleepers

Each run suspends a million threads at a boundary in Postgres (group commit on), samples memory at 0, 10,000, 100,000 and 1,000,000, then a child process lists them and resumes them all. Three runs on each.

| Figure | Windows | Linux |
|:--|:--|:--|
| Resident MiB at 0 / 10,000 / 100,000 / 1,000,000 suspended | 102–109 / 120–123 / 127–141 / 130–131 | 108–111 / 136–142 / 154–156 / 150–153 |
| `resident_per_thread_bytes` | 7.8–10.0 | 8.2–18.2 |
| Managed heap MiB at 10,000 / 1,000,000 (`engine_specific`) | 7.8–9.0 / 9.8–10.4 | 8.8–10.9 / 9.0–10.1 |
| `managed_heap_per_thread_bytes` (`engine_specific`) | 1.3–2.0 | −2.0–1.1 |
| Store MiB at 1,000,000 | 1,805–1,817 | 1,812–1,813 |
| `store_per_thread_bytes` | 1,887–1,900 | 1,895–1,896 |
| `checkpoint_bytes` | 1,329 | 1,329 |
| `suspend_per_s` | 7,256–7,575 | 16,856–16,994 |
| `list_s` | 568–577 | 576–589 |
| `resume_per_s` | 9,135–10,396 | 12,619–12,763 |
| `resume_process_resident_bytes`, MiB | 120–123 | 140–158 |
| Checks | 6 of 6 | 6 of 6 |
| Whole run | 41.5–42.2 min | 40.1–41.5 min |

- **The claim holds on both.** What the first 10,000 threads fill once (the pool's connections, the JIT, the GC's heaps) is the 12 to 31 MiB between the first two samples; from there to a million, the managed heap doesn't move, and resident memory grows by 18 bytes a thread at most, under 1.5% of a checkpoint.
- **Linux's resident memory is 13 to 29 MiB higher** at every sample after the first, with the same managed heap. The runtime's native memory differs between the two; not chased.
- **Private bytes don't compare.** On Linux, .NET's `PrivateMemorySize64` is a virtual-memory figure, 350 to 580 MiB here, larger than the resident memory, so `private_per_thread_bytes` in the Linux JSON (73 to 91) measures reservations, not memory used. On Windows it is committed private memory, 71 to 106 MiB.
- **Suspends are 2.3 times faster on Linux.** 256 threads are in flight, each waiting 1–5 ms, which a Windows timer stretches towards its 15.6 ms tick; the path to Postgres differs too (see finding 9). The scenario doesn't record its timers' lateness (SCENARIOS.md says why).

## 3. Kill it mid-storm

200,000 orders, three kills of the worker at a quarter, a half and three quarters, each worker running 256 orders at once with a 2 s lease, against Postgres with group commit off. Five runs on each, so fifteen kills.

| Figure | Windows | Linux |
|:--|:--|:--|
| Orphans a kill | 138–183 | 136–192 |
| `first_recovered_ms` | 2,279–2,480 | 2,353–2,551 |
| `all_recovered_ms` | 38.1–61.1 s | 27.1–41.0 s |
| `lost` | 0 | 0 |
| `duplicate_charges` | 0 | 0 |
| `charges` | 200,000 | 200,000 |
| `charge_attempts` | 200,144–200,185 | 200,145–200,203 |
| `recovered_node_runs` | the orphans', in every run | the orphans', in every run |
| `orders_per_s` | 854–888 | 1,330–1,340 |
| `timer_late_p50_ms` / `p99` | 0.98–1.10 / 11.6–12.1 | 0.46–0.50 / 6.4–7.0 |
| Idle recovery pass, median (`engine_specific`) | 2,104–2,245 ms | 1,250–1,261 ms |
| Longest recovery pass (`engine_specific`) | 24.5–25.5 s | 16.4–17.4 s |
| Orphan listing (`engine_specific`) | 74–122 ms | 70–163 ms |
| Checks | 6 of 6 | 6 of 6 |
| Whole run | 4.0–4.2 min | 2.6–2.7 min |

- **The claim holds on both.** Every order ended `completed`, with one `completed` checkpoint and one ledger row; every re-run of `Charge` used the original's idempotency key. `recovered_node_runs` equals the orphans in every run, so no live worker's thread was taken by a recovery. `charge_attempts` exceeds 200,000 by the orphans whose `Charge` attempt was written before the kill.
- **The recovery is a run at a time, at a run's latency under load** ([#39](https://github.com/datalium/strata/issues/39)). A kill's orphans all completed 38–61 s after it on Windows and 27–41 s on Linux, and the longest recovery pass (the one that recovers them) took up to 25.5 s and 17.4 s. The idle passes' 1.25 to 2.2 s is the sweep of the live worker's 256 threads that each pass makes first.
- **The recovery times vary within a run** as much as between runs: 38.1 to 61.1 s on Windows, with three kills of one run spanning 43 to 61 s. Not explained.

## 4. Durable firehose

Ten-node runs, each checkpointing after every node, in a closed loop of 16 to 1,024 at once, group commit off and on; three repeats of 20 s per configuration in each run, three runs on each, so nine repeats per configuration. "Linux via forwarded port" is one more Linux run, reaching Postgres through `host.docker.internal:5432`, the host's forwarded port, as the Windows process does: three repeats.

Checkpoints a second: each run's median, and the lowest and highest of the nine repeats.

| Runs at once | Group commit | Windows, each run's median | Windows, all 9 | Linux, each run's median | Linux, all 9 | Linux via forwarded port, 3 repeats |
|---:|:--|:--|:--|:--|:--|:--|
| 16 | off | 1,371 / 1,362 / 1,359 | 1,342–1,387 | 2,472 / 2,474 / 2,477 | 2,469–2,481 | 1,431–1,514 |
| 16 | on | 1,242 / 1,239 / 1,251 | 1,223–1,292 | 2,416 / 2,418 / 2,397 | 2,386–2,430 | 1,648–1,902 |
| 64 | off | 6,106 / 6,092 / 6,067 | 5,940–6,214 | 8,341 / 8,349 / 8,213 | 8,082–8,354 | 4,011–4,039 |
| 64 | on | 4,044 / 3,449 / 3,373 | 3,294–4,176 | 8,307 / 8,285 / 8,173 | 8,065–8,436 | 3,646–4,535 |
| 256 | off | 7,148 / 7,130 / 7,079 | 6,917–7,216 | 10,608 / 10,574 / 10,359 | 9,241–10,680 | 4,177–4,582 |
| 256 | on | 6,389 / 6,500 / 6,594 | 6,164–6,958 | 22,014 / 21,872 / 21,774 | 21,697–22,067 | 8,916–9,233 |
| 1,024 | off | 7,133 / 7,107 / 6,856 | 6,556–7,191 | 10,431 / 10,559 / 10,198 | 10,130–10,610 | 4,681–4,723 |
| 1,024 | on | 17,558 / 17,414 / 17,519 | 17,195–17,759 | 44,399 / 44,757 / 43,255 | 38,509–44,950 | 17,216–17,451 |

Group commit on ÷ off, each run's medians:

| Runs at once | Windows | Linux | Linux via forwarded port |
|---:|:--|:--|:--|
| 16 | 0.91–0.92 | 0.97–0.98 | 1.21 |
| 64 | 0.56–0.66 | 0.99–1.00 | 0.95 |
| 256 | 0.89–0.93 | 2.07–2.10 | 1.99 |
| 1,024 | 2.45–2.56 | 4.24–4.26 | 3.67 |

Ranges over the three runs on each, for 16 / 64 / 256 / 1,024 runs at once:

| Figure | Windows | Linux |
|:--|:--|:--|
| Run P50 ms, off | 128–130 / 115 / 392–396 / 1,570–1,626 | 71–72 / 84–86 / 264–275 / 1,065–1,097 |
| Run P50 ms, on | 141 / 174–199 / 423–443 / 647–648 | 72–73 / 84–86 / 128–129 / 251–264 |
| Checkpoints' cost P50 ms, off | 34–36 / 55–56 / 323–328 / 1,502–1,557 | 16–17 / 31–32 / 200–210 / 1,000–1,031 |
| Checkpoints' cost P50 ms, on | 37–38 / 89–101 / 323–341 / 539–542 | 19–20 / 30–31 / 70–71 / 192–203 |
| Timer late P50 ms, off | 2.49–3.01 / −0.31 to −0.28 / 0.19–0.27 / 0.19–0.27 | −0.56 / −0.73 to −0.67 / 0.32–0.41 / 0.34–0.42 |
| Timer late P50 ms, on | 4.40–4.50 / 2.53–2.83 / 2.96–3.45 / 3.78–3.99 | −0.71 to −0.69 / −0.59 to −0.52 / −0.26 to −0.23 / −0.19 to −0.18 |
| Group commit's mean flush, saves (`engine_specific`) | 2.1–2.3 / 4.5–5.1 / 11.4–11.7 / 98–108 | 1.7 / 4.9–5.1 / 14.5–14.6 / 104–120 |
| The process's CPU in the window, off / on, cores | 0.5–0.7 / 2.2–2.5 from 64 up; 0.2–0.6 on | 1.3–1.4 / 3.1–3.9 from 64 up; 0.8–2.6 on |
| Saves repeated after a transient failure | 0 | 0 |
| Checks | 3 of 3 in each run | 3 of 3 in each run |
| Whole run | 10.6 min | 10.6 min |

- **On Linux, the claim holds from 256 runs at once**, and "tens of thousands of checkpoints a second" too: 38,500 to 45,000 at 1,024, a run taking 251–264 ms at the median against 1,065–1,097 ms without group commit. At 16 and 64, a flush finds only 1.7 to 5 saves, and does as much as single saves on their own connections.
- **On Windows, it holds only at 1,024**, as the scenario's own runs of 2026-11-15 found ([its README](scenarios/04-durable-firehose/README.md)), with the same figures: ×0.56–0.66 at 64 here, ×0.62–0.65 then. The flushes are as large as on Linux, so each one takes longer: at 64 runs, a run's checkpoints cost it 89 to 101 ms with group commit, against 55 to 56 without, and against 30 to 31 on Linux with it. The timers wake 2.5 to 4.5 ms late at the median with group commit on, and −0.3 to 3.0 ms without it, 3.0 only at 16 runs; on Linux they wake on time with both. The cause is [#40](https://github.com/datalium/strata/issues/40)'s, still unknown.
- **A negative timer lateness is a wait that ended early.** `Task.Delay` takes whole milliseconds and drops the fraction, so a call that asks for 5.9 ms can wake after 4.9. The handlers charge the run with the wait they measured, so the checkpoints' cost isn't affected.
- **Through the forwarded port, Linux loses its lead without group commit** (4,000 to 4,700 checkpoints a second from 64 runs up, below Windows' 5,900 to 7,200) and reaches Windows' figure with it at 1,024 (17,200–17,500). With group commit it is ahead of Windows at 16 and 256, and level at 64 (3,646–4,535 against 3,294–4,176), where its ratio is 0.95. One run of three repeats: enough to say that the network path matters as much as the OS here, not to rank them.
- **The repeats agree, with two exceptions on Linux**: one repeat of nine at 256 runs without group commit (9,241, against 10,359 to 10,680) and one at 1,024 with it (38,509, against 43,255 to 44,950), both in the third run. On Windows, at 64 runs with group commit, the first run's median (4,044) is above the other two's (3,373 and 3,449). The store's own count of saves committed agreed with the table's count to within 1.5% in every repeat.

## Noise and what isn't explained

- **Scenario 1's ranges are wide** from 10,000 runs up, on both OSes: at 100,000, the median run took 1.4 s in one Linux run and 2.9 s in another. Its control row got faster with every Linux run, 279,000 to 1,255,000 runs a second, and isn't explained; nor is Linux's longer overhead tail at 10,000 runs (116–292 ms at P99, against 12–21 ms).
- **Scenario 3's recoveries** spread from 38 to 61 s on Windows and 27 to 41 s on Linux, within runs as much as between them.
- **Scenario 4's Windows slowdown with group commit** ([#40](https://github.com/datalium/strata/issues/40)) is measured, located (.NET on Windows, not the store's design, Postgres or the network path) and not explained.
- **Scenario 1's ceiling** ([#37](https://github.com/datalium/strata/issues/37)) is the same on both OSes and not explained.
- **Linux's process memory** is 13 to 29 MiB higher than Windows' in scenario 2, and not chased.
- **The Linux figures are a VM's.** Windows schedules the VM's 24 virtual CPUs with everything else on the machine, and the VM has 62.7 GiB of the machine's 128 GB. Linux on its own machine could do better or worse; it wasn't measured.

## Open issues

Each is open against Strata `0.9.0-preview.2`. When one is closed, the figures it names are measured again, on both OSes, and this file changes.

| Issue | What it is | Figures it could change |
|:--|:--|:--|
| [#37](https://github.com/datalium/strata/issues/37) | A burst of runs that await gets through 3 to 5 times fewer runs a second than the same waits without the engine | Scenario 1 from 10,000 runs: runs a second, run times, overhead. Scenario 1's verdict |
| [#38](https://github.com/datalium/strata/issues/38) | Each page of a Postgres thread listing joins every matching thread (a collation mismatch) | Scenario 2's `list_s` and whole run; scenario 3's orphan listing and recovery passes |
| [#39](https://github.com/datalium/strata/issues/39) | `RecoverStaleAsync` recovers one thread at a time and checks every live thread first | Scenario 3's `all_recovered_ms`, its recovery passes, and the `--orders` size the spec chose for them |
| [#40](https://github.com/datalium/strata/issues/40) | Postgres group commit is slower than none until about a thousand writers, .NET on Windows | Scenario 4's Windows figures with group commit below 1,024 runs, and its Windows verdict |

## What it doesn't show

- **The Rust engine.** The Rust twin, `strata-stress-rust`, implements the same spec and isn't built yet. Its figures come next to these, on the same machine.
- **Linux on bare metal**, or Postgres on a machine of its own (see the noise above).
- **A store on disk.** Postgres keeps its data on tmpfs, so a commit's fsync costs almost nothing. On a disk, every checkpoint waits for one, and group commit, which shares a commit among many saves, would gain more than it does here.
- **A real model.** Every model call is a timer of a seeded latency (SCENARIOS.md, "The simulated model"). A provider's latency, errors and rate limits aren't in it.

## The runs

[`results/windows/`](results/windows/) and [`results/linux/`](results/linux/) hold each run's JSON (`<scenario>-<n>.json`, in the spec's `strata-stress/1` format) and what it printed (`<scenario>-<n>.txt`). `results/linux/04-durable-firehose-host-port-1` is the run through the forwarded port.

To run them again, from the repository root:

```bash
docker compose up -d --wait
dotnet run -c Release --project scenarios/04-durable-firehose -- --json results/windows/04-durable-firehose-1.json
docker compose run --rm --build linux scenarios/04-durable-firehose -- --json results/linux/04-durable-firehose-1.json
```

Restart Postgres between runs (`docker compose down`, then `up -d --wait`) for an empty one, as these runs did.
