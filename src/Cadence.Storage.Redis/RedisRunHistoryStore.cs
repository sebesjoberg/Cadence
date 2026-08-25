using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cadence.Storage.Redis;

/// <summary>
/// Run history in Redis: a hash per run, and sorted sets to find them by.
/// </summary>
/// <remarks>
/// <para>
/// Redis has no query planner, so the indexes here are the query plan. A run is written into up to
/// four sorted sets — all runs, its job's, its instance's, and its job's successes — each scored by
/// the run's start instant, because every question this store answers is "the newest N matching X".
/// </para>
/// <para>
/// A query picks the narrowest index its filters allow and evaluates the rest by reading the
/// candidate hashes. That is a deliberate limit: filtering by status alone still walks the global
/// index. Adding a per-status index would fix it and cost a write on every state change, and the
/// dashboard's queries are all scoped to a job, so the trade goes the other way.
/// </para>
/// </remarks>
public sealed class RedisRunHistoryStore : IRunHistoryStore, IAsyncDisposable
{
    /// <summary>How many index entries to read per round trip while filtering candidates.</summary>
    /// <remarks>
    /// Large enough that a query filtered down to a few matches does not make a round trip per
    /// candidate, small enough that a query with no matches at all does not pull an entire index
    /// into memory to discover that.
    /// </remarks>
    private const int ScanPage = 256;

    private readonly RedisConnection _connection;
    private readonly RedisLogAppender _logAppender;

    internal RedisRunHistoryStore(
        RedisConnection connection,
        RedisStorageOptions options,
        ILogger<RedisRunHistoryStore> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _logAppender = new RedisLogAppender(connection, options, logger);
    }

    /// <inheritdoc />
    public async Task<JobRun> StartAsync(JobRunStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var member = start.RunId.ToString("N");
        var startedAt = RedisValues.Ticks(start.StartedAt);

        // One script, not a read then a write: the run may already exist because the coordinator
        // wrote it when it claimed the occurrence, and an existence check would cost a round trip
        // on the hot path to learn something the write does not need to know.
        await database.ScriptEvaluateAsync(
            Scripts.Start,
            [
                keys.Run(start.RunId),
                keys.AllRuns,
                keys.JobRuns(start.JobName),
                keys.InstanceRuns(start.InstanceId),
                keys.RunningRuns,
                keys.JobNames,
            ],
            [
                member,
                start.JobName,
                start.ScheduledFor is { } scheduled
                    ? RedisValues.Argument(RedisValues.Ticks(scheduled))
                    : string.Empty,
                (int)start.Trigger,
                (int)RunStatus.Running,
                start.InstanceId,
                RedisValues.Argument(startedAt),
            ]).ConfigureAwait(false);

        return new JobRun
        {
            RunId = start.RunId,
            JobName = start.JobName,
            ScheduledFor = start.ScheduledFor,
            Trigger = start.Trigger,
            Status = RunStatus.Running,
            InstanceId = start.InstanceId,
            StartedAt = start.StartedAt,
        };
    }

