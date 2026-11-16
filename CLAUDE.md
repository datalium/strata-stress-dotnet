# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repo is

.NET stress demos for [Datalium Strata](https://github.com/datalium/strata): four scenarios that put the
framework under extreme workloads and measure it (burst, million sleepers, kill it mid-storm, durable
firehose). [SCENARIOS.md](SCENARIOS.md) is the spec, shared with the Rust twin `strata-stress-rust-preview`;
[README.md](README.md) says how to run them.

This is the private preview repository (`datalium/strata-stress-dotnet-preview`). The finished code moves to
a fresh `datalium/strata-stress-dotnet`, which is then made public (STR-102). Write everything as if it
were already public: no secrets, no internal links in code or JSON, no host names.

The scenarios build from Strata's **published NuGet packages** (`Datalium.Strata`, `.Storage`), not from
a source checkout. Versions are set once, in `Directory.Packages.props` (central package management;
Strata's share `$(StrataVersion)`, `0.9.0-preview.2`), and a project's `.csproj` has versionless
`<PackageReference>`s. `nuget.config` restores from nuget.org only. `Directory.Build.props` holds the
settings every number depends on (.NET 10, server GC); `global.json` opts `dotnet test` into
Microsoft.Testing.Platform.

Jira: epic STR-93 in the `STR` space. STR-95 is the spec, STR-96 this scaffold, STR-97 to STR-100 the
four scenarios, STR-101 the measured `RESULTS.md`.

## Conventions to follow

- **The spec decides the workload.** Graph ids and versions, node names, state field names, thread ids,
  call numbers, latency ranges, sizes, procedures and JSON keys come from `SCENARIOS.md`. Code that needs
  something the spec doesn't say gets the spec changed first, in both repos (it is copied to the Rust
  repo and kept identical) and with its version moved. Don't let the two engines' workloads drift.
- **Verify the actual API before writing scenario code.** Read the source of `dotnet/src/Datalium.Strata`
  and `.Storage` in [`datalium/strata`](https://github.com/datalium/strata) at the tag the packages come
  from (`v0.9.0-preview.2`), and prefer a working example (`strata-demos-dotnet`: demo 10 for parallel
  groups, 05 for interrupt boundaries, 12 for the Postgres store, 20 for leases and recovery after a real
  crash) over inferring from the design docs.
- **The harness is shared.** `src/Strata.Stress.Harness` has the command line, the simulated model, the
  percentiles, the memory samples, the machine's description and the report. A scenario uses it rather
  than its own. Its tests hold the spec's test vectors; the Rust harness has the same ones.
- **Measure, don't assume.** No number goes in a README, `RESULTS.md`, a post or a commit message unless a
  real run produced it, with the machine, runtime versions, Strata version and date beside it. A quick
  run measures nothing: never quote one.
- **Be honest about noise.** Report ranges where runs differ, say what is unexplained, and say where .NET
  costs more than Rust as plainly as where it costs less. Timer overshoot and scheduler queueing are
  part of what a scenario shows, and its README says so.
- **Checks are about correctness, never speed.** A scenario checks its claim (no run lost, nothing
  charged twice, every answer right) and exits 1 when a check fails. How fast it went is a result, not a
  check.
- **Every scenario must actually build and run before it's considered done**, both full and `--quick`:
  `dotnet run -c Release --project scenarios/<scenario> -- --quick`, plus
  `dotnet build Strata.Stress.slnx -c Release` for the whole solution.
- **Each scenario gets its own `README.md`**: the claim, how to run it, what it doesn't show, and the
  output of a real run marked with its machine and date. Add a `<Project Path="..." />` entry under the
  `/scenarios/` folder in `Strata.Stress.slnx`; CI runs every `scenarios/NN-*/` project with `--quick`.
- **No live model calls.** Every model call is the harness's `SimulatedModel`. Don't add a provider.
- **A new package version goes in `Directory.Packages.props`**, never in a `.csproj`.
- **Ask before scope decisions that aren't purely mechanical**: a new external service, a change to a
  scenario's claim, a size the spec doesn't name.

## Commands

```bash
docker compose up -d --wait                     # Postgres + Redis, for scenarios 2-4
dotnet restore Strata.Stress.slnx
dotnet build Strata.Stress.slnx --configuration Release --no-restore
dotnet test --solution Strata.Stress.slnx --configuration Release --no-build
dotnet format Strata.Stress.slnx --verify-no-changes
dotnet run -c Release --project scenarios/<scenario> -- --quick
```
