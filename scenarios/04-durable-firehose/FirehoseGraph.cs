using System.Globalization;
using System.Text.Json.Serialization;
using Datalium.Strata.Graph;
using Datalium.Strata.Stress.Harness;

namespace Datalium.Strata.Stress.Firehose;

/// <summary>A run's state (SCENARIOS.md, "4. Durable firehose", "Graph and state"), with the spec's JSON field names.</summary>
internal sealed record FirehoseState(
    [property: JsonPropertyName("run")] ulong Run,
    [property: JsonPropertyName("step")] long Step,
    [property: JsonPropertyName("payload")] string Payload)
{
    public static FirehoseState Start(ulong seed, ulong run) => new(run, 0, Harness.Payload.Of(seed, run));
}

/// <summary>
/// Graph <c>stress-firehose</c>, version <c>1</c>: ten async nodes in a line, <c>N0</c> to <c>N9</c>, compiled with
/// <c>EveryNode</c>. Node <c>Ni</c> is model call <c>i</c> and sets <c>step = i + 1</c>. A run writes eleven
/// checkpoints: <c>running</c> before <c>N0</c> and after <c>N0</c> to <c>N8</c>, then <c>completed</c>.
/// </summary>
internal static class FirehoseGraph
{
    public const int Nodes = 10;

    /// <summary>The checkpoints a run writes: one before the first node, one after each node but the last, and <c>completed</c>.</summary>
    public const int CheckpointsPerRun = Nodes + 1;

    public static CompiledGraph<FirehoseState> Build(SimulatedModel model, Waits waits)
    {
        var graph = new StateGraph<FirehoseState>();
        for (var i = 0; i < Nodes; i++)
        {
            var call = i;
            graph.AddNode(Node(call), async (state, context, cancellationToken) =>
            {
                var waited = await model.CallAsync(state.Run, (ulong)call, cancellationToken);
                waits.Record(state.Run, call, waited);
                return state with { Step = call + 1 };
            });
        }

        for (var i = 0; i + 1 < Nodes; i++)
        {
            graph.AddEdge(Node(i), Node(i + 1));
        }

        return graph
            .SetEntryPoint(Node(0))
            .Compile(new CompilationOptions
            {
                GraphId = "stress-firehose",
                GraphVersion = "1",
                Checkpointing = CheckpointPolicy.EveryNode,
            });
    }

    public static string Node(int i) => "N" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>firehose-{config}-{repeat}-{worker}-{n}</c>.</summary>
    public static string ThreadId(int config, int repeat, int worker, long n) =>
        string.Create(CultureInfo.InvariantCulture, $"firehose-{config}-{repeat}-{worker}-{n}");

    /// <summary>The run number: <c>n × concurrency + worker</c>, from 0 in each configuration and repeat.</summary>
    public static ulong RunNumber(int concurrency, int worker, long n) => ((ulong)n * (ulong)concurrency) + (ulong)worker;
}

/// <summary>
/// The measured waits of each worker's current run, outside the state, in memory allocated once per configuration:
/// a handler finds its worker from the run number (<c>run % concurrency</c>), and a worker runs one run at a time, so
/// no two handlers write the same slot at once.
/// </summary>
internal sealed class Waits(int concurrency)
{
    private readonly long[] _ticks = new long[concurrency * FirehoseGraph.Nodes];

    public void Record(ulong run, int call, long ticks) =>
        _ticks[((int)(run % (ulong)concurrency) * FirehoseGraph.Nodes) + call] = ticks;

    /// <summary>The waits of <paramref name="worker"/>'s run, in <see cref="System.Diagnostics.Stopwatch"/> ticks, call by call.</summary>
    public ReadOnlySpan<long> Of(int worker) => _ticks.AsSpan(worker * FirehoseGraph.Nodes, FirehoseGraph.Nodes);
}
