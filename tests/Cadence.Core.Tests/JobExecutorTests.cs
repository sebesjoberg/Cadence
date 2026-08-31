using System.Diagnostics.Metrics;
using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Scheduling;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Core.Tests;

public class JobExecutorTests
{
    private static readonly RunSettings Default = new() { Overlap = OverlapPolicy.Skip };

    [Fact]
    public async Task ASuccessfulRunIsRecordedAsSucceeded()
    {
        await using var fixture = JobExecutorFixture.Create();

        var result = await fixture.DispatchAsync<SucceedingJob>(Default);
        await fixture.Executor.WaitForIdleAsync();

        Assert.True(result.WasStarted);

        var run = await fixture.History.GetLastRunAsync("job", CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, run!.Status);
        Assert.Equal(result.RunId, run.RunId);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public async Task AThrowingJobIsRecordedAsFailedWithTheException()
    {
        await using var fixture = JobExecutorFixture.Create();

        await fixture.DispatchAsync<FailingJob>(Default);
        await fixture.Executor.WaitForIdleAsync();

        var run = await fixture.History.GetLastRunAsync("job", CancellationToken.None);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Contains("the invoice service is unreachable", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceedingTheMaximumDurationIsRecordedAsTimedOutNotAborted()
    {
        await using var fixture = JobExecutorFixture.Create();

        await fixture.DispatchAsync<NeverEndingJob>(
            new RunSettings { Overlap = OverlapPolicy.Skip, MaxDuration = TimeSpan.FromMilliseconds(50) });

        await fixture.Executor.WaitForIdleAsync();

        var run = await fixture.History.GetLastRunAsync("job", CancellationToken.None);

        // The distinction matters: TimedOut says the job is slow, Aborted says the host is churning.
        Assert.Equal(RunStatus.TimedOut, run!.Status);
    }

    [Fact]
    public async Task ShutdownRecordsAnInFlightRunAsAborted()
    {
        await using var fixture = JobExecutorFixture.Create();

        await fixture.DispatchAsync<NeverEndingJob>(Default);
        await fixture.Executor.DrainAsync(TimeSpan.FromSeconds(5));

        var run = await fixture.History.GetLastRunAsync("job", CancellationToken.None);
        Assert.Equal(RunStatus.Aborted, run!.Status);
    }

    [Fact]
    public async Task SkipPolicyRecordsTheSecondOccurrenceAsSkippedWithAReason()
    {
        await using var fixture = JobExecutorFixture.Create();

        var first = await fixture.DispatchAsync<GatedJob>(Default);
        var second = await fixture.DispatchAsync<GatedJob>(Default);

        Assert.True(first.WasStarted);
        Assert.False(second.WasStarted);
        Assert.Contains("overlap policy is Skip", second.SkipReason, StringComparison.Ordinal);

        fixture.Spy.Gate.SetResult();
        await fixture.Executor.WaitForIdleAsync();

        var runs = await fixture.History.QueryAsync(new RunQuery { JobName = "job" }, CancellationToken.None);
        var skipped = runs.Single(r => r.Status == RunStatus.Skipped);

        // The reason is stored, not just logged: a gap in the schedule has to be explainable.
        Assert.Contains("already in flight", Assert.Single(skipped.Log).Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Spy.Started);
    }

    [Fact]
    public async Task AllowConcurrentStartsASecondRunInAScopeOfItsOwn()
    {
        await using var fixture = JobExecutorFixture.Create();
        var settings = new RunSettings { Overlap = OverlapPolicy.AllowConcurrent };

        await fixture.DispatchAsync<GatedJob>(settings);
        await fixture.DispatchAsync<GatedJob>(settings);

        // Both are gated, so both are provably in flight at the same moment.
        Assert.Equal(2, fixture.Executor.ActiveRunCount);
        Assert.Equal(2, fixture.Executor.InFlightCount("job"));

        fixture.Spy.Gate.SetResult();
        await fixture.Executor.WaitForIdleAsync();

        // Concurrent runs of the same job do NOT share scoped state. Counter-intuitive, and exactly
        // why it is worth pinning down.
        Assert.Equal(2, fixture.Spy.ObservedScopeIds.Distinct().Count());
    }

    [Fact]
    public async Task ThePerInstanceConcurrencyCapSkipsRatherThanQueues()
    {
        await using var fixture = JobExecutorFixture.Create(options => options.MaxConcurrentRuns = 1);
        var settings = new RunSettings { Overlap = OverlapPolicy.AllowConcurrent };

        await fixture.DispatchAsync<GatedJob>(settings);
        var blocked = await fixture.DispatchAsync<GatedJob>(settings);

        Assert.False(blocked.WasStarted);
        Assert.Contains("concurrency limit", blocked.SkipReason, StringComparison.Ordinal);
        Assert.Contains("MaxConcurrentRuns", blocked.SkipReason, StringComparison.Ordinal);

        fixture.Spy.Gate.SetResult();
        await fixture.Executor.WaitForIdleAsync();
    }

    [Fact]
    public async Task CapacityIsReleasedWhenARunFinishes()
    {
        await using var fixture = JobExecutorFixture.Create(options => options.MaxConcurrentRuns = 1);

        await fixture.DispatchAsync<SucceedingJob>(Default);
        await fixture.Executor.WaitForIdleAsync();

        var second = await fixture.DispatchAsync<SucceedingJob>(Default);
        await fixture.Executor.WaitForIdleAsync();

        Assert.True(second.WasStarted);
        Assert.Equal(0, fixture.Executor.ActiveRunCount);
    }

    [Fact]
    public async Task ReportedProgressLandsInRunHistory()
    {
        await using var fixture = JobExecutorFixture.Create();

        var result = await fixture.DispatchAsync<ReportingJob>(Default);
        await fixture.Executor.WaitForIdleAsync();

        // The sink writes without blocking the job, so give the append a moment to land.
        JobRun? run = null;
        for (var attempt = 0; attempt < 50 && (run?.Log.Count ?? 0) == 0; attempt++)
        {
            run = await fixture.History.GetLastRunAsync("job", CancellationToken.None);
            if (run!.Log.Count == 0)
            {
                await Task.Delay(10);
            }
        }

        Assert.Equal(result.RunId, run!.RunId);
        var entry = Assert.Single(run.Log);
        Assert.Equal("processed 400 of 12000", entry.Message);
        Assert.Equal(400, Assert.IsType<int>(entry.Data!["done"]));
    }
}
