using Cadence.Storage;
using Microsoft.Extensions.Primitives;

namespace Cadence.Api.Tests;

/// <summary>
/// An in-memory <see cref="IWritableScheduleSource"/>, versioning rows the way a storage package
/// does. It lets these tests present an editable schedule without one, and being writable is also
/// what mounts the operator tree's schedule route at all.
/// </summary>
internal sealed class FakeWritableScheduleSource : IWritableScheduleSource
{
    private readonly Dictionary<string, JobSchedule> _schedules = [];

    private JobSchedule? _last;

    /// <summary>The schedule most recently written, as it was stored, or null when none has been.</summary>
    public JobSchedule? Last
    {
        get
        {
            lock (_schedules)
            {
                return _last;
            }
        }
    }

    /// <summary>Seeds a row, as a store that already holds one would.</summary>
    /// <param name="schedule">The row to seed.</param>
    public void Seed(JobSchedule schedule)
    {
        lock (_schedules)
        {
            _schedules[schedule.JobName] = schedule;
        }
    }

    public Task<IReadOnlyList<JobSchedule>> GetAllAsync(CancellationToken cancellationToken)
    {
        lock (_schedules)
        {
            return Task.FromResult<IReadOnlyList<JobSchedule>>([.. _schedules.Values]);
        }
    }

    public Task<JobSchedule?> GetAsync(string jobName, CancellationToken cancellationToken)
    {
        lock (_schedules)
        {
            return Task.FromResult(_schedules.GetValueOrDefault(jobName));
        }
    }

    /// <summary>A token that never fires, which is what a source nothing else writes to has.</summary>
    public IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);

    public Task UpsertAsync(JobSchedule schedule, CancellationToken cancellationToken)
    {
        lock (_schedules)
        {
            var current = _schedules.GetValueOrDefault(schedule.JobName)?.Version ?? 0;

            // Zero means "I did not read this row first, just make it so", which is how the SQL and
            // Redis sources read it; any other value has to match what is stored.
            if (schedule.Version != 0 && schedule.Version != current)
            {
                throw new ScheduleConflictException(schedule.JobName, schedule.Version, current);
            }

            _last = schedule with { Version = current + 1 };
            _schedules[schedule.JobName] = _last;

            return Task.CompletedTask;
        }
    }
}
