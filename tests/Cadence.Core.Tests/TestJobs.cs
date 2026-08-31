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

/// <summary>What a result job is asked for.</summary>
internal sealed record ReportRequest(string Customer, int Rows);

/// <summary>What a JSON-returning result job hands back.</summary>
internal sealed record ReportSummary(string Customer, int Rows);

/// <summary>Returns bytes directly, which is the shortest path for a file-producing job.</summary>
internal sealed class FileResultJob : IResultJob<ReportRequest, JobResult>
{
    public Task<JobResult> ExecuteAsync(
        ReportRequest request,
        JobContext context,
        CancellationToken cancellationToken)
    {
        var customer = request?.Customer ?? "(scheduled)";
        return Task.FromResult(JobResult.Csv($"customer,rows\n{customer},{request?.Rows ?? 0}\n", "report.csv"));
    }
}

/// <summary>Returns a plain type, so the default JSON serializer has to turn it into bytes.</summary>
internal sealed class PocoResultJob : IResultJob<ReportRequest, ReportSummary>
{
    public Task<ReportSummary> ExecuteAsync(
        ReportRequest request,
        JobContext context,
        CancellationToken cancellationToken)
        => Task.FromResult(new ReportSummary(request.Customer, request.Rows));
}

/// <summary>Produces more than any sane ceiling allows.</summary>
internal sealed class OversizedResultJob : IResultJob<ReportRequest, JobResult>
{
    public Task<JobResult> ExecuteAsync(
        ReportRequest request,
        JobContext context,
        CancellationToken cancellationToken)
        => Task.FromResult(JobResult.Bytes(new byte[4096], "application/octet-stream"));
}

/// <summary>Returns null, which records a run that produced nothing to collect.</summary>
internal sealed class NullResultJob : IResultJob<ReportRequest, ReportSummary?>
{
    public Task<ReportSummary?> ExecuteAsync(
        ReportRequest request,
        JobContext context,
        CancellationToken cancellationToken)
        => Task.FromResult<ReportSummary?>(null);
}

/// <summary>
/// Implements the result interface twice, so which result a run produces has no answer. Only
/// reachable by declaring <see cref="IJob.ExecuteAsync"/> as well: without it the compiler rejects
/// the type outright, because the two inherited default implementations are ambiguous.
/// </summary>
internal sealed class AmbiguousResultJob
    : IResultJob<ReportRequest, JobResult>, IResultJob<string, string>
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<JobResult> ExecuteAsync(
        ReportRequest request, JobContext context, CancellationToken cancellationToken)
        => Task.FromResult(JobResult.Text("csv"));

    public Task<string> ExecuteAsync(
        string request, JobContext context, CancellationToken cancellationToken)
        => Task.FromResult("text");
}
