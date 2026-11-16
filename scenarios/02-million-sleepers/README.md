# 2. Million sleepers

**Claim.** A thread suspended at a boundary holds nothing in the process: a million of them grow its memory by nothing measurable, they cost their checkpoint in the store and no more, and a fresh process finds them and resumes them all.

The spec is [SCENARIOS.md, "2. Million sleepers"](../../SCENARIOS.md#2-million-sleepers). In short:

- **The graph.** `stress-sleepers` v1 is `Intake`, an interrupt boundary, then `Resolve`. Each node makes one model call: a simulated wait of 1–5 ms, seeded so the Rust twin waits the same. The state carries a 1,024-character payload that doesn't compress, standing in for a conversation.
- **The store.** Postgres, with group commit on at its defaults (256 saves a flush, 4 flushes at once), and the default checkpoint policy (`Boundaries`). A thread that suspends writes one `interrupted` checkpoint. A resume writes its `resumed_at` copy, and a run that completes writes nothing more.
- **Phase 1, in the scenario's process.** It empties the store and runs a warm-up. Then it suspends threads `sleeper-0000000` to `sleeper-0999999`, at most 256 at once. At each milestone it stops, takes a memory sample after a full collection, and measures the store's size.
- **Phase 2, in a child process.** The scenario starts the child from its own binary, in `resume` mode. The child opens a new store on the same database, lists the waiting threads 1,000 to a page, and resumes each one with an approval from `stress`, at most 256 at once. It reports to the parent through a file, and the parent writes the one report.

## Run it

```bash
docker compose up -d --wait                                            # Postgres, on tmpfs
dotnet run -c Release --project scenarios/02-million-sleepers            # full: a million threads
dotnet run -c Release --project scenarios/02-million-sleepers -- --quick # 1,000 threads, for CI; measures nothing
```

It needs Postgres, and says how to start it if it can't reach it (exit code 2). `STRATA_STRESS_POSTGRES_URL` points it at another one, but **it starts by dropping Strata's checkpoint store from that database**, the same tables any Strata app keeps there, so use one you can lose. A full run takes 41 to 43 minutes, most of it listing threads (see [What it shows](#what-it-shows)). At the end the store holds 4.3 GB in Docker's tmpfs, which fits in the 31.4 GB the container had.

| Option | Full | Quick |
|:--|:--|:--|
| `--threads` | `1000000` | `1000` |
| `--in-flight` (runs at once) | `256` | `32` |
| `--milestones` (memory samples, in threads suspended; three or more, ascending) | `0,10000,100000,1000000` | `0,100,1000` |
| `--warmup` (threads suspended and resumed first, not counted) | `1000` | `10` |

`--seed N` changes every latency and payload, and `--json PATH` says where the JSON goes (default `artifacts/results/02-million-sleepers.json`). The exit code is 0 when every check passes, 1 when one fails, and 2 for a bad option or a Postgres it can't reach.

## What the figures mean

- **Resident and store, per milestone.** Resident memory is the process's working set after a full, blocking, compacting collection. The store is `pg_total_relation_size` of the store's four tables (`checkpoints`, `checkpoint_threads`, `checkpoint_leases`, `checkpoint_blobs`), summed, indexes included.
- **Per thread.** The growth from the second milestone to the last, divided by the threads in between. It starts at the second so that what the first threads fill once (the connection pool, the JIT, buffers) isn't spread over a million.
- **`checkpoint_bytes`.** The JSON of one thread's `interrupted` checkpoint, loaded back from the store, as `datalium/strata`'s benchmarks measure it.
- **`suspend_per_s`.** Threads suspended a second, the sampling pauses left out.
- **`list_s` and `resume_per_s`.** Phase 2's listing of every waiting thread, then the rate at which it resumes them and they complete.
- **`resume_process_resident_bytes`.** Phase 2's resident memory after its last resume, sampled the same way as phase 1's. The list of tickets it resumed is let go first.

The JSON adds, under `engine_specific`, the managed heap and private bytes at each milestone, the managed heap's growth per thread, and phase 2's managed heap and private bytes.

## Real runs

Three full runs, on 2026-11-15 between 01:33 and 05:24 UTC: the first with version 0.3 of the spec, the other two with 0.4, which pinned what this code already did and changed none of the workload. The same build, the same machine and the same Postgres, emptied by each run:

- **Machine.** A desktop with an Intel Core i9-12900KF (8 performance and 8 efficiency cores, 24 logical) and 128 GB of RAM, running Windows 11 Pro 10.0.26300. It had the usual desktop apps open, so it wasn't a quiet benchmark machine.
- **Software.** .NET 10.0.15 with server GC, Strata `0.9.0-preview.2` from nuget.org. Postgres 16.15 (`postgres:16-alpine`) on tmpfs, in Docker Desktop 29.9.3's Linux VM with 62.7 GiB of memory, on the same machine.

| Figure | Run 1 | Run 2 | Run 3 | Range |
|:--|--:|--:|--:|:--|
| Resident MiB at 10,000 / 1,000,000 suspended | 128 / 129 | 124 / 128 | 130 / 144 | |
| `resident_per_thread_bytes` | 1.08 | 3.73 | 15.2 | 1.1–15 |
| `managed_heap_per_thread_bytes` (`engine_specific`) | 0.52 | −1.28 | 0.41 | −1.3–0.5 |
| `private_per_thread_bytes` (`engine_specific`) | −0.004 | 2.96 | 13.6 | 0–14 |
| `store_per_thread_bytes` | 1,895 | 1,901 | 1,891 | 1,891–1,901 |
| `checkpoint_bytes` | 1,329 | 1,329 | 1,329 | 1,329 |
| `suspend_per_s` | 7,714 | 5,228 | 6,839 | 5,200–7,700 |
| `list_s` | 540 | 558 | 555 | 540–558 |
| `resume_per_s` | 7,273 | 7,152 | 10,686 | 7,200–10,700 |
| `resume_process_resident_bytes`, MiB | 125 | 119 | 128 | 119–128 |
| Checks | 6 of 6 | 6 of 6 | 6 of 6 | |
| Whole run | 40m35s | 42m57s | 40m48s | 41–43 min |

The first, as it printed:

```text
## 02-million-sleepers: Strata 0.9.0-preview.2, .NET 10.0.15, 12th Gen Intel(R) Core(TM) i9-12900KF, 24 logical cores

| Suspended | Resident MiB | Store MiB |
|---:|---:|---:|
| 0 | 108 | 3.76 |
| 10,000 | 128 | 22.7 |
| 100,000 | 129 | 188 |
| 1,000,000 | 129 | 1,811 |

- `threads`: 1,000,000
- `suspend_per_s`: 7,714
- `resident_per_thread_bytes`: 1.08
- `store_per_thread_bytes`: 1,895
- `checkpoint_bytes`: 1,329
- `list_s`: 540
- `resume_per_s`: 7,273
- `resume_process_resident_bytes`: 130,703,360

Checks: 6 passed, 0 failed.
- PASS after phase 1, the store lists every thread waiting at the boundary, and no other
- PASS phase 2 listed every waiting thread, and no other
- PASS every resume completed
- PASS every resolution is right
- PASS after phase 2, no thread waits at the boundary
- PASS after phase 2, the store lists every thread as resumed
```

**[RESULTS.md](../../RESULTS.md) replaces these figures.** It has this scenario's full runs of 2026-11-16, on Windows and in a Linux container on the same machine, with the same code. The runs above are the ones the scenario was built and sized with.

## What it shows

- **A suspended thread holds nothing in the process: the claim holds.** From 10,000 suspended threads to a million, resident memory grew by 1.1 to 15 bytes a thread across the three runs. A thread's checkpoint is 1,329 bytes, and it is in Postgres, not in the process. The managed heap, where a thread's state would be if the process kept it, stayed at about 9.5 MiB from 10,000 threads to a million in every run: −1.3 to 0.5 bytes a thread. The 14 to 20 MiB between the first two milestones is what the first threads fill once: the pool's connections, the JIT and the GC's heaps.
- **The process's native memory varies from run to run, with the GC's spare memory and the process's threads, not with the threads suspended.** In run 3, private bytes grew by 12.7 MiB between 100,000 and a million threads, while the managed heap didn't move; runs 1 and 2 grew by 0 and 2.7 MiB over the same span. Four more runs of phase 1 alone, on the same machine on the same day, with a temporary probe, recorded at each milestone the memory the GC keeps committed and the process's OS threads. Server GC kept 42 to 62 MiB committed around a heap of 5 to 11 MiB, and how much it kept after a collection moved by up to 8 MiB from one milestone to the next while the heap stayed put. The process had 68 to 195 OS threads, about 60 KB each. Together they account for private bytes to within 2.1 MiB in all 16 samples (private ≈ 21.7 MiB + 1.13 × the GC's committed memory + 0.061 MiB a thread). The probe runs didn't repeat run 3's 12.7 MiB: their largest growth, run 4's 7.7 MiB, came with 7.6 MiB more committed by the GC. Run 3's is very likely the same, but it wasn't measured in run 3 itself. At 15 bytes a thread at most, it is a hundredth of the checkpoint.
- **A thread costs its checkpoint in the store, and a little more.** 1,891 to 1,901 bytes a thread for a 1,329-byte checkpoint. The rest is the row in `checkpoints` stored as JSONB, its head row in `checkpoint_threads`, and their indexes. After the resumes, each thread has its `resumed_at` copy as well, and the store holds 4.3 GB in all.
- **A fresh process finds them and resumes them all.** In every run, the child process listed exactly the million waiting threads and resumed every one, each completing with `resolved-{ticket}-stress`, at 7,200 to 10,700 a second. Its own resident memory at the end was 119 to 128 MiB.
- **Listing them is slow, and that is a Strata issue** ([datalium/strata#38](https://github.com/datalium/strata/issues/38)). Listing a million waiting threads took 540 to 558 s; 100,000 took 4 s in an earlier, smaller run. Each page of 1,000 cost about half a second at a million threads, because each page joined and sorted every waiting thread past the cursor, not just the page's 1,000. The listing joins `checkpoint_threads`, whose `thread_id` is `COLLATE "C"`, to `checkpoints`, whose `thread_id` has the database's default collation. In `EXPLAIN ANALYZE`, that mismatch keeps the join from using `checkpoints`' primary key or the head table's ordered index, whatever the database's locale: it does the same on a database created with `C`. With the join's collation matched (`c.thread_id = h.thread_id COLLATE "default"`), the same page took 2 ms instead of 500 ms. The query is the same in the Rust store. The scenario lists four times (phase 1's check, phase 2, and two checks after it), which is why a full run takes 41 to 43 minutes.
- **Suspends and resumes vary from run to run.** 256 threads are in flight, each waiting 1–5 ms, which a Windows timer can stretch to its 15.6 ms tick (see [SCENARIOS.md](../../SCENARIOS.md#the-simulated-model)); this scenario doesn't record how late its timers woke (the spec says why). The runs gave 5,200 to 7,700 suspends and 7,200 to 10,700 resumes a second. What sets the rate between the timer, Postgres and the desktop's other work isn't measured, and the spread is why a single run's rate means little here.

## What it doesn't show

- **A store on disk.** Postgres keeps its data on tmpfs, as `datalium/strata`'s benchmarks did, so no disk sets the pace. On a real disk, suspends and resumes cost their fsyncs, and group commit matters more.
- **Postgres's own memory.** The claim is about the process that runs the graphs. A million rows cost Postgres its shared buffers and the tmpfs, which the scenario doesn't measure beyond the tables' size.
- **A crash.** Phase 1 ends normally and disposes of its store before phase 2 starts. A worker killed in the middle is scenario 3's subject.
- **Linux on bare metal.** These runs were on Windows, against a Postgres in a Linux VM. [RESULTS.md](../../RESULTS.md) also measures the scenario in a Linux container on the same machine: the claim holds there too, and it suspends 2.3 times faster.
- **A real conversation.** The payload is 1,024 characters. A real thread's state can be far larger, and its checkpoint with it.
