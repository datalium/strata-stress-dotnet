# Scenarios

One project per scenario, numbered as in [SCENARIOS.md](../SCENARIOS.md). CI runs every `NN-*/` folder here with `--quick`.

| # | Folder | Ticket | Status |
|:--|:--|:--|:--|
| 1 | [`01-burst/`](01-burst/) | [STR-97](https://datalium.atlassian.net/browse/STR-97) | Built |
| 2 | [`02-million-sleepers/`](02-million-sleepers/) | [STR-98](https://datalium.atlassian.net/browse/STR-98) | Built |
| 3 | [`03-kill-mid-storm/`](03-kill-mid-storm/) | [STR-99](https://datalium.atlassian.net/browse/STR-99) | Built |
| 4 | [`04-durable-firehose/`](04-durable-firehose/) | [STR-100](https://datalium.atlassian.net/browse/STR-100) | Built |

## Adding a scenario

1. Create `scenarios/NN-name/NN-name.csproj`, an `Exe` with a `<ProjectReference>` to `src/Strata.Stress.Harness` and versionless `<PackageReference>`s to `Datalium.Strata` (and `.Storage` if it needs a store). `Directory.Build.props` sets the framework and the GC.
2. Add `<Project Path="scenarios/NN-name/NN-name.csproj" />` under the `/scenarios/` folder in `Strata.Stress.slnx`.
3. Wrap `Main` in `Scenario.RunAsync`, parse the options with `ScenarioArgs.Parse`, use `SimulatedModel` for every model call, and write the output with `Report`.
4. Give it a `README.md`: the claim, how to run it, what it doesn't show, and the output of a real run with its machine and date.
5. Update the table above and the one in the root README.
