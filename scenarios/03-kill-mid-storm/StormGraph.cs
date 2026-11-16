using System.Globalization;
using System.Text.Json.Serialization;
using Datalium.Strata.Graph;
using Datalium.Strata.Stress.Harness;

namespace Datalium.Strata.Stress.Storm;

/// <summary>An order's state (SCENARIOS.md, "3. Kill it mid-storm", "Graph and state"), with the spec's JSON field names.</summary>
internal sealed record StormState(
    [property: JsonPropertyName("order")] ulong Order,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("validated")] bool Validated,
    [property: JsonPropertyName("charge_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChargeId,
    [property: JsonPropertyName("shipped")] bool Shipped)
{
    public static StormState Start(ulong order) => new(order, StormGraph.AmountCents(order), false, null, false);
}

/// <summary>
/// Graph <c>stress-storm</c>, version <c>1</c>: <c>Validate</c> (call 0), <c>Charge</c> (call 1, then the charge)
/// and <c>Ship</c> (call 2), compiled with <c>EveryNode</c>, so a thread whose worker died can be recovered. Each
/// node appends its attempt to the ledger first, before its model call, so a node run killed at any point shows.
/// </summary>
internal static class StormGraph
{
    public const string ThreadPrefix = "order-";

    /// <summary>A thread id's order, zero-padded to 6 digits.</summary>
    public const int OrderDigits = 6;

    public const long MaxOrders = 1_000_000;

    public static CompiledGraph<StormState> Build(SimulatedModel model, Ledger ledger, Action<long> late) =>
        new StateGraph<StormState>()
            .AddNode("Validate", async (state, context, cancellationToken) =>
            {
                await ledger.AppendAttemptAsync(context, cancellationToken);
                await CallAsync(model, state.Order, 0, late, cancellationToken);
                return state with { Validated = true };
            })
            .AddNode("Charge", async (state, context, cancellationToken) =>
            {
                await ledger.AppendAttemptAsync(context, cancellationToken);
                await CallAsync(model, state.Order, 1, late, cancellationToken);

                // The effect outside the state, after the model call: a kill can land between it and the checkpoint.
                var chargeId = await ledger.ChargeAsync(context, state.Order, state.AmountCents, cancellationToken);
                return state with { ChargeId = chargeId };
            })
            .AddNode("Ship", async (state, context, cancellationToken) =>
            {
                await ledger.AppendAttemptAsync(context, cancellationToken);
                await CallAsync(model, state.Order, 2, late, cancellationToken);
                return state with { Shipped = true };
            })
            .AddEdge("Validate", "Charge")
            .AddEdge("Charge", "Ship")
            .SetEntryPoint("Validate")
            .Compile(new CompilationOptions
            {
                GraphId = "stress-storm",
                GraphVersion = "1",
                Checkpointing = CheckpointPolicy.EveryNode,
            });

    /// <summary>A model call, handing how late its timer woke (measured wait minus the latency asked for), in µs, to <paramref name="late"/>.</summary>
    private static async Task CallAsync(SimulatedModel model, ulong order, ulong call, Action<long> late, CancellationToken cancellationToken)
    {
        var waited = await model.CallAsync(order, call, cancellationToken);
        late((long)Ticks.ToMicroseconds(waited) - model.LatencyMicroseconds(order, call));
    }

    public static long AmountCents(ulong order) => 100 + (long)(order % 10_000);

    /// <summary><c>order-000042</c>.</summary>
    public static string ThreadId(long order) =>
        ThreadPrefix + order.ToString("D" + OrderDigits, CultureInfo.InvariantCulture);

    /// <summary>The order of an <c>order-</c> thread id, or -1 for any other id.</summary>
    public static long Order(string threadId) =>
        threadId.Length == ThreadPrefix.Length + OrderDigits
        && threadId.StartsWith(ThreadPrefix, StringComparison.Ordinal)
        && long.TryParse(threadId.AsSpan(ThreadPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var order)
            ? order
            : -1;

    /// <summary>What the ledger returns for an order: <c>ch_</c> and the order, zero-padded to 6 digits.</summary>
    public static string ChargeId(ulong order) =>
        "ch_" + order.ToString("D" + OrderDigits, CultureInfo.InvariantCulture);
}
