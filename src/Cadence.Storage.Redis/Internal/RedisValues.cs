using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace Cadence.Storage.Redis.Internal;

/// <summary>
/// Converts between Cadence's records and the flat strings Redis stores.
/// </summary>
/// <remarks>
/// <para>
/// Runs live in a hash of scalar fields rather than as one JSON blob, because the janitor and the
/// reap pass read a single field — status, instance, start — off thousands of runs, and parsing a
/// whole document per run to answer that would make every maintenance pass proportional to the size
/// of the history rather than to the work in it.
/// </para>
/// <para>
/// Instants are stored as UTC ticks. They are the sort key for every index here, and a numeric
/// score has to agree exactly with the stored value or a range query returns something a
/// reader cannot explain.
/// </para>
/// </remarks>
internal static class RedisValues
{
    /// <summary>Field names on a run hash. Short, because they are repeated per run.</summary>
    public static class RunField
    {
        public const string JobName = "job";
        public const string ScheduledFor = "sched";
        public const string Trigger = "trig";
        public const string Status = "status";
        public const string InstanceId = "inst";
        public const string StartedAt = "start";
        public const string CompletedAt = "done";
        public const string DurationMs = "dur";
        public const string Error = "err";
    }

    /// <summary>Renders an instant as the UTC ticks used for both storage and index scores.</summary>
    public static long Ticks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    /// <summary>Reads back an instant stored as UTC ticks.</summary>
    public static DateTimeOffset FromTicks(long ticks)
        => new(new DateTime(ticks, DateTimeKind.Utc));

    /// <summary>The hash fields describing a starting run.</summary>
    public static HashEntry[] StartEntries(JobRunStart start)
    {
        ArgumentNullException.ThrowIfNull(start);

        return
        [
            new(RunField.JobName, start.JobName),
            new(RunField.ScheduledFor, start.ScheduledFor is { } s ? Ticks(s) : RedisValue.EmptyString),
            new(RunField.Trigger, (int)start.Trigger),
            new(RunField.Status, (int)RunStatus.Running),
            new(RunField.InstanceId, start.InstanceId),
            new(RunField.StartedAt, Ticks(start.StartedAt)),
        ];
    }

    /// <summary>Rebuilds a run from its hash, or null when the hash is absent.</summary>
    /// <remarks>
    /// A hash that exists but lacks the fields a run must have is treated as absent rather than
    /// throwing. That state is reachable exactly once — between a janitor deleting a run and the
    /// index entry that pointed at it being removed — and a query stumbling over it should skip the
    /// record, not fail.
    /// </remarks>
    public static JobRun? ToRun(Guid runId, HashEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Length == 0)
        {
            return null;
        }

