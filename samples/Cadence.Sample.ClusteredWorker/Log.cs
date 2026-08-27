using Microsoft.Extensions.Logging;

namespace Cadence.Sample.ClusteredWorker;

/// <summary>
/// The sample's own log messages, as source-generated <see cref="LoggerMessageAttribute"/> methods —
/// same reasoning as Cadence.Sample.Worker's: warnings are errors in this repository, and a sample
/// that needs a CA1848 waiver to compile teaches the exemption rather than the lesson.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Claimed the {ScheduledFor:O} occurrence on {Instance}.")]
    public static partial void ClaimedOccurrence(
        this ILogger logger, DateTimeOffset? scheduledFor, string instance);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Sweep starting on {Instance}; it will take {Seconds} seconds, which is longer " +
                  "than the interval between its own occurrences.")]
    public static partial void SweepStarting(this ILogger logger, string instance, double seconds);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Sweep finished on {Instance}.")]
    public static partial void SweepFinished(this ILogger logger, string instance);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Replica {Instance} joining the cluster against the shared Cadence database.")]
    public static partial void ReplicaStarting(this ILogger logger, string instance);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Reindex starting on {Instance}, by request rather than by schedule.")]
    public static partial void ReindexStarting(this ILogger logger, string instance);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Reindex finished on {Instance}.")]
    public static partial void ReindexFinished(this ILogger logger, string instance);
}
