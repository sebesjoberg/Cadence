using Xunit;

namespace Cadence.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IRunHistoryStore"/> must have, whichever tier it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the in-memory and SQL tiers are meant to be interchangeable, and "meant to be"
/// decays fast. The failure it is guarding against is subtle and expensive: someone sets an alert on
/// two consecutive failures, adds a connection string six months later, and the threshold quietly
/// starts counting something slightly different. A shared suite makes that a build failure rather
/// than a 3am surprise.
/// </para>
/// <para>
/// It deliberately says nothing about retention. The in-memory store keeps a bounded ring and drops
/// the oldest runs; the SQL store keeps everything until the janitor comes round. Both are correct
/// for what they are, so that difference is tested per tier rather than pinned here.
/// </para>
/// </remarks>
public abstract class RunHistoryStoreConformance
{
    /// <summary>An instant every test measures from, so nothing depends on the wall clock.</summary>
    protected static readonly DateTimeOffset Origin = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a store with no history in it.</summary>
    /// <remarks>Each test gets its own, so tests never see each other's rows.</remarks>
    protected abstract Task<IRunHistoryStore> CreateAsync();

    /// <summary>
    /// Makes any buffered writes readable. The SQL tier batches progress appends; the in-memory tier
    /// has nothing to do.
    /// </summary>
    /// <param name="store">The store under test.</param>
    protected virtual Task SettleAsync(IRunHistoryStore store) => Task.CompletedTask;

    [SkippableFact]
    public async Task AStartedRunReadsBackAsRunning()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        await store.StartAsync(Start(runId, "job", Origin), default);

