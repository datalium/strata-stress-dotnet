using Datalium.Strata.Storage;
using Datalium.Strata.Storage.Postgres;
using Npgsql;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// The Postgres the scenarios use (SCENARIOS.md, "Services"): reaching it, emptying the checkpoint store, its size,
/// and what the JSON records about it. Npgsql is the version <c>Datalium.Strata.Storage</c> brings, so the scenarios
/// talk to Postgres with the same client the store does.
/// </summary>
public sealed class StressPostgres : IAsyncDisposable
{
    /// <summary>The checkpoint store's tables (<c>PostgresCheckpointStore</c>'s DDL), which its size sums.</summary>
    public static readonly IReadOnlyList<string> CheckpointTables = ["checkpoints", "checkpoint_threads", "checkpoint_leases", "checkpoint_blobs"];

    private readonly NpgsqlDataSource _dataSource;

    private StressPostgres(string url, NpgsqlDataSource dataSource, string serverVersion)
    {
        Url = url;
        _dataSource = dataSource;
        ServerVersion = serverVersion;
    }

    /// <summary><c>STRATA_STRESS_POSTGRES_URL</c>, or the default.</summary>
    public string Url { get; }

    /// <summary>The server's <c>server_version</c>, e.g. <c>16.11</c>.</summary>
    public string ServerVersion { get; }

    /// <summary>
    /// The pool this connects with: for a scenario's own tables (scenario 3's ledger), never the store's, which
    /// has a pool of its own.
    /// </summary>
    public NpgsqlDataSource DataSource => _dataSource;

    /// <summary>The size of a store's connection pool: Npgsql's default unless the URL changes it.</summary>
    public int PoolSize => new NpgsqlConnectionStringBuilder(PostgresConnection.ToConnectionString(Url)).MaxPoolSize;

    /// <summary>Connects to <see cref="Services.PostgresUrl"/>, or says how to start it.</summary>
    /// <exception cref="EnvironmentException">Postgres can't be reached.</exception>
    public static async Task<StressPostgres> ConnectAsync()
    {
        var url = Services.PostgresUrl;
        NpgsqlDataSource? dataSource = null;
        try
        {
            dataSource = NpgsqlDataSource.Create(PostgresConnection.ToConnectionString(url));
            await using var command = dataSource.CreateCommand("SHOW server_version");
            var version = (string)(await command.ExecuteScalarAsync() ?? "unknown");
            return new StressPostgres(url, dataSource, version.Split(' ')[0]);
        }
        catch (Exception e) when (e is NpgsqlException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            if (dataSource is not null)
            {
                await dataSource.DisposeAsync();
            }

            throw new EnvironmentException(
                $"can't reach Postgres at {Services.Redact(url)}: {(e.InnerException ?? e).Message}\n" +
                $"  start it with `docker compose up -d --wait`, or set {Services.PostgresVariable}");
        }
    }

    /// <summary>
    /// A checkpoint store on this database, its tables created, with <paramref name="groupCommit"/> (off when
    /// <see langword="null"/>) and every other setting the store's default.
    /// </summary>
    public Task<PostgresCheckpointStore> OpenCheckpointStoreAsync(GroupCommitOptions? groupCommit) =>
        PostgresCheckpointStore.OpenAsync(Url, new PostgresStoreOptions { GroupCommit = groupCommit });

    /// <summary>
    /// Removes the checkpoint store's tables and its trigger, and forgets their schema version, so the next store
    /// opened creates them empty (SCENARIOS.md, "Every scenario starts from an empty store").
    /// </summary>
    public async Task DropCheckpointStoreAsync()
    {
        await ExecuteAsync($"DROP TABLE IF EXISTS {string.Join(", ", CheckpointTables)} CASCADE");
        await ExecuteAsync("DROP FUNCTION IF EXISTS strata_checkpoints_head() CASCADE");
        await using var exists = _dataSource.CreateCommand($"SELECT to_regclass('{StoreSchemaVersions.Table}') IS NOT NULL");
        if (await exists.ExecuteScalarAsync() is true)
        {
            await using var forget = _dataSource.CreateCommand($"DELETE FROM {StoreSchemaVersions.Table} WHERE component = $1");
            forget.Parameters.Add(new NpgsqlParameter { Value = StoreSchemaVersions.Checkpoints });
            await forget.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The checkpoint store's tables, summed: <c>pg_total_relation_size</c>, indexes and TOAST included.</summary>
    public async Task<long> CheckpointStoreBytesAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT COALESCE(SUM(pg_total_relation_size(t)), 0)::bigint FROM unnest($1::text[]) AS n, to_regclass(n) AS t WHERE t IS NOT NULL");
        command.Parameters.Add(new NpgsqlParameter { Value = CheckpointTables.ToArray() });
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>
    /// What the JSON's <c>parameters</c> records about the store (SCENARIOS.md, "Services"): the server's version,
    /// group commit with its batch and flushes, and the pool's size. Never the URL, which holds a password.
    /// </summary>
    public void Describe(OrderedDictionary<string, object?> parameters, GroupCommitOptions? groupCommit)
    {
        parameters["postgres_version"] = ServerVersion;
        parameters["group_commit"] = groupCommit is null ? "off" : "on";
        if (groupCommit is not null)
        {
            parameters["group_commit_max_batch"] = (long)groupCommit.MaxBatch;
            parameters["group_commit_max_flushes"] = (long)groupCommit.MaxConcurrentFlushes;
        }

        parameters["pool_size"] = (long)PoolSize;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
