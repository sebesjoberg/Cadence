using Cadence.Storage.Redis.Internal;
using StackExchange.Redis;

namespace Cadence.Storage.Redis;

/// <summary>
/// The Redis tier's half of the janitor.
/// </summary>
/// <remarks>
/// Every pass here loops over scripts with a scan offset rather than a plain "repeat until a short
/// batch". Redis has no <c>DELETE TOP (n) WHERE</c>: the scripts read a window of an index and
/// decide per entry, so a window full of records that must be kept — live runs, runs still in
/// flight — would otherwise be rescanned forever. The scripts return what they looked at as well as
/// what they changed, and the offset advances by the former.
/// </remarks>
public sealed class RedisStorageMaintenance : IStorageMaintenance
{
    private readonly RedisConnection _connection;

    internal RedisStorageMaintenance(RedisConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <inheritdoc />
    public async Task<int> ReapAbandonedRunsAsync(
        DateTimeOffset heartbeatDeadline,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var reaped = 0;
        var offset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await database.ScriptEvaluateAsync(
                Scripts.Reap,
                [keys.RunningRuns, keys.Heartbeats],
                [
                    RedisValues.Argument(RedisValues.Ticks(heartbeatDeadline)),
                    RedisValues.Argument(RedisValues.Ticks(now)),
                    batchSize,
                    (int)RunStatus.Lost,
                    keys.Parts.Run,
                    offset,
                ]).ConfigureAwait(false);

            var (changed, scanned) = Pair(result);
            reaped += changed;

            // Reaped runs leave the index, so only the ones left behind shift the offset.
            offset += scanned - changed;

            if (scanned < batchSize)
            {
                break;
            }
        }

        return reaped;
    }

    /// <inheritdoc />
    public async Task<int> PurgeRunsByAgeAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        // int.MaxValue arrives from IRunHistoryStore.PurgeAsync, which means "all of it". Redis
        // takes the limit as a Lua number and would happily try to materialise that many entries in
        // one script, so it is clamped to something a single script can hold.
        var window = Math.Min(batchSize, 10_000);

        var deleted = 0;
        var offset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await database.ScriptEvaluateAsync(
                Scripts.PurgeByAge,
                [keys.AllRuns],
                [
                    RedisValues.Argument(RedisValues.Ticks(olderThan)),
                    window,
                    keys.Parts.Run,
                    (int)RunStatus.Running,
                    offset,
                    keys.Parts.JobRuns,
                    keys.Parts.SuccessSuffix,
                    keys.Parts.Occurrence,
                    keys.Parts.InstanceRuns,
                    keys.Parts.LogSuffix,
                ]).ConfigureAwait(false);

            var (changed, scanned) = Pair(result);
            deleted += changed;
            offset += scanned - changed;

            if (scanned < window)
            {
                break;
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> TrimRunsPerJobAsync(
        int maxRunsPerJob,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        // Per job rather than one sweeping script: the cap is per job, and a script that walked
        // every job's index would hold the server for as long as the whole history takes to read.
        var jobs = await database.SetMembersAsync(keys.JobNames).ConfigureAwait(false);

        var trimmed = 0;

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var jobName = (string)job!;

            var result = await database.ScriptEvaluateAsync(
                Scripts.TrimJob,
                [keys.JobRuns(jobName), keys.AllRuns],
                [
                    maxRunsPerJob,
                    batchSize,
                    keys.Parts.Run,
                    (int)RunStatus.Running,
                    jobName,
                    keys.Parts.JobRuns,
                    keys.Parts.SuccessSuffix,
                    keys.Parts.Occurrence,
                    keys.Parts.InstanceRuns,
                    keys.Parts.LogSuffix,
                ]).ConfigureAwait(false);

            trimmed += (int)(long)result;
        }

        return trimmed;
    }

    /// <inheritdoc />
    public async Task<int> PurgeDeadInstancesAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var result = await database.ScriptEvaluateAsync(
            Scripts.PurgeInstances,
            [keys.Heartbeats, keys.Instances],
            [RedisValues.Argument(RedisValues.Ticks(olderThan)), batchSize]).ConfigureAwait(false);

        return (int)(long)result;
    }

    private static (int Changed, int Scanned) Pair(RedisResult result)
    {
        var values = (RedisResult[])result!;

        return ((int)(long)values[0], (int)(long)values[1]);
    }
}
