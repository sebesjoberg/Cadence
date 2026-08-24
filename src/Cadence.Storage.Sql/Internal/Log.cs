using Microsoft.Extensions.Logging;

namespace Cadence.Storage.Sql.Internal;

/// <summary>
/// Every log message the SQL storage tier writes, as source-generated
/// <see cref="LoggerMessageAttribute"/> methods.
/// </summary>
/// <remarks>
/// Event ids start at 2000 so they never collide with the core scheduler's 1000-range, which matters
/// to anyone alerting on ids rather than on text.
/// </remarks>
internal static partial class Log
{
    // 2000-2099: schema.

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Cadence schema is up to date ({ScriptCount} script(s) already applied).")]
    public static partial void SchemaUpToDate(this ILogger logger, int scriptCount);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Applied {ScriptCount} Cadence schema script(s) to schema '{Schema}'.")]
    public static partial void SchemaMigrated(this ILogger logger, int scriptCount, string schema);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Applying Cadence schema script '{ScriptName}'.")]
    public static partial void SchemaApplyingScript(this ILogger logger, string scriptName);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Debug,
        Message = "Could not release the Cadence schema lock. It is session-scoped, so closing the " +
                  "connection releases it regardless.")]
    public static partial void SchemaLockReleaseFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Information,
        Message = "Automatic schema migration is off. Cadence assumes the scripts in scripts/sql " +
                  "have been applied to schema '{Schema}'.")]
    public static partial void SchemaMigrationSkipped(this ILogger logger, string schema);

    // 2100-2199: claiming.

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Debug,
        Message = "Claim for '{JobName}' at {Occurrence:O} was already held by this instance under " +
                  "the same run id, so an earlier attempt committed and its acknowledgement was " +
                  "lost. Treating the claim as won.")]
    public static partial void ClaimAlreadyOurs(
        this ILogger logger, string jobName, DateTimeOffset occurrence);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Transient failure claiming '{JobName}' at {Occurrence:O}; retrying (attempt " +
                  "{Attempt} of {MaxAttempts}).")]
    public static partial void ClaimRetrying(
        this ILogger logger,
        Exception exception,
        string jobName,
        DateTimeOffset occurrence,
        int attempt,
        int maxAttempts);

    // 2200-2299: instance registry.

    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Information,
        Message = "Registered instance '{InstanceId}' in the Cadence instance registry.")]
    public static partial void InstanceRegistered(this ILogger logger, string instanceId);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Warning,
        Message = "Heartbeat for instance '{InstanceId}' failed. If this continues past the " +
                  "heartbeat timeout, the janitor will treat this instance as gone and mark its " +
                  "unfinished runs as lost.")]
    public static partial void HeartbeatFailed(this ILogger logger, Exception exception, string instanceId);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Debug,
        Message = "Deregistered instance '{InstanceId}'.")]
    public static partial void InstanceDeregistered(this ILogger logger, string instanceId);

    // 2300-2399: janitor.

    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Information,
        Message = "Janitor pass: purged {PurgedByAge} run(s) by age, trimmed {TrimmedPerJob} " +
                  "run(s) over the per-job cap, reaped {Reaped} abandoned run(s), removed " +
                  "{DeadInstances} dead instance(s).")]
    public static partial void JanitorPass(
        this ILogger logger, int purgedByAge, int trimmedPerJob, int reaped, int deadInstances);

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Error,
        Message = "A janitor pass failed. History will keep growing until a pass succeeds, but " +
                  "scheduling is unaffected.")]
    public static partial void JanitorFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Warning,
        Message = "Marked {Count} run(s) as lost: their instance stopped heartbeating more than " +
                  "{Timeout} ago, so no outcome was ever recorded for them.")]
    public static partial void RunsReaped(this ILogger logger, int count, TimeSpan timeout);

    // 2400-2499: schedules and progress.

    [LoggerMessage(
        EventId = 2400,
        Level = LogLevel.Debug,
        Message = "Schedule version moved from {Previous} to {Current}; signalling a reload.")]
    public static partial void ScheduleVersionChanged(this ILogger logger, long previous, long current);

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Warning,
        Message = "Could not poll the Cadence schedule version. Instances keep running the " +
                  "schedules they already have until the poll succeeds.")]
    public static partial void SchedulePollFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Warning,
        Message = "Failed to flush {Count} buffered progress entr(ies). Progress is a diagnostic, " +
                  "so the run itself is unaffected.")]
    public static partial void ProgressFlushFailed(this ILogger logger, Exception exception, int count);

    [LoggerMessage(
        EventId = 2403,
        Level = LogLevel.Warning,
        Message = "Dropped {Count} buffered progress entr(ies): the buffer is full because writes " +
                  "are not keeping up with how fast jobs are reporting.")]
    public static partial void ProgressDropped(this ILogger logger, int count);
}
