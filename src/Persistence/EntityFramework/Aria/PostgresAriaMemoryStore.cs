// <copyright file="PostgresAriaMemoryStore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework.Aria;

using System.Threading;
using MUnique.OpenMU.Persistence.Aria;
using Npgsql;

/// <summary>A small independent pool for versioned PostgreSQL NPC memory tables.</summary>
public sealed class PostgresAriaMemoryStore : IAriaMemoryStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Initializes a new instance of the <see cref="PostgresAriaMemoryStore"/> class.</summary>
    /// <param name="connectionString">The private database connection string.</param>
    public PostgresAriaMemoryStore(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 2,
            MinPoolSize = 0,
            Timeout = 5,
            CommandTimeout = 5,
            ApplicationName = "OpenMU-Aria",
            IncludeErrorDetail = false,
            LogParameters = false,
        };
        this._dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>Uses the initialized game account database configuration.</summary>
    /// <returns>The memory store.</returns>
    public static PostgresAriaMemoryStore ForGameDatabase()
        => new(ConnectionConfigurator.GetConnectionString<AccountContext>());

    /// <inheritdoc />
    public async ValueTask<AriaMemory> LoadAsync(Guid characterId, string persona, CancellationToken cancellationToken)
    {
        var summary = string.Empty;
        long through = 0;
        await using (var command = this.Command("SELECT summary, through_id FROM aria.memory WHERE character_id=$1 AND persona=$2", characterId, persona))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                summary = reader.GetString(0);
                through = reader.GetInt64(1);
            }
        }

        var recent = await this.ReadTurnsAsync(
            "SELECT id, question, answer FROM aria.turn WHERE character_id=$1 AND persona=$2 ORDER BY id DESC LIMIT 6",
            characterId,
            persona,
            null,
            cancellationToken).ConfigureAwait(false);
        recent.Reverse();
        var pending = await this.ReadTurnsAsync(
            "SELECT id, question, answer FROM aria.turn WHERE character_id=$1 AND persona=$2 AND id>$3 ORDER BY id LIMIT 32",
            characterId,
            persona,
            through,
            cancellationToken).ConfigureAwait(false);
        return new AriaMemory(summary, through, recent, pending);
    }

    /// <inheritdoc />
    public async ValueTask<long> AppendAsync(Guid characterId, string persona, string question, string answer, string model, CancellationToken cancellationToken)
    {
        await using var command = this.Command(
            "INSERT INTO aria.turn(character_id,persona,question,answer,model) VALUES ($1,$2,$3,$4,$5) RETURNING id",
            characterId,
            persona);
        command.Parameters.AddWithValue(question);
        command.Parameters.AddWithValue(answer);
        command.Parameters.AddWithValue(model);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask SummarizeAsync(Guid characterId, string persona, long previousThroughId, long throughId, string summary, CancellationToken cancellationToken)
    {
        await using var command = this.Command(
            """
            INSERT INTO aria.memory(character_id,persona,summary,through_id) VALUES ($1,$2,$3,$4)
            ON CONFLICT(character_id,persona) DO UPDATE
            SET summary=EXCLUDED.summary, through_id=EXCLUDED.through_id, updated_at=now()
            WHERE aria.memory.through_id=$5
            """,
            characterId,
            persona);
        command.Parameters.AddWithValue(summary);
        command.Parameters.AddWithValue(throughId);
        command.Parameters.AddWithValue(previousThroughId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ForgetAsync(Guid characterId, string persona, CancellationToken cancellationToken)
    {
        await using var connection = await this._dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in new[] { "turn", "memory" })
        {
            await using var command = new NpgsqlCommand($"DELETE FROM aria.{table} WHERE character_id=$1 AND persona=$2", connection, transaction);
            command.Parameters.AddWithValue(characterId);
            command.Parameters.AddWithValue(persona);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask PruneAsync(int retentionDays, CancellationToken cancellationToken)
    {
        await using var command = this._dataSource.CreateCommand("DELETE FROM aria.turn WHERE created_at < now() - make_interval(days => $1)");
        command.Parameters.AddWithValue(retentionDays);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await this._dataSource.DisposeAsync().ConfigureAwait(false);

    private NpgsqlCommand Command(string sql, Guid characterId, string persona)
    {
        var command = this._dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(persona);
        return command;
    }

    private async ValueTask<List<AriaTurn>> ReadTurnsAsync(string sql, Guid characterId, string persona, long? through, CancellationToken cancellationToken)
    {
        await using var command = this.Command(sql, characterId, persona);
        if (through is { } value)
        {
            command.Parameters.AddWithValue(value);
        }

        var turns = new List<AriaTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            turns.Add(new AriaTurn(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return turns;
    }
}
