using Cadence.Storage.Sql.Internal;

namespace Cadence.Storage.Sql;

/// <summary>
/// The SQL tier's half of the janitor: five set operations and nothing else.
/// </summary>
/// <remarks>
/// A separate class rather than another face on <see cref="SqlRunHistoryStore"/>, because the
/// instance table is not run history and the store has no other reason to know about it. Keeping
/// them apart also means the store's public surface stays the one the scheduler and the dashboard
/// use, with the tidying operations reachable only through this seam.
/// </remarks>
public sealed class SqlStorageMaintenance : IStorageMaintenance
{
    private readonly SqlDatabase _database;
    private readonly SqlRunHistoryStore _history;
    private readonly SqlApiTokenStore _tokens;

    internal SqlStorageMaintenance(SqlDatabase database, SqlRunHistoryStore history, SqlApiTokenStore tokens)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(tokens);

        _database = database;
        _history = history;
        _tokens = tokens;
    }

    /// <inheritdoc />
    public Task<int> ReapAbandonedRunsAsync(
        DateTimeOffset heartbeatDeadline,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
        => _history.ReapAbandonedAsync(heartbeatDeadline, now, batchSize, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeRunsByAgeAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken)
        => _history.PurgeByAgeAsync(olderThan, batchSize, cancellationToken);

    /// <inheritdoc />
    public Task<int> TrimRunsPerJobAsync(
        int maxRunsPerJob,
        int batchSize,
        CancellationToken cancellationToken)
        => _history.TrimPerJobAsync(maxRunsPerJob, batchSize, cancellationToken);

    /// <inheritdoc />
    public async Task<int> PurgeDeadInstancesAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken)
    {
        return await _database.ExecuteAsync(
            $"""
            DELETE TOP (@BatchSize)
            FROM {_database.Table("CadenceInstance")}
            WHERE LastHeartbeatUtc < @Cutoff;
            """,
            command =>
            {
                command.Parameters.AddWithValue("@BatchSize", batchSize);
                SqlValues.AddInstant(command, "@Cutoff", olderThan);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> PurgeExpiredApiTokensAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
        => _tokens.PurgeExpiredAsync(now, batchSize, cancellationToken);
}
