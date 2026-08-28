using Cadence.Storage.Sql.Internal;

namespace Cadence.Storage.Sql;

/// <summary>Reads the heartbeat rows <see cref="SqlInstanceRegistry"/> writes.</summary>
internal sealed class SqlInstanceDirectory : IInstanceDirectory
{
    private readonly SqlDatabase _database;

    public SqlInstanceDirectory(SqlDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstanceInfo>> GetAllAsync(CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT InstanceId, MachineName, ProcessId, AssemblyVersion, StartedAtUtc, LastHeartbeatUtc
            FROM {_database.Table("CadenceInstance")}
            ORDER BY MachineName, ProcessId;
            """;

        return await _database.QueryAsync(
            sql,
            bind: null,
            reader => new InstanceInfo
            {
                InstanceId = reader.GetString(0),
                MachineName = reader.GetString(1),
                ProcessId = reader.GetInt32(2),
                AssemblyVersion = SqlValues.GetStringOrNull(reader, 3),
                StartedAtUtc = SqlValues.GetInstant(reader, 4),
                LastHeartbeatUtc = SqlValues.GetInstant(reader, 5),
            },
            cancellationToken).ConfigureAwait(false);
    }
}
