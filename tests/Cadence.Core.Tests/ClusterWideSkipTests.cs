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

/// <summary>
/// <see cref="OverlapPolicy.Skip"/> across two instances sharing one store.
/// </summary>
/// <remarks>
/// The per-instance gate in <see cref="JobExecutor"/> cannot see a run on another replica, so this
/// is the only place the cluster-wide half of the policy is actually exercised: two executors, one
/// history store, and no in-process state between them.
/// </remarks>
public class ClusterWideSkipTests : IAsyncDisposable
{
    private static readonly RunSettings SkipPolicy = new() { Overlap = OverlapPolicy.Skip };
    private static readonly RunSettings Concurrent = new() { Overlap = OverlapPolicy.AllowConcurrent };

    private static readonly JobDescriptor Gated =
        new() { Name = "job", ImplementationType = typeof(GatedJob) };

    private readonly InMemoryRunHistoryStore _history = new();
    private readonly JobSpy _spy = new();
    private readonly ServiceProvider _provider;
    private readonly JobExecutor _alpha;
    private readonly JobExecutor _beta;

    public ClusterWideSkipTests()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton(_spy);
        services.AddScoped<ScopeMarker>();
        services.AddTransient<GatedJob>();
        services.AddTransient<SucceedingJob>();

        _provider = services.BuildServiceProvider();

        _alpha = Build("alpha:1:aaaaaaaa");
        _beta = Build("beta:1:bbbbbbbb");
    }

    [Fact]
    public async Task ASecondInstanceIsRefusedWhileTheFirstIsRunning()
    {
        var first = await Dispatch(_alpha, SkipPolicy);
        var second = await Dispatch(_beta, SkipPolicy);

        Assert.True(first.WasStarted);

        // Before this, beta had no way to know alpha was running and would have started a second
        // run of the same job -- the caveat the README used to carry.
        Assert.False(second.WasStarted);
        Assert.Contains("another instance", second.SkipReason, StringComparison.Ordinal);

        _spy.Gate.SetResult();
        await _alpha.WaitForIdleAsync();

        Assert.Equal(1, _spy.Started);
    }

    [Fact]
    public async Task TheRefusalIsRecordedAsASkippedRunWithItsReason()
    {
        await Dispatch(_alpha, SkipPolicy);
        await Dispatch(_beta, SkipPolicy);

        // Queried rather than looked up by id: a skipped DispatchResult carries only the reason,
        // the same as one refused by the in-instance gate.
        var runs = await _history.QueryAsync(new RunQuery { JobName = "job" }, CancellationToken.None);
        var skipped = Assert.Single(runs, r => r.Status == RunStatus.Skipped);

        // A gap in the schedule has to be explainable, and a refusal on another instance is no
        // less of a gap than one this instance made.
        Assert.Equal("beta:1:bbbbbbbb", skipped.InstanceId);
        Assert.Contains("another instance", Assert.Single(skipped.Log).Message, StringComparison.Ordinal);

        _spy.Gate.SetResult();
        await _alpha.WaitForIdleAsync();
    }

    [Fact]
    public async Task TheSecondInstanceRunsOnceTheFirstHasFinished()
    {
        await Dispatch(_alpha, SkipPolicy);

        _spy.Gate.SetResult();
        await _alpha.WaitForIdleAsync();

        var second = await Dispatch(_beta, SkipPolicy);

        Assert.True(second.WasStarted);
        await _beta.WaitForIdleAsync();
    }

    [Fact]
    public async Task AllowConcurrentStillRunsOnBothInstances()
    {
        var first = await Dispatch(_alpha, Concurrent);
        var second = await Dispatch(_beta, Concurrent);

        // The key is only taken for Skip, so nothing about this policy changed.
        Assert.True(first.WasStarted);
        Assert.True(second.WasStarted);

        _spy.Gate.SetResult();
        await _alpha.WaitForIdleAsync();
        await _beta.WaitForIdleAsync();

        Assert.Equal(2, _spy.Started);
    }

    [Fact]
    public async Task AFailedRunOnOneInstanceDoesNotBlockTheOther()
    {
        var failing = new JobDescriptor { Name = "job", ImplementationType = typeof(SucceedingJob) };

        await _alpha.DispatchAsync(
            failing, SkipPolicy, scheduledFor: null, TriggerKind.Manual, payload: null, CancellationToken.None);

        await _alpha.WaitForIdleAsync();

        var second = await _beta.DispatchAsync(
            failing, SkipPolicy, scheduledFor: null, TriggerKind.Manual, payload: null, CancellationToken.None);

        // The outcome write releases the key, so a finished run never leaves the job locked out.
        Assert.True(second.WasStarted);
        await _beta.WaitForIdleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _spy.Gate.TrySetResult();

        await _alpha.DisposeAsync();
        await _beta.DisposeAsync();
        await _provider.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    private static Task<DispatchResult> Dispatch(JobExecutor executor, RunSettings settings)
        => executor.DispatchAsync(
            Gated, settings, scheduledFor: null, TriggerKind.Manual, payload: null, CancellationToken.None);

    private JobExecutor Build(string instanceId)
    {
        var clock = new FakeClock(Occurrences.Utc(2026, 8, 24, 2, 0));

        return new JobExecutor(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _history,
            new InMemoryJobResultStore(),
            new RunHistoryProgressSink(_history, clock, NullLogger<RunHistoryProgressSink>.Instance),
            clock,
            new CadenceMetrics(_provider.GetRequiredService<IMeterFactory>()),
            Options.Create(new CadenceOptions { InstanceId = instanceId }),
            NullLogger<JobExecutor>.Instance);
    }
}
