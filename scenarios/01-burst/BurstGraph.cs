using System.Globalization;
using System.Text.Json.Serialization;
using Datalium.Strata.Graph;
using Datalium.Strata.Stress.Harness;

namespace Datalium.Strata.Stress.Burst;

/// <summary>A run's state (SCENARIOS.md, "1. Burst", "Graph and state"), with the spec's JSON field names.</summary>
internal sealed record BurstState(
    [property: JsonPropertyName("run")] ulong Run,
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("findings")] IReadOnlyList<string> Findings,
    [property: JsonPropertyName("answer")] string Answer)
{
    public static BurstState Start(ulong run) => new(run, string.Empty, [], string.Empty);
}

/// <summary>
/// How long each of a row's model calls really waited, in <see cref="System.Diagnostics.Stopwatch"/> ticks, and when
/// each run started and returned. Preallocated for the row, outside the state, so recording costs a store to an array.
/// </summary>
internal sealed class Timings(int runs)
{
    public int Runs => runs;

    public long[] Started { get; } = new long[runs];

    public long[] Finished { get; } = new long[runs];

    public long[] Plan { get; } = new long[runs];

    /// <summary>Branch <c>b{i}</c> of run <c>r</c> is at <c>r * 8 + i</c>.</summary>
    public long[] Branch { get; } = new long[runs * BurstGraph.Branches];

    public long[] Answer { get; } = new long[runs];
}

/// <summary>
/// Graph <c>stress-burst</c>, version <c>1</c>: <c>Plan</c> (call 0), the parallel group <c>Research</c> of eight
/// branches <c>b0</c> to <c>b7</c> (calls 1 to 8) merged in declared order, and <c>Answer</c> (call 9).
/// </summary>
internal static class BurstGraph
{
    public const int Branches = 8;

    public const int CallsPerRun = Branches + 2;

    public static CompiledGraph<BurstState> Build(SimulatedModel model, Timings timings)
    {
        var branches = new ParallelBranches<BurstState>();
        for (var i = 0; i < Branches; i++)
        {
            var branch = i;
            var call = (ulong)(1 + branch);
            branches.Branch($"b{branch}", async (state, _, cancellationToken) =>
            {
                timings.Branch[(int)state.Run * Branches + branch] = await model.CallAsync(state.Run, call, cancellationToken);
                return state with { Findings = [.. state.Findings, Finding(branch, state.Run)] };
            });
        }

        return new StateGraph<BurstState>()
            .AddNode("Plan", async (state, _, cancellationToken) =>
            {
                timings.Plan[(int)state.Run] = await model.CallAsync(state.Run, 0, cancellationToken);
                return state with { Plan = string.Create(CultureInfo.InvariantCulture, $"plan-{state.Run}") };
            })
            .AddParallelGroup("Research", branches, Merge)
            .AddNode("Answer", async (state, _, cancellationToken) =>
            {
                timings.Answer[(int)state.Run] = await model.CallAsync(state.Run, Branches + 1, cancellationToken);
                return state with { Answer = ExpectedAnswer(state.Run, state.Findings.Count) };
            })
            .AddEdge("Plan", "Research")
            .AddEdge("Research", "Answer")
            .SetEntryPoint("Plan")
            .Compile(new CompilationOptions { GraphId = "stress-burst", GraphVersion = "1" });
    }

    public static string Finding(int branch, ulong run) => string.Create(CultureInfo.InvariantCulture, $"f{branch}-{run}");

    public static string ExpectedAnswer(ulong run, int findings) => string.Create(CultureInfo.InvariantCulture, $"answer-{run}-{findings}");

    /// <summary>
    /// The group's reducer: each branch's new findings, appended in the order the branches were declared (which is
    /// the order the engine hands them over), whatever order they finished in.
    /// </summary>
    private static BurstState Merge(BurstState entry, IReadOnlyList<BurstState> outputs)
    {
        var before = entry.Findings.Count;
        var findings = new List<string>(before + outputs.Count);
        findings.AddRange(entry.Findings);
        foreach (var output in outputs)
        {
            for (var k = before; k < output.Findings.Count; k++)
            {
                findings.Add(output.Findings[k]);
            }
        }

        return entry with { Findings = findings };
    }
}
