using System.Globalization;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// A scenario's command line (SCENARIOS.md, "Running a scenario"): <c>[mode...] [--quick] [--json PATH] [--seed N]</c>
/// and the scenario's own <c>--name value</c> options. Every option has the same name in the Rust twin.
/// </summary>
public sealed class ScenarioArgs
{
    private readonly Dictionary<string, string> _values;

    private ScenarioArgs(IReadOnlyList<string> positional, Dictionary<string, string> values, bool quick)
    {
        Positional = positional;
        _values = values;
        Quick = quick;
    }

    /// <summary>The words before the options, e.g. a child process's mode.</summary>
    public IReadOnlyList<string> Positional { get; }

    /// <summary>Small sizes for CI. A quick run checks that the scenario still runs; it measures nothing.</summary>
    public bool Quick { get; }

    public ulong Seed => _values.TryGetValue("seed", out var s) ? ulong.Parse(s, CultureInfo.InvariantCulture) : 1;

    /// <summary>Where the JSON goes: <c>--json</c>, or <c>artifacts/results/&lt;scenario&gt;.json</c>.</summary>
    public string JsonPath(string scenario) =>
        _values.TryGetValue("json", out var path) ? path : Path.Combine("artifacts", "results", $"{scenario}.json");

    public bool Has(string name) => _values.ContainsKey(name);

    public long Long(string name, long full, long quick) =>
        _values.TryGetValue(name, out var s) ? ParseLong(name, s) : Quick ? quick : full;

    public string String(string name, string full, string quick) =>
        _values.TryGetValue(name, out var s) ? s : Quick ? quick : full;

    /// <summary>A comma-separated list of whole numbers, e.g. <c>--runs 1000,10000</c>.</summary>
    public IReadOnlyList<long> Longs(string name, IReadOnlyList<long> full, IReadOnlyList<long> quick) =>
        _values.TryGetValue(name, out var s)
            ? [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => ParseLong(name, v))]
            : Quick ? quick : full;

    private static long ParseLong(string name, string value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new UsageException($"--{name} takes whole numbers, not '{value}'");

    /// <summary>
    /// Parses <paramref name="args"/>. <paramref name="options"/> are the scenario's own option names, without the
    /// dashes; <c>quick</c>, <c>json</c> and <c>seed</c> are always known.
    /// </summary>
    /// <exception cref="UsageException">An unknown option, or one without its value.</exception>
    public static ScenarioArgs Parse(IReadOnlyList<string> args, params string[] options)
    {
        var known = new HashSet<string>(options) { "json", "seed" };
        var positional = new List<string>();
        var values = new Dictionary<string, string>();
        var quick = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (values.Count > 0 || quick)
                {
                    throw new UsageException($"'{arg}' after the options: a mode comes first");
                }

                positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (name == "quick")
            {
                quick = true;
            }
            else if (!known.Contains(name))
            {
                throw new UsageException($"unknown option '{arg}'");
            }
            else if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"'{arg}' needs a value");
            }
            else
            {
                values[name] = args[++i];
            }
        }

        if (values.TryGetValue("seed", out var seed) && !ulong.TryParse(seed, CultureInfo.InvariantCulture, out _))
        {
            throw new UsageException($"--seed takes a whole number from 0 to {ulong.MaxValue}, not '{seed}'");
        }

        return new ScenarioArgs(positional, values, quick);
    }

    /// <summary>The arguments to pass a child process so it runs with the same options.</summary>
    public IEnumerable<string> Forward()
    {
        if (Quick)
        {
            yield return "--quick";
        }

        foreach (var (name, value) in _values)
        {
            yield return $"--{name}";
            yield return value;
        }
    }
}

/// <summary>A command line the scenario can't run with. Exit code 2.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>Something the scenario needs and can't reach, such as Postgres. Exit code 2.</summary>
public sealed class EnvironmentException(string message) : Exception(message);
