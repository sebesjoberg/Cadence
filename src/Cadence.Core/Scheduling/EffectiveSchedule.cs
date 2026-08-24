using System.Collections.Immutable;
using Cronos;

namespace Cadence.Scheduling;

/// <summary>
/// A job's schedule after the store row has been layered over the code-declared defaults, with
/// the cron expression and timezone already parsed.
/// </summary>
/// <remarks>
/// Parsed once per configuration change rather than once per tick. Re-parsing cron and resolving
/// timezones every second is the cost that makes a naive tick loop scale badly with job count.
/// </remarks>
public sealed record EffectiveSchedule
{
    /// <summary>The job this schedule belongs to.</summary>
    public required JobDescriptor Descriptor { get; init; }

    /// <summary>The parsed cron expression.</summary>
    public required CronExpression Cron { get; init; }

    /// <summary>The original expression text, for display and diagnostics.</summary>
    public required string CronText { get; init; }

    /// <summary>The zone the expression is evaluated in.</summary>
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Whether the scheduler should act on this schedule.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Effective overlap policy.</summary>
    public required OverlapPolicy Overlap { get; init; }

    /// <summary>Effective maximum run duration, or null for no limit.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>Runtime-editable settings handed to the job.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    /// <summary>The next occurrence strictly after an instant, or null if the expression has none.</summary>
    /// <param name="after">The instant to search from, exclusive.</param>
    public DateTimeOffset? NextOccurrenceAfter(DateTimeOffset after)
        => Cron.GetNextOccurrence(after, TimeZone, inclusive: false);

    /// <summary>Projects the parts of this schedule that the run executor needs.</summary>
    public RunSettings ToRunSettings() => new()
    {
        Overlap = Overlap,
        MaxDuration = MaxDuration,
        Settings = Settings,
    };
}
