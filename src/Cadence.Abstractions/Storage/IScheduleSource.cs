using Microsoft.Extensions.Primitives;

namespace Cadence.Storage;

/// <summary>
/// Supplies what should run and when. Separate from run history and from claiming because the three
/// have different write volumes and different consequences when unavailable — that split is what
/// makes the no-infrastructure configuration coherent rather than degraded.
/// </summary>
public interface IScheduleSource
{
    /// <summary>All configured schedules.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<JobSchedule>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>One schedule by job name, or null when the source has no row for it.</summary>
    /// <param name="jobName">The job's stable name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<JobSchedule?> GetAsync(string jobName, CancellationToken cancellationToken);

    /// <summary>
    /// A token that fires when an external actor changes configuration. SQL sources poll a version
    /// row; Redis sources use pub/sub; code and configuration sources return a token that never
    /// fires.
    /// </summary>
    IChangeToken GetChangeToken();
}
