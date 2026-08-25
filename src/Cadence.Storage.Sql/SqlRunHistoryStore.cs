using System.Globalization;
using System.Text;
using System.Text.Json;
using Cadence.Storage.Sql.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Cadence.Storage.Sql;

/// <summary>
/// Records runs in SQL Server, so history outlives the process and every instance sees the same
/// picture.
/// </summary>
/// <remarks>
/// For a scheduled occurrence the row already exists: <see cref="SqlOccurrenceCoordinator"/> created
/// it as the claim. So <see cref="StartAsync"/> updates rather than inserts, and falls back to an
/// insert only when there is nothing to update — which covers a run that was never claimed, either
/// because it was triggered out of band or because this store is paired with a coordinator that does
/// not write rows.
/// </remarks>
public sealed class SqlRunHistoryStore : IRunHistoryStore, IAsyncDisposable
{
    private readonly SqlDatabase _database;
    private readonly BatchingLogAppender _logAppender;

    internal SqlRunHistoryStore(
        SqlDatabase database,
        SqlStorageOptions options,
        ILogger<SqlRunHistoryStore> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _logAppender = new BatchingLogAppender(database, options, logger);
    }

    /// <inheritdoc />
    public async Task<JobRun> StartAsync(JobRunStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);

        var table = _database.Table("CadenceJobRun");

        // One round trip, not a read followed by a write: the claim's row is updated if it is there,
        // and inserted if it is not. A separate existence check would race with nothing useful and
        // cost a round trip on the hot path.
        var sql = $"""
            UPDATE {table}
            SET [Trigger]    = @Trigger,
                Status       = @Status,
                InstanceId   = @InstanceId,
                StartedAtUtc = @StartedAtUtc
            WHERE RunId = @RunId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO {table}
                    (RunId, JobName, ScheduledForUtc, [Trigger], Status, InstanceId, StartedAtUtc)
                VALUES
                    (@RunId, @JobName, @ScheduledForUtc, @Trigger, @Status, @InstanceId, @StartedAtUtc);
            END
            """;

        await _database.ExecuteAsync(
            sql,
            command =>
            {
                SqlValues.AddGuid(command, "@RunId", start.RunId);
                SqlValues.AddText(command, "@JobName", start.JobName, 200);
                SqlValues.AddInstant(command, "@ScheduledForUtc", start.ScheduledFor);
                SqlValues.AddEnum(command, "@Trigger", start.Trigger);
                SqlValues.AddEnum(command, "@Status", RunStatus.Running);
                SqlValues.AddText(command, "@InstanceId", start.InstanceId, 200);
                SqlValues.AddInstant(command, "@StartedAtUtc", start.StartedAt);
            },
            cancellationToken).ConfigureAwait(false);

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

        // No row is not an error: the janitor may already have purged or reaped it. The point of this
        // write is that nothing is left claiming to be running, and a row that is gone satisfies that.
        var sql = $"""
            UPDATE {_database.Table("CadenceJobRun")}
            SET Status         = @Status,
                CompletedAtUtc = @CompletedAtUtc,
                DurationMs     = @DurationMs,
                Error          = @Error
            WHERE RunId = @RunId;
            """;

