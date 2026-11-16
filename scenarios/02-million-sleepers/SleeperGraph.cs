using System.Globalization;
using System.Text.Json.Serialization;
using Datalium.Strata.Graph;
using Datalium.Strata.Stress.Harness;

namespace Datalium.Strata.Stress.Sleepers;

/// <summary>A thread's state (SCENARIOS.md, "2. Million sleepers", "Graph and state"), with the spec's JSON field names.</summary>
internal sealed record SleeperState(
    [property: JsonPropertyName("ticket")] ulong Ticket,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("intake")] string Intake,
    [property: JsonPropertyName("resolution"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Resolution)
{
    public static SleeperState Start(ulong seed, ulong ticket) => new(ticket, Harness.Payload.Of(seed, ticket), string.Empty, null);
}

/// <summary>
/// Graph <c>stress-sleepers</c>, version <c>1</c>: <c>Intake</c> (call 0), an interrupt boundary after it, and
/// <c>Resolve</c> (call 1), which names the reviewer the resume's decision carries. The default checkpoint policy,
/// <c>Boundaries</c>: one <c>interrupted</c> checkpoint when a thread suspends, its <c>resumed_at</c> copy when it is
/// resumed, and nothing when it completes.
/// </summary>
internal static class SleeperGraph
{
    /// <summary>Who approves every thread.</summary>
    public const string Reviewer = "stress";

    public const string SleeperPrefix = "sleeper-";

    public const string WarmupPrefix = "warmup-";

    /// <summary>A thread id's ticket, zero-padded to 7 digits.</summary>
    public const int TicketDigits = 7;

    public const long MaxThreads = 10_000_000;

    public static CompiledGraph<SleeperState> Build(SimulatedModel model) =>
        new StateGraph<SleeperState>()
            .AddNode("Intake", async (state, _, cancellationToken) =>
            {
                await model.CallAsync(state.Ticket, 0, cancellationToken);
                return state with { Intake = string.Create(CultureInfo.InvariantCulture, $"intake-{state.Ticket}") };
            })
            .AddInterruptBoundary("Intake")
            .AddNode("Resolve", async (state, context, cancellationToken) =>
            {
                await model.CallAsync(state.Ticket, 1, cancellationToken);
                var reviewer = context.Decision?.Reviewer
                    ?? throw new InvalidOperationException("Resolve ran without a decision: it runs only after an approval");
                return state with { Resolution = ExpectedResolution(state.Ticket, reviewer) };
            })
            .AddEdge("Intake", "Resolve")
            .SetEntryPoint("Intake")
            .Compile(new CompilationOptions { GraphId = "stress-sleepers", GraphVersion = "1" });

    /// <summary><c>sleeper-0000042</c>, or <c>warmup-0000042</c> for the warm-up's.</summary>
    public static string ThreadId(string prefix, long ticket) =>
        prefix + ticket.ToString("D" + TicketDigits, CultureInfo.InvariantCulture);

    /// <summary>The ticket of a <c>sleeper-</c> thread id, or -1 for any other id.</summary>
    public static long SleeperTicket(string threadId) =>
        threadId.Length == SleeperPrefix.Length + TicketDigits
        && threadId.StartsWith(SleeperPrefix, StringComparison.Ordinal)
        && long.TryParse(threadId.AsSpan(SleeperPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var ticket)
            ? ticket
            : -1;

    public static string ExpectedResolution(ulong ticket, string reviewer) =>
        string.Create(CultureInfo.InvariantCulture, $"resolved-{ticket}-{reviewer}");
}
