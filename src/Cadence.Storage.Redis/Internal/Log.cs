using Microsoft.Extensions.Logging;

namespace Cadence.Storage.Redis.Internal;

/// <summary>
/// The Redis tier's log messages, as source-generated
/// <see cref="LoggerMessageAttribute"/> methods.
/// </summary>
/// <remarks>
/// Event ids are in the 3000 range: 1000 is Core, 2000 is the SQL tier. Keeping the ranges apart
/// matters to anyone alerting on an id rather than on a message.
/// </remarks>
internal static partial class Log
{
    // 3000-3099: instance registry.

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Registered instance '{InstanceId}', heartbeating every {Interval}.")]
    public static partial void InstanceRegistered(
        this ILogger logger, string instanceId, TimeSpan interval);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "A heartbeat for instance '{InstanceId}' failed. If this continues past the " +
                  "heartbeat timeout, the janitor will treat this instance as gone and mark its " +
                  "in-flight runs as lost.")]
    public static partial void HeartbeatFailed(
        this ILogger logger, Exception exception, string instanceId);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Debug,
        Message = "Deregistered instance '{InstanceId}'.")]
    public static partial void InstanceDeregistered(this ILogger logger, string instanceId);

    // 3100-3199: schedules.

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Debug,
        Message = "Schedule version moved from {Previous} to {Current}; signalling a reload.")]
    public static partial void ScheduleVersionMoved(this ILogger logger, long previous, long current);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "Could not read the schedule version. Schedules will still be re-read on the " +
                  "poll interval, so changes are delayed rather than missed.")]
    public static partial void ScheduleVersionUnreadable(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Warning,
        Message = "Could not subscribe to schedule changes; falling back to polling every " +
                  "{Interval}. Edits will be picked up, just not immediately.")]
    public static partial void ScheduleSubscribeFailed(
        this ILogger logger, Exception exception, TimeSpan interval);

    [LoggerMessage(
        EventId = 3103,
        Level = LogLevel.Warning,
        Message = "The stored schedule for '{JobName}' could not be read and has been ignored. " +
                  "The job falls back to whatever the code declared.")]
    public static partial void ScheduleUnreadable(this ILogger logger, string jobName);

    // 3200-3299: run history.

    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Warning,
        Message = "Dropped {Count} progress entr(ies): the buffer is full because the job is " +
                  "reporting faster than they can be written. The run itself is unaffected.")]
    public static partial void ProgressDropped(this ILogger logger, int count);

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Warning,
        Message = "Failed to write {Count} buffered progress entr(ies). The runs they belong to " +
                  "are unaffected.")]
    public static partial void ProgressFlushFailed(
        this ILogger logger, Exception exception, int count);
}