        var run = await store.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(runId, run.RunId);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(Origin, run.StartedAt);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.Duration);
    }

    [SkippableFact]
    public async Task CompletingARunRecordsStatusDurationAndError()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        await store.StartAsync(Start(runId, "job", Origin), default);

        await store.CompleteAsync(
            runId,
            JobRunResult.Failed(TimeSpan.FromSeconds(3), Origin.AddSeconds(3), new InvalidOperationException("boom")),
            default);

        var run = await store.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(TimeSpan.FromSeconds(3), run.Duration);
        Assert.Equal(Origin.AddSeconds(3), run.CompletedAt);
        Assert.Contains("boom", run.Error, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task CompletingARunThatIsGoneIsNotAnError()
    {
        var store = await CreateAsync();

        // The janitor may have purged or reaped it first. The point of the completion write is that
        // nothing is left claiming to be running, and a row that no longer exists satisfies that.
        await store.CompleteAsync(Guid.NewGuid(), JobRunResult.Success(TimeSpan.Zero, Origin), default);
    }

    [SkippableFact]
    public async Task AnOccurrenceIsRecordedAgainstTheRun()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();
        var occurrence = Origin.AddMinutes(-1);

        await store.StartAsync(
            Start(runId, "job", Origin) with { ScheduledFor = occurrence },
            default);

        var run = await store.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(occurrence, run.ScheduledFor);
    }

    [SkippableFact]
    public async Task RunsOfOtherJobsAreNotReturned()
    {
        var store = await CreateAsync();

        await store.StartAsync(Start(Guid.NewGuid(), "wanted", Origin), default);
        await store.StartAsync(Start(Guid.NewGuid(), "other", Origin.AddMinutes(1)), default);

        var run = await store.GetLastRunAsync("wanted", default);

        Assert.NotNull(run);
        Assert.Equal("wanted", run.JobName);
    }

    [SkippableFact]
    public async Task GetLastRunReturnsNullForAJobWithNoHistory()
    {
        var store = await CreateAsync();

        Assert.Null(await store.GetLastRunAsync("never-run", default));
        Assert.Null(await store.GetLastSuccessAsync("never-run", default));
    }

    [SkippableFact]
    public async Task GetLastRunReturnsTheNewestWhateverItsOutcome()
    {
        var store = await CreateAsync();

        var older = await StartAndComplete(store, "job", Origin, JobRunResult.Success(TimeSpan.Zero, Origin));
        var newer = await StartAndComplete(
            store, "job", Origin.AddMinutes(5),
            JobRunResult.Failed(TimeSpan.Zero, Origin.AddMinutes(5), new InvalidOperationException()));

        var run = await store.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(newer, run.RunId);
        Assert.NotEqual(older, run.RunId);
    }

    [SkippableFact]
    public async Task GetLastSuccessSkipsOverLaterFailures()
    {
        var store = await CreateAsync();

        var success = await StartAndComplete(
            store, "job", Origin, JobRunResult.Success(TimeSpan.Zero, Origin));

        await StartAndComplete(
            store, "job", Origin.AddMinutes(5),
            JobRunResult.Failed(TimeSpan.Zero, Origin.AddMinutes(5), new InvalidOperationException()));

        var run = await store.GetLastSuccessAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(success, run.RunId);
    }

    [SkippableFact]
    public async Task ProgressEntriesReadBackInOrder()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        await store.StartAsync(Start(runId, "job", Origin), default);

        await store.AppendLogAsync(
            runId, new JobLogEntry { Timestamp = Origin.AddSeconds(1), Message = "first" }, default);

        await store.AppendLogAsync(
            runId,
            new JobLogEntry
            {
                Timestamp = Origin.AddSeconds(2),
                Message = "second",
                Data = new Dictionary<string, object?> { ["count"] = 7 },
            },
            default);

        await SettleAsync(store);

        var run = await store.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(2, run.Log.Count);
        Assert.Equal("first", run.Log[0].Message);
        Assert.Equal("second", run.Log[1].Message);
        Assert.Equal(Origin.AddSeconds(1), run.Log[0].Timestamp);
    }

    [SkippableFact]
    public async Task ConsecutiveFailuresCountBackToTheLastSuccess()
    {
        var store = await CreateAsync();

        await StartAndComplete(store, "job", Origin, Failure(Origin));
        await StartAndComplete(store, "job", Origin.AddMinutes(1), JobRunResult.Success(TimeSpan.Zero, Origin.AddMinutes(1)));
        await StartAndComplete(store, "job", Origin.AddMinutes(2), Failure(Origin.AddMinutes(2)));
        await StartAndComplete(store, "job", Origin.AddMinutes(3), Failure(Origin.AddMinutes(3)));

        Assert.Equal(2, await store.CountConsecutiveFailuresAsync("job", default));
    }

    [SkippableFact]
    public async Task ASuccessResetsTheFailureStreakToZero()
    {
        var store = await CreateAsync();

        await StartAndComplete(store, "job", Origin, Failure(Origin));
        await StartAndComplete(
            store, "job", Origin.AddMinutes(1), JobRunResult.Success(TimeSpan.Zero, Origin.AddMinutes(1)));

        Assert.Equal(0, await store.CountConsecutiveFailuresAsync("job", default));
    }

    [SkippableFact]
    public async Task AJobWithNoHistoryHasNoFailureStreak()
    {
        var store = await CreateAsync();

        Assert.Equal(0, await store.CountConsecutiveFailuresAsync("never-run", default));
    }

    [SkippableTheory]
    [InlineData(RunStatus.TimedOut)]
    [InlineData(RunStatus.Lost)]
    public async Task TimedOutAndLostRunsExtendTheFailureStreak(RunStatus status)
    {
        // Both mean the work did not get done. A threshold set on "failures" that ignored them would
        // stay silent through exactly the outages people most want to hear about.
        var store = await CreateAsync();

        await StartAndComplete(store, "job", Origin, Failure(Origin));
        await StartAndComplete(
            store,
            "job",
            Origin.AddMinutes(1),
            new JobRunResult
            {
                Status = status,
                Duration = TimeSpan.Zero,
                CompletedAt = Origin.AddMinutes(1),
            });

        Assert.Equal(2, await store.CountConsecutiveFailuresAsync("job", default));
    }

    [SkippableTheory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Skipped)]
    [InlineData(RunStatus.Aborted)]
    public async Task RunningSkippedAndAbortedRunsNeitherExtendNorBreakTheStreak(RunStatus status)
    {
        // None of them says anything about whether the job's own logic is broken: one has not
        // finished, one never started, and one was cut short by a deployment.
        var store = await CreateAsync();

        await StartAndComplete(store, "job", Origin, Failure(Origin));

        var neutral = Guid.NewGuid();
        await store.StartAsync(Start(neutral, "job", Origin.AddMinutes(1)), default);

        if (status != RunStatus.Running)
        {
            await store.CompleteAsync(
                neutral,
                new JobRunResult
                {
                    Status = status,
                    Duration = TimeSpan.Zero,
                    CompletedAt = Origin.AddMinutes(1),
                },
                default);
        }

        await StartAndComplete(store, "job", Origin.AddMinutes(2), Failure(Origin.AddMinutes(2)));

        Assert.Equal(2, await store.CountConsecutiveFailuresAsync("job", default));
    }

    [SkippableFact]
    public async Task QueryReturnsNewestFirst()
    {
        var store = await CreateAsync();

        await StartAndComplete(store, "job", Origin, JobRunResult.Success(TimeSpan.Zero, Origin));
        var newest = await StartAndComplete(
            store, "job", Origin.AddMinutes(1), JobRunResult.Success(TimeSpan.Zero, Origin.AddMinutes(1)));

        var results = await store.QueryAsync(new RunQuery { JobName = "job" }, default);

        Assert.Equal(2, results.Count);
        Assert.Equal(newest, results[0].RunId);
    }

    [SkippableFact]
    public async Task QueryFiltersByJob()
    {
        var store = await CreateAsync();

        await store.StartAsync(Start(Guid.NewGuid(), "a", Origin), default);
        await store.StartAsync(Start(Guid.NewGuid(), "b", Origin), default);

        var results = await store.QueryAsync(new RunQuery { JobName = "a" }, default);

        Assert.Equal("a", Assert.Single(results).JobName);
    }

    [SkippableFact]
    public async Task QueryFiltersByStatus()
    {
        var store = await CreateAsync();

        await StartAndComplete(store, "job", Origin, JobRunResult.Success(TimeSpan.Zero, Origin));
        var failed = await StartAndComplete(store, "job", Origin.AddMinutes(1), Failure(Origin.AddMinutes(1)));

        var results = await store.QueryAsync(
            new RunQuery { JobName = "job", Statuses = [RunStatus.Failed] }, default);

        Assert.Equal(failed, Assert.Single(results).RunId);
    }

    [SkippableFact]
    public async Task QueryFiltersByStartWindowInclusiveOfFromAndExclusiveOfTo()
    {
        var store = await CreateAsync();

        await store.StartAsync(Start(Guid.NewGuid(), "job", Origin), default);
        var inside = Guid.NewGuid();
        await store.StartAsync(Start(inside, "job", Origin.AddMinutes(5)), default);
        await store.StartAsync(Start(Guid.NewGuid(), "job", Origin.AddMinutes(10)), default);

        var results = await store.QueryAsync(
            new RunQuery { JobName = "job", From = Origin.AddMinutes(5), To = Origin.AddMinutes(10) },
            default);

        Assert.Equal(inside, Assert.Single(results).RunId);
    }

    [SkippableFact]
    public async Task QueryFiltersByInstance()
    {
        var store = await CreateAsync();

        await store.StartAsync(Start(Guid.NewGuid(), "job", Origin) with { InstanceId = "one" }, default);
        var wanted = Guid.NewGuid();
        await store.StartAsync(Start(wanted, "job", Origin) with { InstanceId = "two" }, default);

        var results = await store.QueryAsync(new RunQuery { InstanceId = "two" }, default);

        Assert.Equal(wanted, Assert.Single(results).RunId);
    }

    [SkippableFact]
    public async Task QueryWithNoJobReturnsEveryJob()
    {
        var store = await CreateAsync();

        await store.StartAsync(Start(Guid.NewGuid(), "a", Origin), default);
        await store.StartAsync(Start(Guid.NewGuid(), "b", Origin.AddMinutes(1)), default);

        var results = await store.QueryAsync(new RunQuery(), default);

        Assert.Equal(2, results.Count);
    }

    [SkippableFact]
    public async Task QueryPagesWithLimitAndOffset()
    {
        var store = await CreateAsync();

        for (var i = 0; i < 5; i++)
        {
            await store.StartAsync(Start(Guid.NewGuid(), "job", Origin.AddMinutes(i)), default);
        }

        var first = await store.QueryAsync(new RunQuery { JobName = "job", Limit = 2 }, default);
        var second = await store.QueryAsync(new RunQuery { JobName = "job", Limit = 2, Offset = 2 }, default);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Empty(first.Select(r => r.RunId).Intersect(second.Select(r => r.RunId)));

        // Newest first, so paging walks backwards through time without repeating or skipping.
        Assert.Equal(Origin.AddMinutes(4), first[0].StartedAt);
        Assert.Equal(Origin.AddMinutes(2), second[0].StartedAt);
    }

    [SkippableFact]
    public async Task PurgeRemovesFinishedRunsOlderThanTheCutOff()
    {
        var store = await CreateAsync();

        await StartAndComplete(store, "job", Origin, JobRunResult.Success(TimeSpan.Zero, Origin));
        var kept = await StartAndComplete(
            store, "job", Origin.AddHours(2), JobRunResult.Success(TimeSpan.Zero, Origin.AddHours(2)));

        await store.PurgeAsync(Origin.AddHours(1), default);

        var results = await store.QueryAsync(new RunQuery { JobName = "job" }, default);

        Assert.Equal(kept, Assert.Single(results).RunId);
    }

    [SkippableFact]
    public async Task PurgeLeavesARunningRunAloneHoweverOldItIs()
    {
        // Deleting it would hide an abandoned run rather than surface it. Resolving that case is the
        // janitor's job, and it needs the row to still be there to do it.
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        await store.StartAsync(Start(runId, "job", Origin), default);
        await store.PurgeAsync(Origin.AddYears(1), default);

        var run = await store.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(runId, run.RunId);
    }

    [SkippableFact]
    public async Task ARunIsRetrievableById()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        await store.StartAsync(Start(runId, "job", Origin), default);
        await store.AppendLogAsync(
            runId,
            new JobLogEntry { Timestamp = Origin.AddSeconds(1), Message = "halfway" },
            default);
        await SettleAsync(store);

        var run = await store.GetAsync(runId, default);

        Assert.NotNull(run);
        Assert.Equal(runId, run.RunId);
        Assert.Equal("job", run.JobName);
        Assert.Equal("halfway", Assert.Single(run.Log).Message);
    }

    [SkippableFact]
    public async Task AnUnknownRunIdReadsBackAsNull()
    {
        var store = await CreateAsync();

        Assert.Null(await store.GetAsync(Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task ExcludingTheLogLeavesEveryOtherFieldIntact()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        await store.StartAsync(Start(runId, "job", Origin), default);
        await store.AppendLogAsync(
            runId,
            new JobLogEntry { Timestamp = Origin, Message = "noise" },
            default);
        await SettleAsync(store);

        var withLog = Assert.Single(await store.QueryAsync(new RunQuery { JobName = "job" }, default));
        var without = Assert.Single(
            await store.QueryAsync(new RunQuery { JobName = "job", IncludeLog = false }, default));

        Assert.Single(withLog.Log);
        Assert.Empty(without.Log);
        Assert.Equal(withLog.RunId, without.RunId);
        Assert.Equal(withLog.JobName, without.JobName);
        Assert.Equal(withLog.ScheduledFor, without.ScheduledFor);
        Assert.Equal(withLog.Status, without.Status);
        Assert.Equal(withLog.Trigger, without.Trigger);
        Assert.Equal(withLog.InstanceId, without.InstanceId);
        Assert.Equal(withLog.StartedAt, without.StartedAt);
        Assert.Equal(withLog.CompletedAt, without.CompletedAt);
        Assert.Equal(withLog.Duration, without.Duration);
        Assert.Equal(withLog.Error, without.Error);
    }

    /// <summary>Builds a start record with the fields most tests do not care about filled in.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="jobName">The job name.</param>
    /// <param name="startedAt">When the run began.</param>
    // ---- Exclusive keys: the cluster-wide half of OverlapPolicy.Skip. ----

    [SkippableFact]
    public async Task AnExclusiveKeyIsHeldByOneRunAtATime()
    {
        var store = await CreateAsync();

        var first = await store.StartAsync(
            Start(Guid.NewGuid(), "job", Origin) with { ExclusiveKey = "job" }, default);

        var second = await store.StartAsync(
            Start(Guid.NewGuid(), "job", Origin.AddSeconds(1)) with { ExclusiveKey = "job" }, default);

        Assert.NotNull(first);

        // Null, not an exception: another instance running the job is an expected answer, and the
        // caller records a skip rather than a failure.
        Assert.Null(second);
    }

    [SkippableFact]
    public async Task AStartCarryingNoKeyIsNeverRefused()
    {
        var store = await CreateAsync();

        await store.StartAsync(Start(Guid.NewGuid(), "job", Origin) with { ExclusiveKey = "job" }, default);

        // AllowConcurrent has to stay concurrent even while a Skip run of the same job holds the key.
        var concurrent = await store.StartAsync(Start(Guid.NewGuid(), "job", Origin.AddSeconds(1)), default);

        Assert.NotNull(concurrent);
    }

    [SkippableFact]
    public async Task DifferentKeysDoNotBlockEachOther()
    {
        var store = await CreateAsync();

        var a = await store.StartAsync(
            Start(Guid.NewGuid(), "a", Origin) with { ExclusiveKey = "a" }, default);

        var b = await store.StartAsync(
            Start(Guid.NewGuid(), "b", Origin) with { ExclusiveKey = "b" }, default);

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [SkippableFact]
    public async Task TheKeyIsReleasedByTheOutcomeWrite()
    {
        var store = await CreateAsync();
        var first = Guid.NewGuid();

        await store.StartAsync(Start(first, "job", Origin) with { ExclusiveKey = "job" }, default);
        await store.CompleteAsync(first, JobRunResult.Success(TimeSpan.Zero, Origin.AddSeconds(1)), default);

        var next = await store.StartAsync(
            Start(Guid.NewGuid(), "job", Origin.AddSeconds(2)) with { ExclusiveKey = "job" }, default);

        Assert.NotNull(next);
    }

    [SkippableFact]
    public async Task TheKeyIsReleasedEvenWhenTheRunFailed()
    {
        var store = await CreateAsync();
        var first = Guid.NewGuid();

        await store.StartAsync(Start(first, "job", Origin) with { ExclusiveKey = "job" }, default);
        await store.CompleteAsync(first, Failure(Origin.AddSeconds(1)), default);

        // A job that throws must not lock itself out until a heartbeat timeout: the outcome was
        // recorded, so the key is free.
        var next = await store.StartAsync(
            Start(Guid.NewGuid(), "job", Origin.AddSeconds(2)) with { ExclusiveKey = "job" }, default);

        Assert.NotNull(next);
    }

    [SkippableFact]
    public async Task ARunDoesNotBlockItselfWhenStartedTwice()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        // The claimed-then-started path: the coordinator writes the row, and the executor starts
        // the same run id a moment later. Blocking on its own key would deadlock every scheduled
        // run of every Skip job.
        await store.StartAsync(Start(runId, "job", Origin) with { ExclusiveKey = "job" }, default);

        var again = await store.StartAsync(
            Start(runId, "job", Origin.AddMilliseconds(5)) with { ExclusiveKey = "job" }, default);

        Assert.NotNull(again);
    }

    protected static JobRunStart Start(Guid runId, string jobName, DateTimeOffset startedAt) => new()
    {
        RunId = runId,
        JobName = jobName,
        Trigger = TriggerKind.Schedule,
        InstanceId = "conformance:1",
        StartedAt = startedAt,
    };

    private static JobRunResult Failure(DateTimeOffset completedAt)
        => JobRunResult.Failed(TimeSpan.Zero, completedAt, new InvalidOperationException("expected"));

    private static async Task<Guid> StartAndComplete(
        IRunHistoryStore store,
        string jobName,
        DateTimeOffset startedAt,
        JobRunResult result)
    {
        var runId = Guid.NewGuid();

        await store.StartAsync(Start(runId, jobName, startedAt), default);
        await store.CompleteAsync(runId, result, default);

        return runId;
    }
}
