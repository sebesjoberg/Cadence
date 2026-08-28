using System.Data;
using Cadence.Storage.Sql.Internal;
using Microsoft.Data.SqlClient;

namespace Cadence.Storage.Sql;

/// <summary>
/// Keeps API tokens in one SQL table, resolved by digest on every authenticated request.
/// </summary>
/// <remarks>
/// Nothing is cached, not even behind a change token. Resolution is a single seek on a unique index,
/// which is cheap enough that a revoked token stops working everywhere on the next request rather
/// than at the end of somebody's poll interval.
/// </remarks>
public sealed class SqlApiTokenStore : IWritableApiTokenStore
{
    private readonly SqlDatabase _database;
    private readonly ISystemClock _clock;

    internal SqlApiTokenStore(SqlDatabase database, ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);

        _database = database;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ApiTokenPrincipal?> FindAsync(byte[] digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);

        var rows = await _database.QueryAsync(
            $"""
            SELECT Id, Name, Fingerprint, Scope
            FROM {_database.Table("CadenceApiToken")}
            WHERE Digest = @Digest
              AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > @Now);
            """,
            command =>
            {
                AddDigest(command, digest);
                SqlValues.AddInstant(command, "@Now", _clock.UtcNow);
            },
            reader => new ApiTokenPrincipal(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                (ApiTokenScope)reader.GetByte(3)),
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<ApiTokenInfo> CreateAsync(
        ApiTokenCreation creation,
        byte[] digest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(digest);

        var info = new ApiTokenInfo(
            Guid.NewGuid(),
            creation.Name,
            ApiTokenSecret.Fingerprint(digest),
            creation.Scope,
            _clock.UtcNow,
            creation.CreatedBySubject,
            creation.CreatedByName,
            creation.ExpiresAtUtc);

        await _database.ExecuteAsync(
            $"""
            INSERT INTO {_database.Table("CadenceApiToken")}
                (Id, Name, Digest, Fingerprint, Scope, CreatedAtUtc, CreatedBySubject,
                 CreatedByName, ExpiresAtUtc)
            VALUES
                (@Id, @Name, @Digest, @Fingerprint, @Scope, @CreatedAtUtc, @CreatedBySubject,
                 @CreatedByName, @ExpiresAtUtc);
            """,
            command =>
            {
                SqlValues.AddGuid(command, "@Id", info.Id);
                SqlValues.AddText(command, "@Name", info.Name, 200);
                AddDigest(command, digest);
                SqlValues.AddText(command, "@Fingerprint", info.Fingerprint, 8);
                SqlValues.AddEnum(command, "@Scope", info.Scope);
                SqlValues.AddInstant(command, "@CreatedAtUtc", info.CreatedAtUtc);
                SqlValues.AddText(command, "@CreatedBySubject", info.CreatedBySubject, 400);
                SqlValues.AddText(command, "@CreatedByName", info.CreatedByName, 256);
                SqlValues.AddInstant(command, "@ExpiresAtUtc", info.ExpiresAtUtc);
            },
            cancellationToken).ConfigureAwait(false);

        return info;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiTokenInfo>> ListAsync(CancellationToken cancellationToken)
    {
        return await _database.QueryAsync(
            $"""
            SELECT Id, Name, Fingerprint, Scope, CreatedAtUtc, CreatedBySubject, CreatedByName,
                   ExpiresAtUtc
            FROM {_database.Table("CadenceApiToken")}
            WHERE ExpiresAtUtc IS NULL OR ExpiresAtUtc > @Now
            ORDER BY CreatedAtUtc DESC, Id;
            """,
            command => SqlValues.AddInstant(command, "@Now", _clock.UtcNow),
            reader => new ApiTokenInfo(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                (ApiTokenScope)reader.GetByte(3),
                SqlValues.GetInstant(reader, 4),
                SqlValues.GetStringOrNull(reader, 5),
                SqlValues.GetStringOrNull(reader, 6),
                SqlValues.GetInstantOrNull(reader, 7)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var affected = await _database.ExecuteAsync(
            $"DELETE FROM {_database.Table("CadenceApiToken")} WHERE Id = @Id;",
            command => SqlValues.AddGuid(command, "@Id", id),
            cancellationToken).ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>Deletes expired tokens, for the janitor.</summary>
    /// <param name="now">Tokens whose expiry precedes this are eligible.</param>
    /// <param name="batchSize">How many to delete per operation.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many tokens were deleted.</returns>
    internal Task<int> PurgeExpiredAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
        => _database.ExecuteAsync(
            $"""
            DELETE TOP (@BatchSize)
            FROM {_database.Table("CadenceApiToken")}
            WHERE ExpiresAtUtc IS NOT NULL AND ExpiresAtUtc <= @Now;
            """,
            command =>
            {
                command.Parameters.AddWithValue("@BatchSize", batchSize);
                SqlValues.AddInstant(command, "@Now", now);
            },
            cancellationToken);

    private static void AddDigest(SqlCommand command, byte[] digest)
    {
        var parameter = command.Parameters.Add("@Digest", SqlDbType.Binary, 32);
        parameter.Value = digest;
    }
}
