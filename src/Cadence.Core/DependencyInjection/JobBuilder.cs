using Cadence.Scheduling;

namespace Cadence.DependencyInjection;

/// <summary>
/// Fluent configuration for one job. Use this when the defaults are computed; use
/// <see cref="ScheduledJobAttribute"/> when they are literals.
/// </summary>
public sealed class JobBuilder
{
    private readonly string _name;
    private readonly Type _implementationType;

    private string? _cron;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private bool _enabled = true;
    private OverlapPolicy _overlap = OverlapPolicy.Skip;
    private MissedRunPolicy _onMissed = MissedRunPolicy.SkipToNext;
    private TimeSpan? _maxDuration;
    private TriggerKind _triggers = TriggerKind.Schedule;

    internal JobBuilder(string name, Type implementationType)
    {
        _name = name;
        _implementationType = implementationType;
    }

    /// <summary>Sets the cron expression, and optionally the zone it is evaluated in.</summary>
    /// <param name="expression">A 5- or 6-field cron expression, validated immediately.</param>
    /// <param name="timeZone">The zone to evaluate in. Defaults to UTC.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="CadenceStartupException">The expression is not valid.</exception>
    public JobBuilder Cron(string expression, TimeZoneInfo? timeZone = null)
    {
        // Validated here rather than at first tick: a bad expression should fail the deploy, not
        // throw once a second in production.
        if (!CronParser.TryParse(expression, out _, out var error))
        {
            throw new CadenceStartupException($"Job '{_name}' has an invalid cron expression. {error}");
        }

        _cron = expression;

        if (timeZone is not null)
        {
            _timeZone = timeZone;
        }

        return this;
    }

    /// <summary>Sets the zone the cron expression is evaluated in.</summary>
    /// <param name="timeZone">The zone.</param>
    /// <returns>This builder.</returns>
    public JobBuilder TimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        _timeZone = timeZone;
        return this;
    }

    /// <summary>Sets whether the job runs unless a schedule store says otherwise.</summary>
    /// <param name="enabled">False to register the job but leave it dormant.</param>
    /// <returns>This builder.</returns>
    public JobBuilder Enabled(bool enabled = true)
    {
        _enabled = enabled;
        return this;
    }

    /// <summary>Sets what happens when an occurrence is due and a previous run is still going.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>This builder.</returns>
    public JobBuilder Overlap(OverlapPolicy policy)
    {
        _overlap = policy;
        return this;
    }

    /// <summary>Sets what happens to occurrences that came due while nothing was watching.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>This builder.</returns>
    public JobBuilder OnMissed(MissedRunPolicy policy)
    {
        _onMissed = policy;
        return this;
    }

    /// <summary>Caps how long a run may take before its cancellation token is signalled.</summary>
    /// <param name="maxDuration">The limit. Must be positive.</param>
    /// <returns>This builder.</returns>
    public JobBuilder MaxDuration(TimeSpan maxDuration)
    {
        if (maxDuration <= TimeSpan.Zero)
        {
            throw new CadenceStartupException(
                $"Job '{_name}' has a maximum duration of {maxDuration}, which must be positive.");
        }

        _maxDuration = maxDuration;
        return this;
    }

    /// <summary>Sets which triggers the job accepts.</summary>
    /// <param name="triggers">The allowed triggers.</param>
    /// <returns>This builder.</returns>
    public JobBuilder Triggers(TriggerKind triggers)
    {
        _triggers = triggers;
        return this;
    }

    /// <summary>Registers the job as triggerable only, with no cron schedule.</summary>
    /// <returns>This builder.</returns>
    public JobBuilder ApiOnly()
    {
        _triggers = TriggerKind.Api | TriggerKind.Manual;
        _cron = null;
        return this;
    }

    internal JobDescriptor Build()
    {
        if (_triggers.HasFlag(TriggerKind.Schedule) && string.IsNullOrWhiteSpace(_cron))
        {
            throw new CadenceStartupException(
                $"Job '{_name}' allows the schedule trigger but has no cron expression. " +
                "Either call Cron(...), or call ApiOnly() if it should only be triggered explicitly.");
        }

        if (_triggers == TriggerKind.None)
        {
            throw new CadenceStartupException(
                $"Job '{_name}' allows no triggers at all, so it could never run.");
        }

        return new JobDescriptor
        {
            Name = _name,
            ImplementationType = _implementationType,
            AllowedTriggers = _triggers,
            DefaultCron = _cron,
            DefaultTimeZone = _timeZone,
            DefaultEnabled = _enabled,
            Overlap = _overlap,
            OnMissed = _onMissed,
            MaxDuration = _maxDuration,
        };
    }
}
