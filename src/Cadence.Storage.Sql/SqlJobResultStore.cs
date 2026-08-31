using System.Data;
using Cadence.Storage.Sql.Internal;
using Microsoft.Data.SqlClient;

namespace Cadence.Storage.Sql;

/// <summary>Keeps run results in <c>CadenceJobResult</c>, streamed out rather than buffered.</summary>
/// <remarks>
/// <see cref="OpenAsync"/> is the only read in the SQL tier that hands back a live connection.
/// <c>CommandBehavior.SequentialAccess</c> plus <see cref="SqlDataReader.GetStream"/> is what lets a
/// forty megabyte download leave the server a buffer at a time instead of materialising whole in
/// this process first, and the price of that is a connection held for the length of the transfer.
/// <see cref="StoredJobResult"/> owns both and closes them together.
/// </remarks>
public sealed class SqlJobResultStore : IJobResultStore
{
    private readonly SqlDatabase _database;

    /// <summary>Creates the store.</summary>
    /// <param name="database">Opens connections and applies the configured schema and timeout.</param>
    internal SqlJobResultStore(SqlDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        Guid runId,
        JobResult result,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        var table = _database.Table("CadenceJobResult");

        // MERGE would be one statement; this is two predictable ones. A run stores its result once,
        // so the update branch only fires on a retry that got as far as writing.
        await _database.ExecuteAsync(
            $"""
            UPDATE {table}
               SET ContentType = @ContentType,
                   FileName = @FileName,
                   Length = @Length,
                   Content = @Content,
                   CreatedAtUtc = @CreatedAtUtc,
                   ExpiresAtUtc = @ExpiresAtUtc
             WHERE RunId = @RunId;

            IF @@ROWCOUNT = 0
            INSERT INTO {table}
                (RunId, ContentType, FileName, Length, Content, CreatedAtUtc, ExpiresAtUtc)
            VALUES
                (@RunId, @ContentType, @FileName, @Length, @Content, @CreatedAtUtc, @ExpiresAtUtc);
            """,
            command =>
            {
                SqlValues.AddGuid(command, "@RunId", runId);
                SqlValues.AddText(command, "@ContentType", result.ContentType, 200);
                SqlValues.AddText(command, "@FileName", result.FileName, 260);
                command.Parameters.Add("@Length", SqlDbType.BigInt).Value = result.Length;

                var content = command.Parameters.Add("@Content", SqlDbType.VarBinary, -1);
                content.Value = result.Content.ToArray();

                SqlValues.AddInstant(command, "@CreatedAtUtc", DateTimeOffset.UtcNow);
                SqlValues.AddInstant(command, "@ExpiresAtUtc", expiresAt);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobResultInfo?> DescribeAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Content is deliberately not selected: describing a result must not cost what reading one does.
        var rows = await _database.QueryAsync(
            $"""
            SELECT ContentType, FileName, Length, CreatedAtUtc, ExpiresAtUtc
              FROM {_database.Table("CadenceJobResult")}
             WHERE RunId = @RunId;
            """,
            command => SqlValues.AddGuid(command, "@RunId", runId),
            reader => Read(reader, runId),
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<StoredJobResult?> OpenAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqlCommand? command = null;
        SqlDataReader? reader = null;

        try
        {
            command = _database.Command(
                connection,
                $"""
                SELECT ContentType, FileName, Length, CreatedAtUtc, ExpiresAtUtc, Content
                  FROM {_database.Table("CadenceJobResult")}
                 WHERE RunId = @RunId;
                """);

            SqlValues.AddGuid(command, "@RunId", runId);

            // SequentialAccess is what makes GetStream a stream rather than a buffer handed back
            // after the whole row has already been read into memory. It also fixes the column order:
            // Content is selected last because nothing may be read after it.
            reader = await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await DisposeAllAsync(reader, command, connection).ConfigureAwait(false);
                return null;
            }

            var info = Read(reader, runId);

            return new StoredJobResult(
                info,
                reader.GetStream(5),
                new ReaderLifetime(reader, command, connection));
        }
        catch
        {
            await DisposeAllAsync(reader, command, connection).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid runId, CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            $"DELETE FROM {_database.Table("CadenceJobResult")} WHERE RunId = @RunId;",
            command => SqlValues.AddGuid(command, "@RunId", runId),
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> PurgeAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            $"""
            DELETE TOP (@BatchSize) FROM {_database.Table("CadenceJobResult")}
             WHERE ExpiresAtUtc <= @Now;
            """,
            command =>
            {
                command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = Math.Max(1, batchSize);
                SqlValues.AddInstant(command, "@Now", now);
            },
            cancellationToken).ConfigureAwait(false);

    private static JobResultInfo Read(SqlDataReader reader, Guid runId) => new()
    {
        RunId = runId,
        ContentType = reader.GetString(0),
        FileName = SqlValues.GetStringOrNull(reader, 1),
        Length = reader.GetInt64(2),
        CreatedAt = SqlValues.GetInstant(reader, 3),
        ExpiresAt = SqlValues.GetInstant(reader, 4),
    };

    private static async ValueTask DisposeAllAsync(
        SqlDataReader? reader,
        SqlCommand? command,
        SqlConnection connection)
    {
        if (reader is not null)
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        if (command is not null)
        {
            await command.DisposeAsync().ConfigureAwait(false);
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class ReaderLifetime(
        SqlDataReader reader,
        SqlCommand command,
        SqlConnection connection) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => DisposeAllAsync(reader, command, connection);
    }
}
