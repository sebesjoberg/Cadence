using System.Collections.Immutable;

namespace Cadence.Storage;

/// <summary>The effective configuration for one job, as stored.</summary>
public sealed record JobSchedule
{
    /// <summary>The job this configuration belongs to.</summary>
    public required string JobName { get; init; }

    /// <summary>Cron expression, 5- or 6-field.</summary>
    public required string CronExpression { get; init; }

    /// <summary>IANA timezone id the expression is evaluated in.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>Whether the scheduler should act on this schedule.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Overrides the job's declared overlap policy when set.</summary>
    public OverlapPolicy? Overlap { get; init; }

    /// <summary>Overrides the job's declared maximum duration when set.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>Arbitrary per-job settings, surfaced to the job as <see cref="JobContext.Settings"/>.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    /// <summary>Optimistic-concurrency token. Zero for sources that do not version rows.</summary>
    public int Version { get; init; }
}
