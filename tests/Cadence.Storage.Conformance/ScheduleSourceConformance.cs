using Microsoft.Extensions.Primitives;
using Xunit;

namespace Cadence.Storage.Conformance;

/// <summary>
/// The behaviour every writable <see cref="IScheduleSource"/> must have.
/// </summary>
/// <remarks>
/// DB-editable schedules are the product, so the contract worth pinning is not just "a row round
/// trips" but the three things a dashboard depends on: an override survives exactly as written, a
/// concurrent edit is refused rather than silently overwriting someone, and an out-of-band change
/// eventually reaches every instance.
/// </remarks>
public abstract class ScheduleSourceConformance
{
    /// <summary>Creates a source with no schedules in it.</summary>
    protected abstract Task<IWritableScheduleSource> CreateAsync();

    /// <summary>
    /// Gives the source a chance to notice a change made through a different connection or instance.
    /// </summary>
    /// <remarks>
    /// A polling source needs its poll driven; an in-process source has already signalled. Neither
    /// should be simulated with a sleep, so each tier implements this its own way.
    /// </remarks>
    /// <param name="source">The source under test.</param>
    protected virtual Task PollAsync(IWritableScheduleSource source) => Task.CompletedTask;

    [SkippableFact]
    public async Task An_upserted_schedule_reads_back_field_for_field()
    {
        var source = await CreateAsync();

        await source.UpsertAsync(
            new JobSchedule
            {
                JobName = "invoice-sync",
                CronExpression = "0 */15 * * * *",
                TimeZoneId = "Europe/Stockholm",
                Enabled = true,
                Overlap = OverlapPolicy.AllowConcurrent,
                MaxDuration = TimeSpan.FromMinutes(10),
                Settings = new Dictionary<string, string> { ["batch"] = "500" },
            },
            default);

        var stored = await source.GetAsync("invoice-sync", default);

        Assert.NotNull(stored);
        Assert.Equal("0 */15 * * * *", stored.CronExpression);
        Assert.Equal("Europe/Stockholm", stored.TimeZoneId);
        Assert.True(stored.Enabled);
        Assert.Equal(OverlapPolicy.AllowConcurrent, stored.Overlap);
        Assert.Equal(TimeSpan.FromMinutes(10), stored.MaxDuration);
        Assert.Equal("500", stored.Settings["batch"]);
    }

    [SkippableFact]
    public async Task Absent_overrides_stay_absent()
    {
        // Null means "defer to what the code declared", which is not the same as any particular
        // value. A source that helpfully filled these in would silently override the job's own
        // declaration.
        var source = await CreateAsync();

        await source.UpsertAsync(Schedule("job"), default);

        var stored = await source.GetAsync("job", default);

        Assert.NotNull(stored);
        Assert.Null(stored.Overlap);
        Assert.Null(stored.MaxDuration);
        Assert.Empty(stored.Settings);
    }

    [SkippableFact]
    public async Task An_unknown_job_reads_back_as_null()
    {
        var source = await CreateAsync();

        Assert.Null(await source.GetAsync("never-configured", default));
    }

