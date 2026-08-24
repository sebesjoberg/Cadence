using System.Collections.Concurrent;

namespace Cadence.Core.Tests;

/// <summary>Records what each job did, shared across runs so tests can assert on it.</summary>
internal sealed class JobSpy
{
    public ConcurrentBag<Guid> CompletedRunIds { get; } = [];

    public ConcurrentBag<Guid> ObservedScopeIds { get; } = [];

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Started;
}

/// <summary>A scoped dependency, so tests can prove each run gets its own scope.</summary>
internal sealed class ScopeMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

internal sealed class SucceedingJob(JobSpy spy, ScopeMarker marker) : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref spy.Started);
        spy.ObservedScopeIds.Add(marker.Id);
        spy.CompletedRunIds.Add(context.RunId);
        return Task.CompletedTask;
    }
}

internal sealed class FailingJob : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException("the invoice service is unreachable");
}

/// <summary>Waits on a gate the test opens, so overlap and concurrency can be tested deterministically.</summary>
internal sealed class GatedJob(JobSpy spy, ScopeMarker marker) : IJob
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref spy.Started);
        spy.ObservedScopeIds.Add(marker.Id);

        await spy.Gate.Task.WaitAsync(cancellationToken);

        spy.CompletedRunIds.Add(context.RunId);
    }
}

/// <summary>Observes its token, so a maximum duration produces a clean timeout.</summary>
internal sealed class NeverEndingJob : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        => Task.Delay(Timeout.Infinite, cancellationToken);
}

/// <summary>Reports progress, so the sink can be asserted on.</summary>
internal sealed class ReportingJob : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        context.Report("processed 400 of 12000", new Dictionary<string, object?> { ["done"] = 400 });
        return Task.CompletedTask;
    }
}

/// <summary>Takes a dependency nobody registered, so the boot probe has something to catch.</summary>
internal sealed class UnresolvableJob(IDisposable missing) : IJob
{
    private readonly IDisposable _missing = missing;

    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        _missing.Dispose();
        return Task.CompletedTask;
    }
}

[ScheduledJob(
    Name = "attributed-job",
    Cron = "0 */15 * * * *",
    TimeZone = "Europe/Stockholm",
    Overlap = OverlapPolicy.AllowConcurrent,
    MaxDuration = "00:10:00",
    Triggers = TriggerKind.Schedule | TriggerKind.Api)]
internal sealed class AttributedJob : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
