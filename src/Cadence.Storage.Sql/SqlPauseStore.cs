using Cadence.Storage.Sql.Internal;

namespace Cadence.Storage.Sql;

/// <summary>
/// Keeps the pause switches in one SQL row, and bumps the schedule version with every write so
/// instances notice on the change detection they already run.
/// </summary>
public sealed class SqlPauseStore : IPauseStore
{
    private readonly SqlDatabase _database;
    private readonly ISystemClock _clock;

    internal SqlPauseStore(SqlDatabase database, ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);

        _database = database;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<PauseState> GetAsync(CancellationToken cancellationToken)
    {
        var rows = await _database.QueryAsync(
            $"SELECT Scope, Reason, SetBy, SetAtUtc FROM {_database.Table("CadencePause")} WHERE Id = 1;",
            bind: null,
            reader => new PauseState
            {
                Scope = (PauseScope)reader.GetByte(0),
                Reason = SqlValues.GetStringOrNull(reader, 1),
                SetBy = SqlValues.GetStringOrNull(reader, 2),
                SetAtUtc = SqlValues.GetInstantOrNull(reader, 3),
            },
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? PauseState.None : rows[0];
    }

    /// <inheritdoc />
    public async Task<PauseState> SetAsync(
        PauseScope scope,
        string? reason,
        string? setBy,
        CancellationToken cancellationToken)
    {
        var setAt = _clock.UtcNow;

        // MERGE-free upsert: the row is seeded by the schema script, but a database restored from
        // before revision 002 -- or a hand-applied script -- may not have it.
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            UPDATE {_database.Table("CadencePause")}
            SET Scope = @Scope, Reason = @Reason, SetBy = @SetBy, SetAtUtc = @SetAtUtc
            WHERE Id = 1;

            IF @@ROWCOUNT = 0
                INSERT INTO {_database.Table("CadencePause")} (Id, Scope, Reason, SetBy, SetAtUtc)
                VALUES (1, @Scope, @Reason, @SetBy, @SetAtUtc);

            UPDATE {_database.Table("CadenceScheduleVersion")} SET Version = Version + 1 WHERE Id = 1;

            COMMIT TRANSACTION;
            """;

        await _database.ExecuteAsync(
            sql,
            command =>
            {
                SqlValues.AddEnum(command, "@Scope", scope);
                SqlValues.AddText(command, "@Reason", reason, 500);
                SqlValues.AddText(command, "@SetBy", setBy, 200);
                SqlValues.AddInstant(command, "@SetAtUtc", setAt);
            },
            cancellationToken).ConfigureAwait(false);

        return new PauseState { Scope = scope, Reason = reason, SetBy = setBy, SetAtUtc = setAt };
    }
}
