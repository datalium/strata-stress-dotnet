# Contributing to the .NET stress demos

Thanks for helping. These are stress demos for [Datalium Strata](https://github.com/datalium/strata), built from the published packages. Bugs and feature requests for Strata itself go to [datalium/strata](https://github.com/datalium/strata/issues). For a change to what a scenario does, open an issue first: the scenarios follow [SCENARIOS.md](SCENARIOS.md), which the Rust twin shares.

## What you need

- The **.NET 10 SDK**.
- **Docker**, for scenarios 2, 3 and 4 (`docker compose up -d --wait` starts Postgres).

## Build and run

These are the commands CI runs:

```bash
dotnet restore Strata.Stress.slnx
dotnet format Strata.Stress.slnx --verify-no-changes   # format
dotnet build Strata.Stress.slnx --configuration Release --no-restore
dotnet test --solution Strata.Stress.slnx --configuration Release --no-build
dotnet run -c Release --project scenarios/01-burst -- --quick
```

## Rules every scenario follows

- **It follows the spec.** The graph, the state, the latencies, the sizes and the JSON keys are [SCENARIOS.md](SCENARIOS.md)'s. A change to any of them changes the spec, here and in [`strata-stress-rust`](https://github.com/datalium/strata-stress-rust), in the same way.
- **It checks its own claim.** A scenario exits non-zero when a check fails. Checks are about correctness; how fast it ran is a result, never a check.
- **Its numbers are real.** A figure in a README or in `RESULTS.md` comes from a run, with the machine, the runtime versions, the Strata version and the date. A `--quick` run measures nothing.
- **It runs offline.** Every model call is simulated. Postgres on the same machine is the only service.
- **A new scenario** needs its own README and a `<Project Path="..." />` entry in `Strata.Stress.slnx`, with versionless `<PackageReference>`s (versions live in `Directory.Packages.props`).

## Pull requests

- One change per pull request, with a clear title. PRs are squash-merged, so the title becomes the commit message.
- The `.NET stress` check must pass.

### Sign your commits (DCO)

Every commit needs a `Signed-off-by` line. It certifies that you wrote the change, or otherwise have the right to submit it under the project's license, as the [Developer Certificate of Origin](https://developercertificate.org/) describes. Add it with `git commit -s`.

## License

By contributing, you agree that your contributions are licensed under the [Apache License 2.0](LICENSE).