    /// <inheritdoc />
    public async Task CompleteAsync(Guid runId, JobRunResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        await database.ScriptEvaluateAsync(
            Scripts.Complete,
            [keys.Run(runId), keys.RunningRuns],
            [
                runId.ToString("N"),
                (int)result.Status,
                RedisValues.Argument(RedisValues.Ticks(result.CompletedAt)),
                RedisValues.Argument((long)result.Duration.TotalMilliseconds),
                result.Error ?? string.Empty,
                keys.Parts.JobRuns,
                (int)RunStatus.Succeeded,
                keys.Parts.SuccessSuffix,
            ]).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AppendLogAsync(Guid runId, JobLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        _logAppender.Append(runId, entry);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobRun?> GetLastRunAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        return await NewestAsync(_connection.Keys.JobRuns(jobName), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobRun?> GetLastSuccessAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        return await NewestAsync(_connection.Keys.JobSuccesses(jobName), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRun>> QueryAsync(RunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit <= 0)
        {
            return [];
        }

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        // Narrowest index the filters allow. Job beats instance because a job's history is bounded
        // by retention while an instance's is bounded by nothing in particular.
        var index = query.JobName is { } job
            ? keys.JobRuns(job)
            : query.InstanceId is { } instance
                ? keys.InstanceRuns(instance)
                : keys.AllRuns;

        var min = query.From is { } from ? RedisValues.Ticks(from) : double.NegativeInfinity;
        var max = query.To is { } to ? RedisValues.Ticks(to) : double.PositiveInfinity;

        // From is inclusive and To is exclusive, which is the window every caller means by "that
        // day" and the only pair that tiles without overlapping.
        var exclude = query.To is null ? Exclude.None : Exclude.Stop;

        var wanted = query.Offset + query.Limit;
        var matches = new List<JobRun>(Math.Min(wanted, ScanPage));
        var skip = 0;

        while (matches.Count < wanted && !cancellationToken.IsCancellationRequested)
        {
            var page = await database.SortedSetRangeByScoreAsync(
                index, min, max, exclude, Order.Descending, skip, ScanPage).ConfigureAwait(false);

            if (page.Length == 0)
            {
                break;
            }

            skip += page.Length;

            foreach (var run in await LoadAsync(database, page, cancellationToken).ConfigureAwait(false))
            {
                if (Matches(run, query))
                {
                    matches.Add(run);
                }
            }

            if (page.Length < ScanPage)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (matches.Count <= query.Offset)
        {
            return [];
        }

        var window = matches
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToList();

        return await WithLogsAsync(database, window, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountConsecutiveFailuresAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        var index = keys.JobRuns(jobName);

        var failures = 0;
        var skip = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Newest first: a descending range starts at the highest score, so paging forward walks
            // backwards through history, which is the direction a failure streak counts in.
            var page = await database.SortedSetRangeByRankAsync(
                index, skip, skip + ScanPage - 1, Order.Descending).ConfigureAwait(false);

            if (page.Length == 0)
            {
                return failures;
            }

            skip += page.Length;

            foreach (var run in await LoadAsync(database, page, cancellationToken).ConfigureAwait(false))
            {
                switch (run.Status)
                {
                    case RunStatus.Succeeded:
                        return failures;

                    case RunStatus.Failed:
                    case RunStatus.TimedOut:
                    case RunStatus.Lost:
                        failures++;
                        break;

                    default:
                        // Running, Skipped and Aborted say nothing about whether the job works, so
                        // they neither extend the streak nor break it.
                        break;
                }
            }

            if (page.Length < ScanPage)
            {
                return failures;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return failures;
    }

    /// <inheritdoc />
    public async Task PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var maintenance = new RedisStorageMaintenance(_connection);

        await maintenance.PurgeRunsByAgeAsync(olderThan, int.MaxValue, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Waits until buffered progress entries have been written.</summary>
    /// <remarks>Exposed for tests; nothing in the scheduler needs it.</remarks>
    internal Task FlushProgressAsync() => _logAppender.FlushAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _logAppender.DisposeAsync().ConfigureAwait(false);

    private static bool Matches(JobRun run, RunQuery query)
    {
        if (query.JobName is { } job && !string.Equals(run.JobName, job, StringComparison.Ordinal))
        {
            return false;
        }

        if (query.InstanceId is { } instance &&
            !string.Equals(run.InstanceId, instance, StringComparison.Ordinal))
        {
            return false;
        }

        return query.Statuses is not { Count: > 0 } statuses || statuses.Contains(run.Status);
    }

    private static Guid ParseMember(RedisValue member) => Guid.ParseExact((string)member!, "N");

    private async Task<JobRun?> NewestAsync(RedisKey index, CancellationToken cancellationToken)
    {
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        // Rank 0 of a descending range is the highest score, which is the newest. Rank -1 would be
        // the far end of that range — the oldest run this job ever had.
        var newest = await database
            .SortedSetRangeByRankAsync(index, 0, 0, Order.Descending)
            .ConfigureAwait(false);

        if (newest.Length == 0)
        {
            return null;
        }

        var runs = await LoadAsync(database, newest, cancellationToken).ConfigureAwait(false);

        if (runs.Count == 0)
        {
            return null;
        }

        var withLogs = await WithLogsAsync(database, runs, cancellationToken).ConfigureAwait(false);
        return withLogs[0];
    }

    private async Task<List<JobRun>> LoadAsync(
        IDatabase database,
        RedisValue[] members,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var batch = database.CreateBatch();
        var pending = new List<(Guid RunId, Task<HashEntry[]> Hash)>(members.Length);

        foreach (var member in members)
        {
            var runId = ParseMember(member);
            pending.Add((runId, batch.HashGetAllAsync(keys.Run(runId))));
        }

        batch.Execute();

        var runs = new List<JobRun>(pending.Count);

        foreach (var (runId, hash) in pending)
        {
            // A missing hash is an index entry whose run has been deleted. Skipped rather than
            // failed: the janitor removes both, and a query stumbling into the gap between them
            // should return the runs that do exist.
            if (RedisValues.ToRun(runId, await hash.ConfigureAwait(false)) is { } run)
            {
                runs.Add(run);
            }
        }

        return runs;
    }

    private async Task<IReadOnlyList<JobRun>> WithLogsAsync(
        IDatabase database,
        List<JobRun> runs,
        CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            return runs;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var batch = database.CreateBatch();

        var pending = runs
            .Select(run => batch.ListRangeAsync(keys.RunLog(run.RunId)))
            .ToList();

        batch.Execute();

        var result = new List<JobRun>(runs.Count);

        for (var i = 0; i < runs.Count; i++)
        {
            var entries = await pending[i].ConfigureAwait(false);

            var log = entries
                .Select(entry => RedisValues.DeserialiseLogEntry((string)entry!))
                .OfType<JobLogEntry>()
                .ToList();

            result.Add(log.Count == 0 ? runs[i] : runs[i] with { Log = log });
        }

        return result;
    }
}
