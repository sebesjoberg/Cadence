using System.Text.Json;
using Cadence.Storage.Redis.Internal;

namespace Cadence.Storage.Redis;

/// <summary>Reads the heartbeats <see cref="RedisInstanceRegistry"/> writes.</summary>
internal sealed class RedisInstanceDirectory : IInstanceDirectory
{
    private readonly RedisConnection _connection;

    public RedisInstanceDirectory(RedisConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstanceInfo>> GetAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var details = await database.HashGetAllAsync(keys.Instances).ConfigureAwait(false);
        var beats = await database.SortedSetRangeByScoreWithScoresAsync(keys.Heartbeats).ConfigureAwait(false);

        var heartbeatByInstance = beats.ToDictionary(
            entry => (string)entry.Element!, entry => entry.Score, StringComparer.Ordinal);

        var result = new List<InstanceInfo>(details.Length);

        foreach (var entry in details)
        {
            var instanceId = (string)entry.Name!;

            // A hash entry without a matching heartbeat is the narrow window register leaves
            // between its two writes; treat it the same as not registered yet rather than guess.
            if (!heartbeatByInstance.TryGetValue(instanceId, out var score))
            {
                continue;
            }

            if (JsonSerializer.Deserialize<StoredInstance>((string)entry.Value!) is not { } stored)
            {
                continue;
            }

            result.Add(new InstanceInfo
            {
                InstanceId = instanceId,
                MachineName = stored.MachineName ?? string.Empty,
                ProcessId = stored.ProcessId,
                AssemblyVersion = stored.AssemblyVersion,
                StartedAtUtc = RedisValues.FromTicks(stored.StartedAtUtc),
                LastHeartbeatUtc = RedisValues.FromTicks((long)score),
            });
        }

        return result;
    }

    /// <summary>The JSON shape <see cref="RedisInstanceRegistry"/> writes to the instances hash.</summary>
    private sealed class StoredInstance
    {
        public string? MachineName { get; init; }

        public int ProcessId { get; init; }

        public string? AssemblyVersion { get; init; }

        public long StartedAtUtc { get; init; }
    }
}
