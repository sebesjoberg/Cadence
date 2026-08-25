using Microsoft.Extensions.Logging;

namespace Cadence.Diagnostics;

/// <summary>
/// Every log message Cadence writes, as source-generated <see cref="LoggerMessageAttribute"/>
/// methods.
/// </summary>
/// <remarks>
/// The tick loop runs every second and logs per lost claim, so the level check has to happen before
/// the arguments are marshalled. Keeping the messages in one place also keeps their wording and
/// their event ids stable, which matters to anyone alerting on them.
/// </remarks>
internal static partial class Log
{
    // 1000-1099: startup and validation.

    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "{Warning}")]
    public static partial void RegistrationWarning(this ILogger logger, string warning);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "All {JobCount} registered job(s) resolved from the container.")]
    public static partial void AllJobsResolved(this ILogger logger, int jobCount);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "{FailureCount} job(s) could not be resolved and will not be scheduled:{Detail}")]
    public static partial void JobsDisabledByValidation(this ILogger logger, int failureCount, string detail);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "{FailureCount} job(s) could not be resolved. They remain scheduled and every run " +
                  "will fail:{Detail}")]
    public static partial void JobsUnresolvable(this ILogger logger, int failureCount, string detail);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Cadence started on instance {InstanceId} with {JobCount} job(s), ticking every " +
                  "{TickInterval}.")]
    public static partial void SchedulerStarted(
        this ILogger logger, string instanceId, int jobCount, TimeSpan tickInterval);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "Cadence stopped on instance {InstanceId}.")]
    public static partial void SchedulerStopped(this ILogger logger, string instanceId);

    // 1100-1199: configuration.

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Schedule configuration changed; reloading.")]
    public static partial void ConfigurationChanged(this ILogger logger);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Error,
        Message = "Could not reload schedules. Continuing with the {LoadedCount} already loaded.")]
    public static partial void ScheduleReloadFailed(this ILogger logger, Exception exception, int loadedCount);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Error,
        Message = "'{JobName}' will not be scheduled: {Problem}")]
    public static partial void ScheduleProblem(this ILogger logger, string jobName, string problem);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Could not read the last run of '{JobName}'. Catch-up will start from now.")]
    public static partial void LastRunReadFailed(this ILogger logger, Exception exception, string jobName);

    // 1200-1299: the tick loop.

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Error,
        Message = "A Cadence tick failed. Scheduling continues.")]
    public static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "'{JobName}' is more than {Limit} occurrences behind. The backlog has been abandoned " +
                  "and scheduling resumes from now.")]
    public static partial void BacklogAbandoned(this ILogger logger, string jobName, int limit);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Warning,
        Message = "Catch-up for '{JobName}' was truncated by MaxCatchUp ({Cap}); {DroppedCount} " +
                  "occurrence(s) were dropped.")]
    public static partial void CatchUpTruncated(this ILogger logger, string jobName, int cap, int droppedCount);

    [LoggerMessage(
        EventId = 1203,
        Level = LogLevel.Information,
        Message = "{DroppedCount} missed occurrence(s) of '{JobName}' were skipped under the {Policy} policy.")]
    public static partial void MissedOccurrencesSkipped(
        this ILogger logger, int droppedCount, string jobName, MissedRunPolicy policy);

    [LoggerMessage(
        EventId = 1204,
        Level = LogLevel.Trace,
        Message = "Another instance claimed '{JobName}' at {Occurrence:O}.")]
    public static partial void ClaimLost(this ILogger logger, string jobName, DateTimeOffset occurrence);

    // 1300-1399: run execution.

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Error,
        Message = "Run {RunId} of '{JobName}' failed.")]
    public static partial void RunFailed(this ILogger logger, Exception exception, Guid runId, string jobName);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Warning,
        Message = "Run {RunId} of '{JobName}' exceeded its maximum duration of {MaxDuration} and was " +
                  "cancelled.")]
    public static partial void RunTimedOut(
        this ILogger logger, Guid runId, string jobName, TimeSpan? maxDuration);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Information,
        Message = "Run {RunId} of '{JobName}' was aborted by host shutdown.")]
    public static partial void RunAborted(this ILogger logger, Guid runId, string jobName);

    [LoggerMessage(
        EventId = 1303,
        Level = LogLevel.Information,
        Message = "Skipped an occurrence of '{JobName}': {Reason}")]
    public static partial void OccurrenceSkipped(this ILogger logger, string jobName, string reason);

    [LoggerMessage(
        EventId = 1304,
        Level = LogLevel.Warning,
        Message = "Could not record a skipped occurrence for '{JobName}'. Reason was: {Reason}")]
    public static partial void SkippedRecordFailed(
        this ILogger logger, Exception exception, string jobName, string reason);

    [LoggerMessage(
        EventId = 1305,
        Level = LogLevel.Error,
        Message = "Could not record the outcome of run {RunId}. It will be reaped as lost.")]
    public static partial void RunCompletionWriteFailed(this ILogger logger, Exception exception, Guid runId);

    [LoggerMessage(
        EventId = 1306,
        Level = LogLevel.Warning,
        Message = "Could not record progress for run {RunId}. The run itself is unaffected.")]
    public static partial void ProgressWriteFailed(this ILogger logger, Exception exception, Guid runId);

    /// <summary>
    /// Progress a job reported. Written through <see cref="ILogger"/> so it reaches whatever the
    /// host has configured, OTLP included; the enclosing scope supplies the run correlation.
    /// </summary>
    [LoggerMessage(EventId = 1307, Level = LogLevel.Information, Message = "{Progress}")]
    public static partial void JobProgress(this ILogger logger, string progress);

    // 1400-1499: shutdown.

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Information,
        Message = "Waiting up to {Timeout} for {RunCount} in-flight run(s) to finish.")]
    public static partial void DrainWaiting(this ILogger logger, TimeSpan timeout, int runCount);

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Warning,
        Message = "Run {RunId} of '{JobName}' did not stop within the drain timeout and has been recorded " +
                  "as aborted. The job is not observing its cancellation token.")]
    public static partial void StragglerAborted(this ILogger logger, Guid runId, string jobName);

    // 1500-1599: the janitor. Core rather than a storage package because the policy is one
    // implementation shared by every persistent tier; only the operations differ.

    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Information,
        Message = "Janitor pass: purged {PurgedByAge} run(s) by age, trimmed {TrimmedPerJob} " +
                  "run(s) over the per-job cap, reaped {Reaped} abandoned run(s), removed " +
                  "{DeadInstances} dead instance(s).")]
    public static partial void JanitorPass(
        this ILogger logger, int purgedByAge, int trimmedPerJob, int reaped, int deadInstances);

    [LoggerMessage(
        EventId = 1501,
        Level = LogLevel.Error,
        Message = "A janitor pass failed. History will keep growing until a pass succeeds, but " +
                  "scheduling is unaffected.")]
    public static partial void JanitorFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1502,
        Level = LogLevel.Warning,
        Message = "Marked {Count} run(s) as lost: their instance stopped heartbeating more than " +
                  "{Timeout} ago, so no outcome was ever recorded for them.")]
    public static partial void RunsReaped(this ILogger logger, int count, TimeSpan timeout);
}
