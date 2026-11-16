namespace Datalium.Strata.Stress.Harness;

/// <summary>A bounded loop over numbers: what a scenario's "at most <c>--in-flight</c> at once" means.</summary>
public static class Concurrently
{
    /// <summary>
    /// Runs <c>body(i)</c> for every <c>i</c> from <paramref name="from"/> up to <paramref name="to"/>, at most
    /// <paramref name="inFlight"/> at once, each on the thread pool, taking the numbers in order.
    /// </summary>
    public static async Task ForEachAsync(long from, long to, int inFlight, Func<long, Task> body)
    {
        var next = from - 1;
        var workers = (int)Math.Min(inFlight, Math.Max(0, to - from));
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            long i;
            while ((i = Interlocked.Increment(ref next)) < to)
            {
                await body(i);
            }
        })));
    }
}
