using Cadence;
using Cadence.Storage;
using Microsoft.Extensions.Primitives;

namespace Cadence.Core.Tests;

/// <summary>A clock the test drives, so nothing has to sleep.</summary>
internal sealed class FakeClock : ISystemClock
{
    public FakeClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>A coordinator whose answer the test chooses.</summary>
internal sealed class ScriptedCoordinator : IOccurrenceCoordinator
{
    private readonly Func<string, DateTimeOffset, bool> _decide;

    public ScriptedCoordinator(bool grantAll) => _decide = (_, _) => grantAll;

    public ScriptedCoordinator(Func<string, DateTimeOffset, bool> decide) => _decide = decide;

    public List<(string JobName, DateTimeOffset Occurrence, Guid RunId)> Attempts { get; } = [];

    public Task<bool> TryClaimAsync(
        string jobName,
        DateTimeOffset scheduledFor,
        Guid runId,
        CancellationToken ct)
    {
        Attempts.Add((jobName, scheduledFor, runId));
        return Task.FromResult(_decide(jobName, scheduledFor));
    }
}

/// <summary>
/// A schedule store the test edits, standing in for the SQL source.
/// </summary>
/// <remarks>
/// Held to the same contract as the real thing by <c>MutableScheduleSourceConformanceTests</c>,
/// including optimistic concurrency. A double that was laxer than the source it stands in for would
/// make every test built on it worth less than it looks.
/// </remarks>
internal sealed class MutableScheduleSource : IWritableScheduleSource, IDisposable
{
    private readonly Dictionary<string, JobSchedule> _rows = new(StringComparer.Ordinal);
    private CancellationTokenSource _changed = new();

    public Task<IReadOnlyList<JobSchedule>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JobSchedule>>([.. _rows.Values]);

    public Task<JobSchedule?> GetAsync(string jobName, CancellationToken ct)
        => Task.FromResult(_rows.TryGetValue(jobName, out var row) ? row : null);

    public Task UpsertAsync(JobSchedule schedule, CancellationToken ct)
    {
        // Version zero means the caller never read the row, so it writes unconditionally. Any other
        // value has to match what is stored, or someone else has edited it since.
        if (_rows.TryGetValue(schedule.JobName, out var existing)
            && schedule.Version != 0
            && schedule.Version != existing.Version)
        {
            throw new ScheduleConflictException(schedule.JobName, schedule.Version, existing.Version);
        }

        Set(schedule);
        return Task.CompletedTask;
    }

    public IChangeToken GetChangeToken() => new CancellationChangeToken(_changed.Token);

    /// <summary>Writes a schedule with no concurrency check, and signals the change.</summary>
    public void Set(JobSchedule schedule)
    {
        var version = _rows.TryGetValue(schedule.JobName, out var existing) ? existing.Version : 0;

        _rows[schedule.JobName] = schedule with { Version = version + 1 };

        var previous = _changed;
        _changed = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }

    public void SetEnabled(string jobName, bool enabled)
        => Set(_rows[jobName] with { Enabled = enabled });

    public void Dispose() => _changed.Dispose();
}

internal static class Occurrences
{
    /// <summary>Europe/Stockholm, resolved once so the DST tests fail loudly if ICU is missing.</summary>
    public static TimeZoneInfo Stockholm { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    public static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);
}