        var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value, StringComparer.Ordinal);

        if (!map.TryGetValue(RunField.JobName, out var jobName) || jobName.IsNullOrEmpty ||
            !map.TryGetValue(RunField.StartedAt, out var startedAt) || !startedAt.TryParse(out long startTicks) ||
            !map.TryGetValue(RunField.Status, out var status) || !status.TryParse(out int statusValue))
        {
            return null;
        }

        map.TryGetValue(RunField.InstanceId, out var instanceId);
        map.TryGetValue(RunField.Trigger, out var trigger);

        return new JobRun
        {
            RunId = runId,
            JobName = (string)jobName!,
            ScheduledFor = OptionalInstant(map, RunField.ScheduledFor),
            Trigger = trigger.TryParse(out int triggerValue) ? (TriggerKind)triggerValue : TriggerKind.Schedule,
            Status = (RunStatus)statusValue,
            InstanceId = instanceId.IsNullOrEmpty ? string.Empty : (string)instanceId!,
            StartedAt = FromTicks(startTicks),
            CompletedAt = OptionalInstant(map, RunField.CompletedAt),
            Duration = OptionalDuration(map),
            Error = map.TryGetValue(RunField.Error, out var error) && !error.IsNullOrEmpty
                ? (string)error!
                : null,
        };
    }

    /// <summary>Serialises a progress entry for the run's log list.</summary>
    public static string SerialiseLogEntry(JobLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return JsonSerializer.Serialize(new StoredLogEntry
        {
            Timestamp = Ticks(entry.Timestamp),
            Message = entry.Message,
            Data = entry.Data is { Count: > 0 } data ? new Dictionary<string, object?>(data) : null,
        });
    }

    /// <summary>Reads a progress entry back, or null when it cannot be parsed.</summary>
    /// <remarks>
    /// Null rather than an exception, for the same reason the SQL tier tolerates unreadable
    /// progress data: a malformed entry should cost that one line of a run's log, not the ability
    /// to read the run.
    /// </remarks>
    public static JobLogEntry? DeserialiseLogEntry(string json)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredLogEntry>(json);

            if (stored?.Message is null)
            {
                return null;
            }

            return new JobLogEntry
            {
                Timestamp = FromTicks(stored.Timestamp),
                Message = stored.Message,
                Data = stored.Data,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serialises a schedule for the schedules hash.</summary>
    /// <remarks>
    /// Without its version. The version lives in a parallel hash so the upsert script can compare
    /// and advance it without parsing or rewriting this document — which would mean either
    /// string-surgery in Lua or a read-modify-write that is no longer atomic, and the optimistic
    /// concurrency check is the one thing here that has to be.
    /// </remarks>
    public static string SerialiseSchedule(JobSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return JsonSerializer.Serialize(new StoredSchedule
        {
            CronExpression = schedule.CronExpression,
            TimeZoneId = schedule.TimeZoneId,
            Enabled = schedule.Enabled,
            Overlap = schedule.Overlap is { } overlap ? (int)overlap : null,
            MaxDurationMs = schedule.MaxDuration is { } max ? (long)max.TotalMilliseconds : null,
            Settings = schedule.Settings.Count == 0
                ? null
                : new Dictionary<string, string>(schedule.Settings, StringComparer.Ordinal),
        });
    }

    /// <summary>Reads a schedule back, or null when it cannot be parsed.</summary>
    /// <param name="jobName">The hash field the document was stored under.</param>
    /// <param name="json">The stored document.</param>
    /// <param name="version">The version from the parallel hash.</param>
    public static JobSchedule? DeserialiseSchedule(string jobName, string json, int version)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSchedule>(json);

            if (stored?.CronExpression is null || stored.TimeZoneId is null)
            {
                return null;
            }

            return new JobSchedule
            {
                JobName = jobName,
                CronExpression = stored.CronExpression,
                TimeZoneId = stored.TimeZoneId,
                Enabled = stored.Enabled,
                Overlap = stored.Overlap is { } overlap ? (OverlapPolicy)overlap : null,
                MaxDuration = stored.MaxDurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                Settings = stored.Settings is { Count: > 0 } settings
                    ? settings
                    : new Dictionary<string, string>(StringComparer.Ordinal),
                Version = version,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Formats a value for a Lua argument, invariantly.</summary>
    public static string Argument(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? OptionalInstant(Dictionary<string, RedisValue> map, string field)
        => map.TryGetValue(field, out var value) && value.TryParse(out long ticks)
            ? FromTicks(ticks)
            : null;

    private static TimeSpan? OptionalDuration(Dictionary<string, RedisValue> map)
        => map.TryGetValue(RunField.DurationMs, out var value) && value.TryParse(out long ms)
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    private sealed class StoredLogEntry
    {
        public long Timestamp { get; init; }

        public string? Message { get; init; }

        public Dictionary<string, object?>? Data { get; init; }
    }

    private sealed class StoredSchedule
    {
        public string? CronExpression { get; init; }

        public string? TimeZoneId { get; init; }

        public bool Enabled { get; init; }

        public int? Overlap { get; init; }

        public long? MaxDurationMs { get; init; }

        public Dictionary<string, string>? Settings { get; init; }
    }
}