        await _database.ExecuteAsync(
            sql,
            command =>
            {
                SqlValues.AddGuid(command, "@RunId", runId);
                SqlValues.AddEnum(command, "@Status", result.Status);
                SqlValues.AddInstant(command, "@CompletedAtUtc", result.CompletedAt);
                SqlValues.AddDuration(command, "@DurationMs", result.Duration);
                SqlValues.AddText(command, "@Error", result.Error, -1);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Buffered and flushed in batches. The entry is queued here and written shortly afterwards, so
    /// this returns before the row exists — which is what keeps a chatty job from turning into a
    /// round trip per <c>Report</c> call.
    /// </remarks>
    public Task AppendLogAsync(Guid runId, JobLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logAppender.Enqueue(runId, entry);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes any buffered progress entries immediately.
    /// </summary>
    /// <remarks>
    /// Exists for tests and for callers that need to read back what they just reported. Normal
    /// operation does not need it: the appender flushes on its own interval.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the wait, not the write.</param>
    public Task FlushProgressAsync(CancellationToken cancellationToken)
        => _logAppender.FlushNowAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<JobRun?> GetLastRunAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        var runs = await _database.QueryAsync(
            $"""
            SELECT TOP (1) {RunColumns}
            FROM {_database.Table("CadenceJobRun")}
            WHERE JobName = @JobName
            ORDER BY StartedAtUtc DESC, Seq DESC;
            """,
            command => SqlValues.AddText(command, "@JobName", jobName, 200),
            ReadRun,
            cancellationToken).ConfigureAwait(false);

        return runs.Count == 0 ? null : await WithLogAsync(runs[0], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobRun?> GetLastSuccessAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        var runs = await _database.QueryAsync(
            $"""
            SELECT TOP (1) {RunColumns}
            FROM {_database.Table("CadenceJobRun")}
            WHERE JobName = @JobName AND Status = @Succeeded
            ORDER BY StartedAtUtc DESC, Seq DESC;
            """,
            command =>
            {
                SqlValues.AddText(command, "@JobName", jobName, 200);
                SqlValues.AddEnum(command, "@Succeeded", RunStatus.Succeeded);
            },
            ReadRun,
            cancellationToken).ConfigureAwait(false);

        return runs.Count == 0 ? null : await WithLogAsync(runs[0], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRun>> QueryAsync(RunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Max(0, query.Limit);

        if (limit == 0)
        {
            return [];
        }

        var where = new StringBuilder("WHERE 1 = 1");
        var statuses = query.Statuses is { Count: > 0 } ? query.Statuses.ToList() : null;

        if (query.JobName is not null)
        {
            where.Append(" AND JobName = @JobName");
        }

        if (query.From is not null)
        {
            where.Append(" AND StartedAtUtc >= @From");
        }

        if (query.To is not null)
        {
            where.Append(" AND StartedAtUtc < @To");
        }

        if (query.InstanceId is not null)
        {
            where.Append(" AND InstanceId = @InstanceId");
        }

        if (statuses is not null)
        {
            where.Append(" AND Status IN (");

            for (var i = 0; i < statuses.Count; i++)
            {
                where.Append(i > 0 ? ", @s" : "@s").Append(i.ToString(CultureInfo.InvariantCulture));
            }

            where.Append(')');
        }

        // OFFSET/FETCH rather than a window function: paging here is the dashboard's "next page",
        // never a deep scan, and this form uses IX_CadenceJobRun_Job_Started directly.
        var sql = $"""
            SELECT {RunColumns}
            FROM {_database.Table("CadenceJobRun")}
            {where}
            ORDER BY StartedAtUtc DESC, Seq DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """;

        var runs = await _database.QueryAsync(
            sql,
            command =>
            {
                if (query.JobName is not null)
                {
                    SqlValues.AddText(command, "@JobName", query.JobName, 200);
                }

                if (query.From is { } from)
                {
                    SqlValues.AddInstant(command, "@From", from);
                }

                if (query.To is { } to)
                {
                    SqlValues.AddInstant(command, "@To", to);
                }

                if (query.InstanceId is not null)
                {
                    SqlValues.AddText(command, "@InstanceId", query.InstanceId, 200);
                }

                if (statuses is not null)
                {
                    for (var i = 0; i < statuses.Count; i++)
                    {
                        SqlValues.AddEnum(command, $"@s{i}", statuses[i]);
                    }
                }

                command.Parameters.AddWithValue("@Offset", Math.Max(0, query.Offset));
                command.Parameters.AddWithValue("@Limit", limit);
            },
            ReadRun,
            cancellationToken).ConfigureAwait(false);

        return await WithLogsAsync(runs, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountConsecutiveFailuresAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        /*
            The semantics have to match the in-memory tier exactly, because an alert threshold that
            changes meaning when someone adds a connection string is worse than no threshold at all.

            Failed, TimedOut and Lost extend the streak. Succeeded ends it. Running, Skipped and
            Aborted are neutral -- none of them says anything about whether the job's own logic is
            broken -- so they are filtered out before the streak is counted rather than treated as a
            break in it.
        */
        var sql = $"""
            WITH Decisive AS
            (
                SELECT Status,
                       ROW_NUMBER() OVER (ORDER BY StartedAtUtc DESC, Seq DESC) AS Position
                FROM {_database.Table("CadenceJobRun")}
                WHERE JobName = @JobName
                  AND Status IN (@Failed, @TimedOut, @Lost, @Succeeded)
            )
            SELECT COUNT(*)
            FROM Decisive
            WHERE Position < COALESCE(
                (SELECT MIN(Position) FROM Decisive WHERE Status = @Succeeded),
                2147483647);
            """;

        return await _database.ScalarAsync<int>(
            sql,
            command =>
            {
                SqlValues.AddText(command, "@JobName", jobName, 200);
                SqlValues.AddEnum(command, "@Failed", RunStatus.Failed);
                SqlValues.AddEnum(command, "@TimedOut", RunStatus.TimedOut);
                SqlValues.AddEnum(command, "@Lost", RunStatus.Lost);
                SqlValues.AddEnum(command, "@Succeeded", RunStatus.Succeeded);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A run still marked <see cref="RunStatus.Running"/> is left alone however old it is. Deleting
    /// it would hide an abandoned run instead of surfacing it; that case belongs to the janitor,
    /// which marks it <see cref="RunStatus.Lost"/> once its instance has stopped heartbeating.
    /// </remarks>
    public async Task PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await PurgeByAgeAsync(olderThan, int.MaxValue, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes finished runs started before a cut-off, in batches.</summary>
    /// <param name="olderThan">Runs started before this instant are eligible.</param>
    /// <param name="batchSize">Rows per statement, to keep locks from escalating.</param>
    /// <param name="cancellationToken">Cancels the purge.</param>
    /// <returns>How many rows were deleted.</returns>
    internal async Task<int> PurgeByAgeAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE TOP (@BatchSize)
            FROM {_database.Table("CadenceJobRun")}
            WHERE StartedAtUtc < @OlderThan AND Status <> @Running;
            """;

        return await DeleteInBatchesAsync(
            sql,
            command =>
            {
                SqlValues.AddInstant(command, "@OlderThan", olderThan);
                SqlValues.AddEnum(command, "@Running", RunStatus.Running);
            },
            batchSize,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Trims each job's history to its most recent runs, in batches.</summary>
    /// <param name="maxRunsPerJob">How many runs to keep per job.</param>
    /// <param name="batchSize">Rows per statement.</param>
    /// <param name="cancellationToken">Cancels the trim.</param>
    /// <returns>How many rows were deleted.</returns>
    internal async Task<int> TrimPerJobAsync(
        int maxRunsPerJob,
        int batchSize,
        CancellationToken cancellationToken)
    {
        // Running rows are excluded from the ranking as well as from the delete: a job at its cap
        // whose current run is still going should not have that run counted towards the cap and then
        // be spared only by the WHERE clause.
        var sql = $"""
            WITH Ranked AS
            (
                SELECT Seq,
                       ROW_NUMBER() OVER (PARTITION BY JobName ORDER BY StartedAtUtc DESC, Seq DESC) AS Position
                FROM {_database.Table("CadenceJobRun")}
                WHERE Status <> @Running
            )
            DELETE FROM {_database.Table("CadenceJobRun")}
            WHERE Seq IN (SELECT TOP (@BatchSize) Seq FROM Ranked WHERE Position > @MaxRunsPerJob);
            """;

        return await DeleteInBatchesAsync(
            sql,
            command =>
            {
                SqlValues.AddEnum(command, "@Running", RunStatus.Running);
                command.Parameters.AddWithValue("@MaxRunsPerJob", maxRunsPerJob);
            },
            batchSize,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks runs abandoned by instances that stopped heartbeating as <see cref="RunStatus.Lost"/>.
    /// </summary>
    /// <param name="deadline">An instance whose last heartbeat predates this is treated as gone.</param>
    /// <param name="now">The completion instant to stamp on the reaped rows.</param>
    /// <param name="batchSize">Rows per statement.</param>
    /// <param name="cancellationToken">Cancels the reap.</param>
    /// <returns>How many rows were marked.</returns>
    internal async Task<int> ReapAbandonedAsync(
        DateTimeOffset deadline,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        /*
            Lost, not Aborted. Aborted means a shutdown cancelled the run and something recorded that;
            Lost means nobody recorded anything at all, which is a different fact and a different
            conversation with whoever operates the host.

            A Running row whose instance is not in the registry at all is also reaped: a graceful stop
            deletes the row, so a run left Running by an instance that has since deregistered was
            never completed either.
        */
        var sql = $"""
            UPDATE TOP (@BatchSize) run
            SET run.Status         = @Lost,
                run.CompletedAtUtc = @Now,
                run.DurationMs     = DATEDIFF_BIG(MILLISECOND, run.StartedAtUtc, @Now)
            FROM {_database.Table("CadenceJobRun")} AS run
            LEFT JOIN {_database.Table("CadenceInstance")} AS instance
                ON instance.InstanceId = run.InstanceId
            WHERE run.Status = @Running
              AND (instance.InstanceId IS NULL OR instance.LastHeartbeatUtc < @Deadline);
            """;

        var total = 0;

        while (true)
        {
            var affected = await _database.ExecuteAsync(
                sql,
                command =>
                {
                    command.Parameters.AddWithValue("@BatchSize", batchSize);
                    SqlValues.AddEnum(command, "@Lost", RunStatus.Lost);
                    SqlValues.AddEnum(command, "@Running", RunStatus.Running);
                    SqlValues.AddInstant(command, "@Now", now);
                    SqlValues.AddInstant(command, "@Deadline", deadline);
                },
                cancellationToken).ConfigureAwait(false);

            total += affected;

            if (affected < batchSize)
            {
                return total;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _logAppender.DisposeAsync().ConfigureAwait(false);

    private async Task<int> DeleteInBatchesAsync(
        string sql,
        Action<SqlCommand> bind,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var effective = batchSize == int.MaxValue ? 5000 : batchSize;

        while (true)
        {
            var affected = await _database.ExecuteAsync(
                sql,
                command =>
                {
                    command.Parameters.AddWithValue("@BatchSize", effective);
                    bind(command);
                },
                cancellationToken).ConfigureAwait(false);

            total += affected;

            if (affected < effective)
            {
                return total;
            }
        }
    }

    private const string RunColumns = """
        RunId, JobName, ScheduledForUtc, [Trigger], Status, InstanceId,
        StartedAtUtc, CompletedAtUtc, DurationMs, Error
        """;

    private static JobRun ReadRun(SqlDataReader reader) => new()
    {
        RunId = reader.GetGuid(0),
        JobName = reader.GetString(1),
        ScheduledFor = SqlValues.GetInstantOrNull(reader, 2),
        Trigger = (TriggerKind)reader.GetByte(3),
        Status = (RunStatus)reader.GetByte(4),
        InstanceId = reader.GetString(5),
        StartedAt = SqlValues.GetInstant(reader, 6),
        CompletedAt = SqlValues.GetInstantOrNull(reader, 7),
        Duration = SqlValues.GetDurationOrNull(reader, 8),
        Error = SqlValues.GetStringOrNull(reader, 9),
    };

    private async Task<JobRun> WithLogAsync(JobRun run, CancellationToken cancellationToken)
        => (await WithLogsAsync([run], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Attaches progress entries to runs, in one query rather than one per run.</summary>
    private async Task<IReadOnlyList<JobRun>> WithLogsAsync(
        List<JobRun> runs,
        CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            return runs;
        }

        var ids = new StringBuilder();

        for (var i = 0; i < runs.Count; i++)
        {
            ids.Append(i > 0 ? ", @l" : "@l").Append(i.ToString(CultureInfo.InvariantCulture));
        }

        var entries = await _database.QueryAsync(
            $"""
            SELECT RunId, TimestampUtc, Message, DataJson
            FROM {_database.Table("CadenceJobRunLog")}
            WHERE RunId IN ({ids})
            ORDER BY RunId, TimestampUtc, Seq;
            """,
            command =>
            {
                for (var i = 0; i < runs.Count; i++)
                {
                    SqlValues.AddGuid(command, $"@l{i}", runs[i].RunId);
                }
            },
            reader => (
                RunId: reader.GetGuid(0),
                Entry: new JobLogEntry
                {
                    Timestamp = SqlValues.GetInstant(reader, 1),
                    Message = reader.GetString(2),
                    Data = Deserialise(SqlValues.GetStringOrNull(reader, 3)),
                }),
            cancellationToken).ConfigureAwait(false);

        if (entries.Count == 0)
        {
            return runs;
        }

        var byRun = entries
            .GroupBy(e => e.RunId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<JobLogEntry>)g.Select(e => e.Entry).ToList());

        return [.. runs.Select(run => byRun.TryGetValue(run.RunId, out var log) ? run with { Log = log } : run)];
    }

    private static Dictionary<string, object?>? Deserialise(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch (JsonException)
        {
            // Progress data is opaque by design, so a row that cannot be read back is a curiosity,
            // not a reason to fail the query that happened to include it.
            return null;
        }
    }
}
