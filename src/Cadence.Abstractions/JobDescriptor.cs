namespace Cadence;

/// <summary>
/// A job's registration metadata, produced from an attribute or the fluent builder. These are
/// the code-declared defaults; a writable schedule source overrides them at runtime.
/// </summary>
public sealed record JobDescriptor
{
    /// <summary>Stable, unique identity. Registering two jobs with the same name fails at boot.</summary>
    public required string Name { get; init; }

    /// <summary>The type resolved from DI once per run.</summary>
    public required Type ImplementationType { get; init; }

    /// <summary>Which triggers this job accepts.</summary>
    public TriggerKind AllowedTriggers { get; init; } = TriggerKind.Schedule;

    /// <summary>Default cron expression. Null for a job that is only triggered explicitly.</summary>
    public string? DefaultCron { get; init; }

    /// <summary>Timezone the cron expression is evaluated in.</summary>
    public TimeZoneInfo DefaultTimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>Whether the job runs unless a schedule store says otherwise.</summary>
    public bool DefaultEnabled { get; init; } = true;

    /// <summary>Default overlap behaviour.</summary>
    public OverlapPolicy Overlap { get; init; } = OverlapPolicy.Skip;

    /// <summary>Default missed-occurrence behaviour.</summary>
    public MissedRunPolicy OnMissed { get; init; } = MissedRunPolicy.SkipToNext;

    /// <summary>Default maximum run duration. Null means no limit.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>True when this job participates in cron scheduling.</summary>
    public bool IsScheduled => AllowedTriggers.HasFlag(TriggerKind.Schedule);
}
