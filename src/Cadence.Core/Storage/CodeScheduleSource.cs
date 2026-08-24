using Microsoft.Extensions.Primitives;

namespace Cadence.Storage;

/// <summary>
/// Serves schedules from the code-declared descriptors. Read-only, so the dashboard renders
/// schedule fields disabled with an explanatory banner — a coherent mode, not a broken one.
/// </summary>
public sealed class CodeScheduleSource : IScheduleSource
{
    private readonly IJobRegistry _registry;

    /// <summary>Creates the source over a registry.</summary>
    /// <param name="registry">The registered jobs.</param>
    public CodeScheduleSource(IJobRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobSchedule>> GetAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<JobSchedule> schedules =
        [
            .. _registry.All
                .Where(d => d.DefaultCron is not null)
                .Select(ToSchedule),
        ];

        return Task.FromResult(schedules);
    }

    /// <inheritdoc />
    public Task<JobSchedule?> GetAsync(string jobName, CancellationToken cancellationToken)
    {
        var found = _registry.TryGet(jobName, out var descriptor) && descriptor!.DefaultCron is not null
            ? ToSchedule(descriptor)
            : null;

        return Task.FromResult(found);
    }

    /// <summary>Code-declared schedules never change at runtime, so the token never fires.</summary>
    /// <returns>A token that is permanently inactive.</returns>
    public IChangeToken GetChangeToken() => NeverChangeToken.Instance;

    private static JobSchedule ToSchedule(JobDescriptor descriptor) => new()
    {
        JobName = descriptor.Name,
        CronExpression = descriptor.DefaultCron!,
        TimeZoneId = descriptor.DefaultTimeZone.Id,
        Enabled = descriptor.DefaultEnabled,
        Overlap = descriptor.Overlap,
        MaxDuration = descriptor.MaxDuration,
    };
}
