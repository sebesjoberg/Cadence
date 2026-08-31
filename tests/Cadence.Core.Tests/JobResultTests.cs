using System.Text;
using System.Text.Json;
using Cadence.Scheduling;
using Cadence.Storage;
using Xunit;

namespace Cadence.Core.Tests;

public class JobResultTests
{
    private static readonly RunSettings Default = new() { Overlap = OverlapPolicy.Skip };

    [Fact]
    public async Task AJobReturningBytesHasThemStoredVerbatim()
    {
        await using var fixture = JobExecutorFixture.Create();

        var result = await fixture.DispatchAsync<FileResultJob>(
            Default, JobExecutorFixture.Payload(new { customer = "Contoso", rows = 3 }));

        await fixture.Executor.WaitForIdleAsync();

        await using var stored = await fixture.Results.OpenAsync(result.RunId!.Value, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("text/csv; charset=utf-8", stored.Info.ContentType);
        Assert.Equal("report.csv", stored.Info.FileName);

        using var reader = new StreamReader(stored.Content, Encoding.UTF8);
        Assert.Equal("customer,rows\nContoso,3\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task APlainReturnTypeIsStoredAsJson()
    {
        await using var fixture = JobExecutorFixture.Create();

        var result = await fixture.DispatchAsync<PocoResultJob>(
            Default, JobExecutorFixture.Payload(new { customer = "Fabrikam", rows = 12 }));

        await fixture.Executor.WaitForIdleAsync();

        await using var stored = await fixture.Results.OpenAsync(result.RunId!.Value, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("application/json; charset=utf-8", stored.Info.ContentType);
        Assert.Null(stored.Info.FileName);

        var summary = await JsonSerializer.DeserializeAsync<ReportSummary>(
            stored.Content, JsonSerializerOptions.Web);

        Assert.Equal(new ReportSummary("Fabrikam", 12), summary);
    }

    [Fact]
    public async Task ARunWithNoPayloadBindsTheDefaultRequest()
    {
        await using var fixture = JobExecutorFixture.Create();

        // What every cron occurrence of a result job looks like: nothing supplied a request.
        var result = await fixture.DispatchAsync<FileResultJob>(Default);

        await fixture.Executor.WaitForIdleAsync();

        await using var stored = await fixture.Results.OpenAsync(result.RunId!.Value, CancellationToken.None);

        Assert.NotNull(stored);

        using var reader = new StreamReader(stored.Content, Encoding.UTF8);
        Assert.Equal("customer,rows\n(scheduled),0\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ANullResultStoresNothingAndStillSucceeds()
    {
        await using var fixture = JobExecutorFixture.Create();

        var result = await fixture.DispatchAsync<NullResultJob>(
            Default, JobExecutorFixture.Payload(new { customer = "Contoso", rows = 1 }));

        await fixture.Executor.WaitForIdleAsync();

        var run = await fixture.History.GetAsync(result.RunId!.Value, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run!.Status);
        Assert.Null(await fixture.Results.DescribeAsync(result.RunId.Value, CancellationToken.None));
    }

    [Fact]
    public async Task AResultOverTheCeilingFailsTheRunAndStoresNothing()
    {
        await using var fixture = JobExecutorFixture.Create(options => options.MaxResultBytes = 1024);

        var result = await fixture.DispatchAsync<OversizedResultJob>(
            Default, JobExecutorFixture.Payload(new { customer = "Contoso", rows = 1 }));

        await fixture.Executor.WaitForIdleAsync();

        var run = await fixture.History.GetAsync(result.RunId!.Value, CancellationToken.None);

        // Failing loudly rather than storing a truncated result: a caller collecting half a
        // spreadsheet has no way to tell that is what happened.
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Contains("MaxResultBytes", run.Error, StringComparison.Ordinal);
        Assert.Null(await fixture.Results.DescribeAsync(result.RunId.Value, CancellationToken.None));
    }

    [Fact]
    public async Task AResultIsStoredWithAnExpiryTakenFromRetention()
    {
        await using var fixture = JobExecutorFixture.Create(
            options => options.Retention = new RetentionOptions { ResultMaxAge = TimeSpan.FromHours(2) });

        var result = await fixture.DispatchAsync<FileResultJob>(Default);
        await fixture.Executor.WaitForIdleAsync();

        var info = await fixture.Results.DescribeAsync(result.RunId!.Value, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(fixture.Now + TimeSpan.FromHours(2), info.ExpiresAt);
    }

    [Fact]
    public async Task AResultJobCalledThroughIJobRunsAndDiscardsItsResult()
    {
        // IResultJob extends IJob so substituting one is safe. Anything holding only the base
        // interface must still get a complete run out of it.
        static Task ThroughBaseInterface(IJob job, JobContext context)
            => job.ExecuteAsync(context, CancellationToken.None);

        var context = new JobContext(new NullProgressSink())
        {
            JobName = "report",
            RunId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UnixEpoch,
            Trigger = TriggerKind.Manual,
            InstanceId = "test:1:aaaaaaaa",
            Payload = JobExecutorFixture.Payload(new { customer = "Contoso", rows = 3 }),
        };

        await ThroughBaseInterface(new FileResultJob(), context);
    }

    [Fact]
    public async Task AJobImplementingTheResultInterfaceTwiceFailsTheRun()
    {
        await using var fixture = JobExecutorFixture.Create();

        var result = await fixture.DispatchAsync<AmbiguousResultJob>(Default);
        await fixture.Executor.WaitForIdleAsync();

        var run = await fixture.History.GetAsync(result.RunId!.Value, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Contains("implements IResultJob<,> 2 times", run.Error, StringComparison.Ordinal);
    }

    private sealed class NullProgressSink : IJobProgressSink
    {
        public void Report(Guid runId, string message, IReadOnlyDictionary<string, object?>? data)
        {
        }
    }
}
