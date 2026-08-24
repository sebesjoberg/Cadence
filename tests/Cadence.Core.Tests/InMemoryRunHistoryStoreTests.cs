using Cadence.Storage;
using Xunit;

namespace Cadence.Core.Tests;

public class InMemoryRunHistoryStoreTests
{
    private static readonly DateTimeOffset Origin = Occurrences.Utc(2026, 8, 24, 2, 0);

    [Fact]
    public async Task Consecutive_failures_stop_counting_at_the_last_success()
    {
        var store = new InMemoryRunHistoryStore();

        await Record(store, 0, RunStatus.Failed);
        await Record(store, 1, RunStatus.Succeeded);
        await Record(store, 2, RunStatus.Failed);
        await Record(store, 3, RunStatus.TimedOut);

        Assert.Equal(2, await store.CountConsecutiveFailuresAsync("job", CancellationToken.None));
    }

    [Fact]
    public async Task Skipped_and_aborted_runs_neither_extend_nor_break_a_failure_streak()
    {
        var store = new InMemoryRunHistoryStore();

        await Record(store, 0, RunStatus.Failed);
        await Record(store, 1, RunStatus.Skipped);
        await Record(store, 2, RunStatus.Aborted);
        await Record(store, 3, RunStatus.Failed);

        // Neither says anything about whether the job's own logic is broken.
        Assert.Equal(2, await store.CountConsecutiveFailuresAsync("job", CancellationToken.None));
    }

    [Fact]
    public async Task Last_success_ignores_later_failures()
    {
        var store = new InMemoryRunHistoryStore();

        var success = await Record(store, 0, RunStatus.Succeeded);
        await Record(store, 1, RunStatus.Failed);

        var lastSuccess = await store.GetLastSuccessAsync("job", CancellationToken.None);
        Assert.Equal(success, lastSuccess!.RunId);
    }

    [Fact]
    public async Task The_ring_drops_the_oldest_runs_once_it_is_full()
    {
        var store = new InMemoryRunHistoryStore(new InMemoryRunHistoryOptions { MaxRunsPerJob = 3 });

        for (var i = 0; i < 5; i++)
        {
            await Record(store, i, RunStatus.Succeeded);
        }

        var runs = await store.QueryAsync(new RunQuery { JobName = "job" }, CancellationToken.None);

        Assert.Equal(3, runs.Count);
        Assert.Equal(Origin.AddMinutes(4), runs[0].StartedAt);
        Assert.Equal(Origin.AddMinutes(2), runs[^1].StartedAt);
    }

    [Fact]
    public async Task Completing_a_run_that_has_already_been_trimmed_is_not_an_error()
    {
        var store = new InMemoryRunHistoryStore(new InMemoryRunHistoryOptions { MaxRunsPerJob = 1 });

        var first = await Record(store, 0, RunStatus.Running);
        await Record(store, 1, RunStatus.Running);

        await store.CompleteAsync(
            first,
            JobRunResult.Success(TimeSpan.Zero, Origin),
            CancellationToken.None);
    }

    [Fact]
    public async Task Queries_filter_by_status_time_and_instance()
    {
        var store = new InMemoryRunHistoryStore();

        await Record(store, 0, RunStatus.Succeeded);
        await Record(store, 10, RunStatus.Failed);
        await Record(store, 20, RunStatus.Failed, instanceId: "other:2:bbbbbbbb");

        var failuresFromOneInstance = await store.QueryAsync(
            new RunQuery
            {
                JobName = "job",
                Statuses = [RunStatus.Failed],
                InstanceId = "test:1:aaaaaaaa",
            },
            CancellationToken.None);

        var run = Assert.Single(failuresFromOneInstance);
        Assert.Equal(Origin.AddMinutes(10), run.StartedAt);

        var recent = await store.QueryAsync(
            new RunQuery { JobName = "job", From = Origin.AddMinutes(5) },
            CancellationToken.None);

        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task Purging_removes_old_runs_but_leaves_running_ones_alone()
    {
        var store = new InMemoryRunHistoryStore();

        await Record(store, 0, RunStatus.Succeeded);
        await Record(store, 1, RunStatus.Running);
        await Record(store, 30, RunStatus.Succeeded);

        await store.PurgeAsync(Origin.AddMinutes(20), CancellationToken.None);

        var runs = await store.QueryAsync(new RunQuery { JobName = "job" }, CancellationToken.None);

        // A run still in flight has no business being deleted just because it started long ago.
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Status == RunStatus.Running);
    }

    private static async Task<Guid> Record(
        InMemoryRunHistoryStore store,
        int minuteOffset,
        RunStatus status,
        string instanceId = "test:1:aaaaaaaa")
    {
        var runId = Guid.NewGuid();
        var startedAt = Origin.AddMinutes(minuteOffset);

        await store.StartAsync(
            new JobRunStart
            {
                RunId = runId,
                JobName = "job",
                Trigger = TriggerKind.Schedule,
                InstanceId = instanceId,
                StartedAt = startedAt,
                ScheduledFor = startedAt,
            },
            CancellationToken.None);

        if (status != RunStatus.Running)
        {
            await store.CompleteAsync(
                runId,
                new JobRunResult
                {
                    Status = status,
                    Duration = TimeSpan.FromSeconds(1),
                    CompletedAt = startedAt.AddSeconds(1),
                },
                CancellationToken.None);
        }

        return runId;
    }
}
