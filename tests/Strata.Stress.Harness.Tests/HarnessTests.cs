using Xunit;

namespace Datalium.Strata.Stress.Harness.Tests;

/// <summary>
/// The harness's contract with the Rust twin: these vectors are in SCENARIOS.md, and the Rust harness has the same
/// tests. If one changes, both change, and the spec with them.
/// </summary>
public sealed class SplitMix64Tests
{
    [Theory]
    [InlineData(0UL, 0xE220A8397B1DCDAFUL)]
    [InlineData(1UL, 0x910A2DEC89025CC1UL)]
    [InlineData(ulong.MaxValue, 0xE4D971771B652C20UL)]
    public void Mix_matches_the_spec(ulong input, ulong expected) => Assert.Equal(expected, SplitMix64.Mix(input));

    [Theory]
    [InlineData(1UL, 0UL, 0UL, 12793040940332582595UL)]
    [InlineData(1UL, 0UL, 1UL, 7806873273932414515UL)]
    [InlineData(1UL, 1UL, 0UL, 6301985355436268297UL)]
    [InlineData(1UL, 9999UL, 9UL, 1449011636669827163UL)]
    [InlineData(42UL, 123456789UL, 3UL, 17597405645336132696UL)]
    public void Hash_matches_the_spec(ulong seed, ulong run, ulong call, ulong expected) =>
        Assert.Equal(expected, SplitMix64.Hash(seed, run, call));

    [Theory]
    [InlineData(1UL, 0UL, 0UL, 60310L)]
    [InlineData(1UL, 0UL, 1UL, 28742L)]
    [InlineData(1UL, 1UL, 0UL, 73339L)]
    [InlineData(1UL, 9999UL, 9UL, 79191L)]
    [InlineData(42UL, 123456789UL, 3UL, 70528L)]
    public void Latency_matches_the_spec(ulong seed, ulong run, ulong call, long expectedMicros) =>
        Assert.Equal(expectedMicros, new SimulatedModel(seed, LatencyRange.Milliseconds(20, 80)).LatencyMicroseconds(run, call));

    [Fact]
    public void A_fixed_latency_is_that_latency() =>
        Assert.Equal(5000, LatencyRange.Milliseconds(5, 5).Draw(7, 8, 9));

    [Fact]
    public void Latencies_stay_in_range()
    {
        var range = LatencyRange.Milliseconds(20, 80);
        for (ulong run = 0; run < 10_000; run++)
        {
            var micros = range.Draw(1, run, run % 10);
            Assert.InRange(micros, 20_000, 80_000);
        }
    }

    [Fact]
    public async Task A_zero_latency_call_returns_without_a_timer()
    {
        var model = new SimulatedModel(1, LatencyRange.None);
        var call = model.CallAsync(0, 0, TestContext.Current.CancellationToken);
        Assert.True(call.IsCompletedSuccessfully);
        Assert.True(await call >= 0);
    }
}

public sealed class DistributionTests
{
    [Fact]
    public void Percentiles_are_nearest_rank()
    {
        var values = Enumerable.Range(1, 1000).Select(i => (double)i).Reverse().ToArray();
        var d = Distribution.Of(values);
        Assert.Equal(1000, d.Count);
        Assert.Equal(500.5, d.Mean);
        Assert.Equal(500, d.P50);
        Assert.Equal(900, d.P90);
        Assert.Equal(990, d.P99);
        Assert.Equal(999, d.P999);
        Assert.Equal(1000, d.Max);
    }

    [Fact]
    public void A_small_sample_rounds_its_rank_up()
    {
        double[] values = [10, 20, 30];
        var d = Distribution.Of(values);
        Assert.Equal(20, d.P50);  // ⌈0.5 · 3⌉ = 2
        Assert.Equal(30, d.P90);  // ⌈0.9 · 3⌉ = 3
        Assert.Equal(30, d.P999);
    }

    [Fact]
    public void One_value_is_every_percentile()
    {
        double[] values = [7];
        Assert.Equal(new Distribution(1, 7, 7, 7, 7, 7, 7), Distribution.Of(values));
    }

    [Fact]
    public void No_values_is_empty() => Assert.Equal(Distribution.Empty, Distribution.Of([]));
}

public sealed class ScenarioArgsTests
{
    [Fact]
    public void Defaults_follow_quick()
    {
        var full = ScenarioArgs.Parse([], "runs");
        var quick = ScenarioArgs.Parse(["--quick"], "runs");
        Assert.Equal(10_000, full.Long("runs", 10_000, 100));
        Assert.Equal(100, quick.Long("runs", 10_000, 100));
        Assert.Equal(1UL, full.Seed);
        Assert.Equal(Path.Combine("artifacts", "results", "01-burst.json"), full.JsonPath("01-burst"));
    }

