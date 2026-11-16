using Datalium.Strata.Graph;
using Npgsql;
using NpgsqlTypes;

namespace Datalium.Strata.Stress.Storm;

/// <summary>
/// The scenario's own two tables (SCENARIOS.md, "3. Kill it mid-storm"): the ledger, which deduplicates charges by
/// the node's idempotency key as a real payment API does, and every node run from every worker, appended. On the
/// harness's pool, not the store's.
/// </summary>
internal sealed class Ledger(NpgsqlDataSource dataSource, string worker)
{
    private const string Ddl = """
        CREATE TABLE stress_storm_ledger (
            idempotency_key text PRIMARY KEY,
            thread_id       text NOT NULL,
            charge_id       text NOT NULL,
            amount_cents    bigint NOT NULL
        );
        CREATE TABLE stress_storm_attempts (
            thread_id       text NOT NULL,
            node            text NOT NULL,
            idempotency_key text NOT NULL,
            worker          text NOT NULL,
            is_recovery     boolean NOT NULL,
            at              timestamptz NOT NULL DEFAULT now()
        );
        """;

    /// <summary>Drops the two tables and creates them empty (SCENARIOS.md, "Every scenario starts from an empty store").</summary>
    public static async Task ResetAsync(NpgsqlDataSource dataSource)
    {
        await using var drop = dataSource.CreateCommand("DROP TABLE IF EXISTS stress_storm_ledger, stress_storm_attempts");
        await drop.ExecuteNonQueryAsync();
        await using var create = dataSource.CreateCommand(Ddl);
        await create.ExecuteNonQueryAsync();
    }

    /// <summary>Records that this node is running, in this worker, with its key and whether it may have run before.</summary>
    public async Task AppendAttemptAsync(NodeContext context, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO stress_storm_attempts (thread_id, node, idempotency_key, worker, is_recovery) VALUES ($1, $2, $3, $4, $5)");
        command.Parameters.Add(Text(context.ThreadId));
        command.Parameters.Add(Text(context.NodePath));
        command.Parameters.Add(Text(context.IdempotencyKey));
        command.Parameters.Add(Text(worker));
        command.Parameters.Add(new NpgsqlParameter { Value = context.IsRecovery, NpgsqlDbType = NpgsqlDbType.Boolean });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Charges the order under the node's idempotency key: <c>INSERT ... ON CONFLICT DO NOTHING</c>, then the
    /// charge id the key has, in one round trip. A second charge under the same key returns the first's.
    /// </summary>
    public async Task<string> ChargeAsync(NodeContext context, ulong order, long amountCents, CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        var insert = new NpgsqlBatchCommand(
            "INSERT INTO stress_storm_ledger (idempotency_key, thread_id, charge_id, amount_cents) VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (idempotency_key) DO NOTHING");
        insert.Parameters.Add(Text(context.IdempotencyKey));
        insert.Parameters.Add(Text(context.ThreadId));
        insert.Parameters.Add(Text(StormGraph.ChargeId(order)));
        insert.Parameters.Add(new NpgsqlParameter { Value = amountCents, NpgsqlDbType = NpgsqlDbType.Bigint });
        var select = new NpgsqlBatchCommand("SELECT charge_id FROM stress_storm_ledger WHERE idempotency_key = $1");
        select.Parameters.Add(Text(context.IdempotencyKey));
        batch.BatchCommands.Add(insert);
        batch.BatchCommands.Add(select);
        return (string?)await batch.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException($"the ledger has no charge under key '{context.IdempotencyKey}' after charging it");
    }

    /// <summary>Every ledger row: the charges, by thread.</summary>
    public static async Task<List<Charge>> ChargesAsync(NpgsqlDataSource dataSource)
    {
        var charges = new List<Charge>();
        await using var command = dataSource.CreateCommand("SELECT thread_id, idempotency_key, charge_id FROM stress_storm_ledger");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            charges.Add(new Charge(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return charges;
    }

    /// <summary>Every node run in <c>stress_storm_attempts</c>.</summary>
    public static async Task<List<Attempt>> AttemptsAsync(NpgsqlDataSource dataSource)
    {
        var attempts = new List<Attempt>();
        await using var command = dataSource.CreateCommand("SELECT thread_id, node, idempotency_key, worker, is_recovery FROM stress_storm_attempts");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            attempts.Add(new Attempt(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        }

        return attempts;
    }

    private static NpgsqlParameter Text(string value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Text };
}

/// <summary>A ledger row.</summary>
internal sealed record Charge(string ThreadId, string IdempotencyKey, string ChargeId);

/// <summary>A node run, as <c>stress_storm_attempts</c> has it.</summary>
internal sealed record Attempt(string ThreadId, string Node, string IdempotencyKey, string Worker, bool IsRecovery);
