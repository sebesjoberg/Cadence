namespace Cadence;

/// <summary>
/// Declares a job's registration metadata in code. Equivalent to the fluent
/// <c>AddJob&lt;T&gt;</c> overloads; use whichever suits — attributes for static defaults,
/// fluent for computed ones.
/// </summary>
/// <remarks>
/// Values here are <em>defaults</em>. A writable schedule source overrides cron, timezone,
/// enabled state and overlap policy at runtime.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ScheduledJobAttribute : Attribute
{
    /// <summary>
    /// The job's stable identity: unique, and kebab-case by convention. Renaming the class must
    /// not orphan the job's configuration or history, which is why identity is the name and not
    /// the CLR type.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Cron expression, 5- or 6-field (Cronos syntax). Required for a job that allows the
    /// <see cref="TriggerKind.Schedule"/> trigger; leave null for an API-only job.
    /// </summary>
    public string? Cron { get; init; }

    /// <summary>
    /// IANA timezone id the cron expression is evaluated in, for example
    /// <c>Europe/Stockholm</c>. Defaults to UTC.
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>Whether the job is enabled by default. A schedule store can override this.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>What to do when an occurrence is due and a previous run is still going.</summary>
    public OverlapPolicy Overlap { get; init; } = OverlapPolicy.Skip;

    /// <summary>What to do about occurrences missed while the host was down or the job disabled.</summary>
    public MissedRunPolicy OnMissed { get; init; } = MissedRunPolicy.SkipToNext;

    /// <summary>
    /// Maximum run duration as a <see cref="TimeSpan"/> string, for example <c>00:10:00</c>.
    /// The run's cancellation token is signalled at the limit and the run is recorded as timed
    /// out. Null means no limit, which is rarely what you want.
    /// </summary>
    public string? MaxDuration { get; init; }

    /// <summary>Which triggers this job accepts. Defaults to schedule only.</summary>
    public TriggerKind Triggers { get; init; } = TriggerKind.Schedule;
}
