using System.Collections.Immutable;
using Cadence.Storage;

namespace Cadence.Scheduling;

/// <summary>
/// Layers the schedule store over the code-declared defaults and parses the result.
/// </summary>
/// <remarks>
/// Resolution order is: store row, then code default. A job that allows the schedule trigger and
/// has neither is a configuration problem, reported rather than thrown, so one bad job does not
/// stop the rest of the schedule from running.
/// </remarks>
public sealed class ScheduleResolver
{
    private readonly IJobRegistry _registry;
    private readonly IScheduleSource _source;

    /// <summary>Creates the resolver.</summary>
    /// <param name="registry">The registered jobs.</param>
    /// <param name="source">Where stored schedules come from.</param>
    public ScheduleResolver(IJobRegistry registry, IScheduleSource source)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(source);

        _registry = registry;
        _source = source;
    }

    /// <summary>Reads the store and produces the effective schedule for every scheduled job.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<ScheduleResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        var stored = await _source.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var rows = stored.ToDictionary(s => s.JobName, StringComparer.Ordinal);

        var schedules = new Dictionary<string, EffectiveSchedule>(StringComparer.Ordinal);
        var problems = new List<ScheduleProblem>();

        foreach (var descriptor in _registry.All)
        {
            if (!descriptor.IsScheduled)
            {
                continue;
            }

            rows.TryGetValue(descriptor.Name, out var row);

            var cronText = row?.CronExpression ?? descriptor.DefaultCron;
            if (string.IsNullOrWhiteSpace(cronText))
            {
                problems.Add(new ScheduleProblem(
                    descriptor.Name,
                    "The job allows the schedule trigger but no cron expression is configured, " +
                    "in code or in the schedule store."));
                continue;
            }

            if (!CronParser.TryParse(cronText, out var cron, out var cronError))
            {
                problems.Add(new ScheduleProblem(descriptor.Name, cronError!));
                continue;
            }

            var timeZoneId = row?.TimeZoneId ?? descriptor.DefaultTimeZone.Id;
            if (!CronParser.TryResolveTimeZone(timeZoneId, out var timeZone, out var zoneError))
            {
                problems.Add(new ScheduleProblem(descriptor.Name, zoneError!));
                continue;
            }

            schedules[descriptor.Name] = new EffectiveSchedule
            {
                Descriptor = descriptor,
                Cron = cron!,
                CronText = cronText,
                TimeZone = timeZone!,
                Enabled = row?.Enabled ?? descriptor.DefaultEnabled,
                Overlap = row?.Overlap ?? descriptor.Overlap,
                MaxDuration = row?.MaxDuration ?? descriptor.MaxDuration,
                Settings = row?.Settings ?? ImmutableDictionary<string, string>.Empty,
            };
        }

        return new ScheduleResolution(schedules, problems);
    }

    /// <summary>Resolves the settings for a run that is not tied to a cron occurrence.</summary>
    /// <param name="descriptor">The job being triggered.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<RunSettings> ResolveRunSettingsAsync(
        JobDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var row = await _source.GetAsync(descriptor.Name, cancellationToken).ConfigureAwait(false);

        return new RunSettings
        {
            Overlap = row?.Overlap ?? descriptor.Overlap,
            MaxDuration = row?.MaxDuration ?? descriptor.MaxDuration,
            Settings = row?.Settings ?? ImmutableDictionary<string, string>.Empty,
        };
    }
}
