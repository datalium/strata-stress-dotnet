using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// A scenario's output (SCENARIOS.md, "Output"): a Markdown table on standard output and the same figures as JSON,
/// with the machine, the versions, the parameters and the checks. A failed check makes the exit code 1.
/// </summary>
public sealed class Report(string scenario, ScenarioArgs args)
{
    /// <summary>The JSON layout's version. A change to the keys changes it, in both engines at once.</summary>
    public const string Format = "strata-stress/1";

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly List<Column> _columns = [];
    private readonly List<OrderedDictionary<string, object?>> _rows = [];
    private readonly List<CheckResult> _checks = [];

    public string Scenario => scenario;

    public RunEnvironment Environment { get; } = RunEnvironment.Capture();

    /// <summary>What the run was asked to do: sizes, latencies, concurrency, store settings.</summary>
    public OrderedDictionary<string, object?> Parameters { get; } = [];

    /// <summary>The run's single figures (a rate, a count of runs lost), keyed as the JSON names them.</summary>
    public OrderedDictionary<string, object?> Summary { get; } = [];

    /// <summary>Figures only this engine has (e.g. .NET's allocated bytes). The Markdown table leaves them out.</summary>
    public OrderedDictionary<string, object?> EngineSpecific { get; } = [];

    public IReadOnlyList<CheckResult> Checks => _checks;

    /// <summary>The table's columns, in order: each a key of the rows.</summary>
    public Report Columns(params Column[] columns)
    {
        _columns.AddRange(columns);
        return this;
    }

    /// <summary>One configuration's figures, keyed as the JSON names them.</summary>
    public void Row(OrderedDictionary<string, object?> row) => _rows.Add(row);

    /// <summary>Records a claim the run checked. A failed one fails the run; it doesn't stop it.</summary>
    public bool Check(string name, bool passed, string? detail = null)
    {
        _checks.Add(new CheckResult(name, passed, detail));
        return passed;
    }

    /// <summary>Prints the table, writes the JSON, and returns the exit code: 0, or 1 when a check failed.</summary>
    public async Task<int> FinishAsync(TextWriter? output = null)
    {
        output ??= Console.Out;
        var finishedAt = DateTimeOffset.UtcNow;
        var path = args.JsonPath(scenario);
        await WriteJsonAsync(path, finishedAt);
        WriteMarkdown(output, path);
        return _checks.All(c => c.Passed) ? 0 : 1;
    }

    public async Task WriteJsonAsync(string path, DateTimeOffset finishedAt)
    {
        var document = new OrderedDictionary<string, object?>
        {
            ["format"] = Format,
            ["scenario"] = scenario,
            ["quick"] = args.Quick,
            ["seed"] = args.Seed,
            ["started_at"] = _startedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["finished_at"] = finishedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["environment"] = Environment,
            ["parameters"] = Parameters,
            ["rows"] = _rows,
            ["summary"] = Summary,
            ["checks"] = _checks,
            ["engine_specific"] = EngineSpecific,
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await using var file = File.Create(path);
        await JsonSerializer.SerializeAsync(file, document, JsonOptions);
        await file.WriteAsync("\n"u8.ToArray());
    }

    public void WriteMarkdown(TextWriter output, string? jsonPath = null)
    {
        var env = Environment;
        output.WriteLine();
        output.WriteLine($"## {scenario}: Strata {env.StrataVersion}, {env.Runtime}, {env.Cpu}, {env.LogicalCores} logical cores");
        output.WriteLine();
        if (args.Quick)
        {
            output.WriteLine("> **Quick run:** small sizes, to check the scenario still runs. These figures measure nothing.");
            output.WriteLine();
        }

        if (_columns.Count > 0 && _rows.Count > 0)
        {
            output.WriteLine("| " + string.Join(" | ", _columns.Select(c => c.Header)) + " |");
            output.WriteLine("|" + string.Concat(_columns.Select(c => c.Numeric ? "---:|" : ":---|")));
            foreach (var row in _rows)
            {
                output.WriteLine("| " + string.Join(" | ", _columns.Select(c => c.Text is null ? Cell(row.GetValueOrDefault(c.Key)) : c.Text(row))) + " |");
            }

            output.WriteLine();
        }

        if (Summary.Count > 0)
        {
            foreach (var (key, value) in Summary)
            {
                output.WriteLine($"- `{key}`: {Cell(value)}");
            }

            output.WriteLine();
        }

        var failed = _checks.Count(c => !c.Passed);
        output.WriteLine($"Checks: {_checks.Count - failed} passed, {failed} failed.");
        foreach (var check in _checks)
        {
            output.WriteLine($"- {(check.Passed ? "PASS" : "FAIL")} {check.Name}{(check.Detail is null ? string.Empty : $": {check.Detail}")}");
        }

        if (jsonPath is not null)
        {
            output.WriteLine();
            output.WriteLine($"JSON: {jsonPath}");
        }
    }

    /// <summary>A figure as the table shows it: whole numbers with separators, others to three significant places or so.</summary>
    public static string Cell(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "yes" : "no",
        int or long or ulong => string.Format(CultureInfo.InvariantCulture, "{0:N0}", value),
        double d when double.IsNaN(d) => "",
        double d when Math.Abs(d) >= 100 => d.ToString("N0", CultureInfo.InvariantCulture),
        double d when Math.Abs(d) >= 10 => d.ToString("N1", CultureInfo.InvariantCulture),
        double d => d.ToString("0.00", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}

/// <summary>
/// A column of the Markdown table: the row key it shows, and its header. <paramref name="Text"/>, when given, makes
/// the cell from the whole row instead (a range from two keys, bytes in MiB); the JSON keeps the keys as they are.
/// </summary>
public sealed record Column(string Key, string Header, bool Numeric = true, Func<OrderedDictionary<string, object?>, string>? Text = null)
{
    /// <summary>A column showing a key that holds bytes, in mebibytes.</summary>
    public static Column Mebibytes(string key, string header) =>
        new(key, header, Text: row => row.GetValueOrDefault(key) is { } v ? Report.Cell(Convert.ToDouble(v, CultureInfo.InvariantCulture) / (1024 * 1024)) : "");
}

/// <summary>A claim the run checked.</summary>
public sealed record CheckResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail);

/// <summary>A scenario's entry point: runs it, and maps a usage or environment problem to exit code 2.</summary>
public static class Scenario
{
    public static async Task<int> RunAsync(string name, string usage, Func<Task<int>> run)
    {
        try
        {
            return await run();
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"{name}: {e.Message}");
            Console.Error.WriteLine($"usage: {usage}");
            return 2;
        }
        catch (EnvironmentException e)
        {
            Console.Error.WriteLine($"{name}: {e.Message}");
            return 2;
        }
    }
}
