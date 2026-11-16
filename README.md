# Datalium Strata Stress - .NET

Stress demos for [Datalium Strata](https://github.com/datalium/strata) in .NET: the agentic AI orchestration framework under extreme workloads. Concurrent bursts, a million suspended threads, a worker killed under load and durable checkpoints to Postgres, each measured and reproducible with one command. Built from the published `Datalium.Strata` **0.9** packages (`0.9.0-preview.2`) on [nuget.org](https://www.nuget.org/packages/Datalium.Strata). Scripted models, no API keys.

> **Preview.** Strata is at `0.9.0-preview.2`, and so are these numbers: they will be measured again before 1.0.

## The scenarios

Four scenarios, each making one claim and checking it. [SCENARIOS.md](SCENARIOS.md) is the spec: the graphs, the state, the simulated latency, the sizes, what is measured and the output. The Rust twin, `strata-stress-rust` (not public yet), implements the same spec, so the two will compare.

| # | Scenario | The claim | Status |
|:--|:--|:--|:--|
| 1 | [Burst](scenarios/01-burst/) | Thousands of runs at once, each fanning out, cost the engine microseconds a run | [Measured](RESULTS.md#the-claims): holds at 1,000 runs at once, not above; waits on strata#37 |
| 2 | [Million sleepers](scenarios/02-million-sleepers/) | A suspended thread holds nothing in the process, and another process can resume it | [Measured](RESULTS.md#the-claims): holds |
| 3 | [Kill it mid-storm](scenarios/03-kill-mid-storm/) | A worker killed under load loses no run and charges nothing twice | [Measured](RESULTS.md#the-claims): holds |
| 4 | [Durable firehose](scenarios/04-durable-firehose/) | Checkpoints to Postgres scale with concurrency, more so with group commit | [Measured](RESULTS.md#the-claims): holds from 256 runs at once on Linux, only at 1,024 on Windows (strata#40) |

What they measure is Strata, not a model provider: every model call is a wait of a known, seeded latency, the same in both engines. They are synthetic, and they are not a Rust-vs-.NET race: where one engine costs more, the results say so and say why.

Every published number comes from a real run, with the machine, the runtime versions, the Strata version and the date, in this repository's [RESULTS.md](RESULTS.md), with the JSON next to it in [`results/`](results/): on Windows, and in a Linux container on the same machine ([`linux/`](linux/)).

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) or later
- [Docker](https://docs.docker.com/get-docker/), for Postgres (scenarios 2, 3 and 4): `docker compose up -d --wait` (see [`docker-compose.yml`](docker-compose.yml)). Scenario 1 needs nothing else.

Strata comes from NuGet, so this repository is all you need to clone.

## Running

```bash
docker compose up -d --wait
dotnet build Strata.Stress.slnx -c Release
dotnet run -c Release --project scenarios/01-burst
dotnet run -c Release --project scenarios/01-burst -- --quick
dotnet run -c Release --project scenarios/02-million-sleepers
docker compose down
```

Every scenario takes the same options:

| Option | Meaning |
|:--|:--|
| `--quick` | Small sizes, for CI: checks the scenario still runs, measures nothing |
| `--json PATH` | Where the JSON goes (default `artifacts/results/<scenario>.json`) |
| `--seed N` | The seed of every simulated latency (default `1`) |

A run prints a Markdown table and writes the same figures as JSON. It exits with `0` when every check passed, `1` when one failed, and `2` when it couldn't start (a bad option, or Postgres not running). `STRATA_STRESS_POSTGRES_URL` points a scenario at another Postgres (default `postgres://strata:strata@localhost:5432/strata`).

> **A scenario empties the database it runs against.** Scenarios 2 to 4 start by dropping Strata's checkpoint store from that database: its tables (`checkpoints`, `checkpoint_threads`, `checkpoint_leases`, `checkpoint_blobs`) and their schema version, which are the same tables any app using `Datalium.Strata.Storage` keeps there. Point `STRATA_STRESS_POSTGRES_URL` only at a database you can lose, such as the one `docker compose` starts.

## Structure

```
strata-stress-dotnet/
├── SCENARIOS.md              # the spec both stress demos implement
├── RESULTS.md                # the measured runs, Windows and Linux, and what they show
├── Strata.Stress.slnx        # solution
├── Directory.Packages.props  # every package version, Strata's included (central package management)
├── Directory.Build.props     # .NET 10, server GC: the settings the numbers depend on
├── nuget.config              # restore from nuget.org only
├── docker-compose.yml        # Postgres (and Redis) for the scenarios that need them
├── src/
│   └── Strata.Stress.Harness/  # what every scenario shares: options, simulated model, payload, percentiles, Postgres, report
├── scenarios/                # one project per scenario: 01-burst/, 02-million-sleepers/, ...
├── results/                  # every run behind RESULTS.md: its JSON and its output, windows/ and linux/
├── linux/                    # the container RESULTS.md's Linux runs used
└── tests/
    └── Strata.Stress.Harness.Tests/
```

## Dependencies on Strata

The scenarios use Strata's published NuGet packages, `Datalium.Strata` and `Datalium.Strata.Storage`. Every version is set once, in [`Directory.Packages.props`](Directory.Packages.props), and Strata's share one property:

```xml
<StrataVersion>0.9.0-preview.2</StrataVersion>
```

A scenario's `.csproj` names the packages it needs, without a version. [`nuget.config`](nuget.config) restores from nuget.org only, so the build doesn't depend on any feed configured on your machine.

## Related

- [datalium/strata](https://github.com/datalium/strata): the framework, its design docs and `benchmarks/RESULTS.md`, the microbenchmarks these scenarios put under load
- `strata-stress-rust` (not public yet): the Rust twin of this repository, on the same spec
- [strata-demos-dotnet](https://github.com/datalium/strata-demos-dotnet): one demo per feature, from minimal to real-world
