namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// Where the external services are (SCENARIOS.md, "Services"). The defaults match docker-compose.yml; the variables
/// have the same names in the Rust twin.
/// </summary>
public static class Services
{
    public const string PostgresVariable = "STRATA_STRESS_POSTGRES_URL";

    public const string RedisVariable = "STRATA_STRESS_REDIS_URL";

    public static string PostgresUrl =>
        Environment.GetEnvironmentVariable(PostgresVariable) ?? "postgres://strata:strata@localhost:5432/strata";

    public static string RedisUrl =>
        Environment.GetEnvironmentVariable(RedisVariable) ?? "redis://localhost:6379";

    /// <summary><c>scheme://user:password@host/...</c> with the password masked, for messages and the JSON.</summary>
    public static string Redact(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.LastIndexOf('@');
        if (schemeEnd < 0 || at <= schemeEnd)
        {
            return url;
        }

        var userInfo = url[(schemeEnd + 3)..at];
        var colon = userInfo.IndexOf(':');
        return colon < 0 ? url : $"{url[..(schemeEnd + 3)]}{userInfo[..colon]}:***{url[at..]}";
    }
}
