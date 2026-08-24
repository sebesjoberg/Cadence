namespace Cadence.Storage;

/// <summary>
/// An <see cref="IScheduleSource"/> whose rows can be edited at runtime. Split out so a read-only
/// source is not forced to throw from a write method, and so the dashboard can branch on capability
/// rather than on a boolean.
/// </summary>
public interface IWritableScheduleSource : IScheduleSource
{
    /// <summary>
    /// Inserts or updates a schedule. Implementations enforce optimistic concurrency on
    /// <see cref="JobSchedule.Version"/> and throw <see cref="ScheduleConflictException"/> when the
    /// row moved underneath the caller.
    /// </summary>
    /// <param name="schedule">The schedule to persist.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task UpsertAsync(JobSchedule schedule, CancellationToken cancellationToken);
}