    [Fact]
    public void An_option_beats_the_default()
    {
        var args = ScenarioArgs.Parse(["--quick", "--runs", "1000,5000", "--seed", "42", "--json", "out.json"], "runs");
        Assert.Equal([1000L, 5000L], args.Longs("runs", [1], [2]));
        Assert.Equal(42UL, args.Seed);
        Assert.Equal("out.json", args.JsonPath("01-burst"));
    }

    [Fact]
    public void A_mode_comes_first()
    {
        var args = ScenarioArgs.Parse(["worker", "3", "--quick"]);
        Assert.Equal(["worker", "3"], args.Positional);
        Assert.Throws<UsageException>(() => ScenarioArgs.Parse(["--quick", "worker"]));
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("--runs")]
    [InlineData("--seed", "-1")]
    public void Bad_options_are_usage_errors(params string[] args) =>
        Assert.Throws<UsageException>(() => ScenarioArgs.Parse(args, "runs"));

    [Theory]
    [InlineData("x")]
    [InlineData("10,x")]
    [InlineData("1.5")]
    public void A_number_that_isnt_one_is_a_usage_error(string value)
    {
        var args = ScenarioArgs.Parse(["--runs", value], "runs");
        Assert.Throws<UsageException>(() => args.Longs("runs", [1], [1]));
        Assert.Throws<UsageException>(() => args.Long("runs", 1, 1));
    }

    [Fact]
    public void Forward_repeats_the_options()
    {
        var args = ScenarioArgs.Parse(["--quick", "--seed", "9"]);
        Assert.Equal(["--quick", "--seed", "9"], args.Forward());
    }
}

public sealed class ReportTests
{
    [Fact]
    public async Task A_failed_check_fails_the_run()
    {
        var json = Path.Combine(Path.GetTempPath(), $"strata-stress-test-{Guid.NewGuid():N}.json");
        try
        {
            var report = new Report("00-test", ScenarioArgs.Parse(["--quick", "--json", json]));
            report.Columns(new Column("runs", "Runs"));
            report.Row(new() { ["runs"] = 3L });
            report.Summary["lost"] = 0L;
            report.Check("holds", true);
            report.Check("doesn't", false, "on purpose");
            var output = new StringWriter();

            Assert.Equal(1, await report.FinishAsync(output));
            var text = await File.ReadAllTextAsync(json, TestContext.Current.CancellationToken);
            Assert.Contains("\"format\": \"strata-stress/1\"", text);
            Assert.Contains("\"engine\": \"dotnet\"", text);
            Assert.Contains("\"quick\": true", text);
            Assert.Contains("FAIL doesn't: on purpose", output.ToString());
            Assert.Contains("| 3 |", output.ToString());
            Assert.Contains("\"summary\": {\n    \"lost\": 0\n  }", text.ReplaceLineEndings("\n"));
            Assert.Contains("- `lost`: 0", output.ToString());
        }
        finally
        {
            File.Delete(json);
        }
    }

    [Theory]
    [InlineData(12345L, "12,345")]
    [InlineData(1234.6, "1,235")]
    [InlineData(12.345, "12.3")]
    [InlineData(1.2345, "1.23")]
    public void Cells_are_rounded_for_reading(object value, string expected) => Assert.Equal(expected, Report.Cell(value));

    [Fact]
    public void A_column_can_make_its_cell_from_the_row()
    {
        var report = new Report("00-test", ScenarioArgs.Parse(["--quick"]));
        report.Columns(
            new Column("latency_min_ms", "Latency", Text: row => $"{row["latency_min_ms"]}–{row["latency_max_ms"]} ms"),
            Column.Mebibytes("peak_resident_bytes", "Peak MiB"));
        report.Row(new() { ["latency_min_ms"] = 20.0, ["latency_max_ms"] = 80.0, ["peak_resident_bytes"] = 3L * 1024 * 1024 * 1024 });
        var output = new StringWriter();

        report.WriteMarkdown(output);

        Assert.Contains("| 20–80 ms | 3,072 |", output.ToString());
    }

    [Fact]
    public void Passwords_are_redacted() =>
        Assert.Equal("postgres://strata:***@localhost:5432/strata", Services.Redact("postgres://strata:strata@localhost:5432/strata"));
}

public sealed class PayloadTests
{
    [Fact]
    public void A_payload_is_1024_lower_case_hex_digits()
    {
        var payload = Payload.Of(1, 0);
        Assert.Equal(1024, payload.Length);
        Assert.All(payload, c => Assert.True(char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c), $"'{c}' is not lower-case hex"));
    }

    [Fact]
    public void Each_part_is_the_hash_of_call_1000_plus_k_zero_padded()
    {
        var payload = Payload.Of(42, 123456789);
        for (var k = 0; k < Payload.Parts; k++)
        {
            Assert.Equal(SplitMix64.Hash(42, 123456789, 1000UL + (ulong)k).ToString("x16"), payload.Substring(k * 16, 16));
        }
    }

    [Fact]
    public void Each_thread_has_its_own() => Assert.NotEqual(Payload.Of(1, 0), Payload.Of(1, 1));
}