    [SkippableFact]
    public async Task GetAll_returns_every_schedule()
    {
        var source = await CreateAsync();

        await source.UpsertAsync(Schedule("a"), default);
        await source.UpsertAsync(Schedule("b"), default);

        var all = await source.GetAllAsync(default);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.JobName == "a");
        Assert.Contains(all, s => s.JobName == "b");
    }

    [SkippableFact]
    public async Task GetAll_is_empty_when_nothing_is_configured()
    {
        var source = await CreateAsync();

        Assert.Empty(await source.GetAllAsync(default));
    }

    [SkippableFact]
    public async Task Upserting_an_existing_job_updates_it_rather_than_duplicating()
    {
        var source = await CreateAsync();

        await source.UpsertAsync(Schedule("job") with { CronExpression = "0 * * * *" }, default);

        var first = await source.GetAsync("job", default);
        Assert.NotNull(first);

        await source.UpsertAsync(first with { CronExpression = "*/5 * * * *" }, default);

        var all = await source.GetAllAsync(default);
        var stored = Assert.Single(all);

        Assert.Equal("*/5 * * * *", stored.CronExpression);
    }

    [SkippableFact]
    public async Task Each_write_advances_the_version()
    {
        var source = await CreateAsync();

        await source.UpsertAsync(Schedule("job"), default);
        var first = await source.GetAsync("job", default);
        Assert.NotNull(first);

        await source.UpsertAsync(first with { Enabled = false }, default);
        var second = await source.GetAsync("job", default);

        Assert.NotNull(second);
        Assert.True(second.Version > first.Version, "the version must move on every write");
    }

    [SkippableFact]
    public async Task A_write_against_a_stale_version_is_refused()
    {
        var source = await CreateAsync();

        await source.UpsertAsync(Schedule("job"), default);
        var read = await source.GetAsync("job", default);
        Assert.NotNull(read);

        // Someone else saves first.
        await source.UpsertAsync(read with { CronExpression = "0 1 * * *" }, default);

        // Then this caller saves the copy it read before that happened.
        var conflict = await Assert.ThrowsAsync<ScheduleConflictException>(
            () => source.UpsertAsync(read with { CronExpression = "0 2 * * *" }, default));

        Assert.Equal("job", conflict.JobName);

        // And the write that won is still the one in the table.
        var stored = await source.GetAsync("job", default);
        Assert.NotNull(stored);
        Assert.Equal("0 1 * * *", stored.CronExpression);
    }

    [SkippableFact]
    public async Task Version_zero_writes_unconditionally()
    {
        // What a caller that never read the row has, and what a source that does not version rows
        // produces. It has to mean "just make it so" or those callers could never write at all.
        var source = await CreateAsync();

        await source.UpsertAsync(Schedule("job"), default);
        await source.UpsertAsync(Schedule("job") with { CronExpression = "0 3 * * *", Version = 0 }, default);

        var stored = await source.GetAsync("job", default);

        Assert.NotNull(stored);
        Assert.Equal("0 3 * * *", stored.CronExpression);
    }

    [SkippableFact]
    public async Task A_change_fires_the_token()
    {
        var source = await CreateAsync();
        var token = source.GetChangeToken();

        Assert.False(token.HasChanged);

        await source.UpsertAsync(Schedule("job"), default);
        await PollAsync(source);

        Assert.True(token.HasChanged, "an edit has to reach instances without a restart");
    }

    [SkippableFact]
    public async Task A_fired_token_is_replaced_by_a_fresh_one()
    {
        // Otherwise the first change is the only one an instance ever notices.
        var source = await CreateAsync();
        var first = source.GetChangeToken();

        await source.UpsertAsync(Schedule("job"), default);
        await PollAsync(source);

        Assert.True(first.HasChanged);

        var second = source.GetChangeToken();
        Assert.False(second.HasChanged);

        var read = await source.GetAsync("job", default);
        Assert.NotNull(read);

        await source.UpsertAsync(read with { Enabled = false }, default);
        await PollAsync(source);

        Assert.True(second.HasChanged);
    }

    [SkippableFact]
    public async Task A_registered_callback_runs_on_change()
    {
        var source = await CreateAsync();
        var fired = 0;

        using var registration = ChangeToken.OnChange(source.GetChangeToken, () => Interlocked.Increment(ref fired));

        await source.UpsertAsync(Schedule("job"), default);
        await PollAsync(source);

        Assert.True(Volatile.Read(ref fired) > 0);
    }

    /// <summary>A minimal schedule, for tests that care about one field at a time.</summary>
    /// <param name="jobName">The job the schedule belongs to.</param>
    protected static JobSchedule Schedule(string jobName) => new()
    {
        JobName = jobName,
        CronExpression = "0 * * * *",
        TimeZoneId = "UTC",
        Enabled = true,
    };
}
