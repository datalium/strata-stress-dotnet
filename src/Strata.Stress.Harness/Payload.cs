using System.Globalization;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// The 1,024 characters a thread carries (SCENARIOS.md, scenarios 2 and 4): the lower-case hex, 16 digits each, of
/// <c>hash(seed, n, 1000 + k)</c> for <c>k</c> from 0 to 63, joined. It stands for a conversation, doesn't compress,
/// and is the same in both engines.
/// </summary>
public static class Payload
{
    public const int Parts = 64;

    public const int Length = Parts * 16;

    /// <summary>The payload of thread (or run) <paramref name="n"/>.</summary>
    public static string Of(ulong seed, ulong n) =>
        string.Create(Length, (seed, n), static (chars, key) =>
        {
            for (var k = 0; k < Parts; k++)
            {
                SplitMix64.Hash(key.seed, key.n, 1000UL + (ulong)k).TryFormat(chars.Slice(k * 16, 16), out _, "x16", CultureInfo.InvariantCulture);
            }
        });
}
