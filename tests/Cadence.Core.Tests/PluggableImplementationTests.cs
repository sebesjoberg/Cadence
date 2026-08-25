using Cadence.DependencyInjection;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Core.Tests;

/// <summary>
/// Every storage seam has to be replaceable from the builder without the caller knowing which
/// registrations Cadence adds internally, or in what order.
/// </summary>
public class PluggableImplementationTests
{
    [Fact]
    public void ACustomCoordinatorReplacesTheNoOpDefault()
    {
        var resolved = Resolve<IOccurrenceCoordinator>(
            cadence => cadence.UseCoordinator<CustomCoordinator>());

        Assert.IsType<CustomCoordinator>(resolved);
    }

    [Fact]
    public void ACoordinatorInstanceCanBeSuppliedDirectly()
    {
        var mine = new ScriptedCoordinator(grantAll: false);

        var resolved = Resolve<IOccurrenceCoordinator>(cadence => cadence.UseCoordinator(mine));

        Assert.Same(mine, resolved);
    }

    [Fact]
    public void ACustomScheduleSourceReplacesTheCodeDefault()
    {
        var resolved = Resolve<IScheduleSource>(cadence => cadence.UseScheduleSource<MutableScheduleSource>());

        Assert.IsType<MutableScheduleSource>(resolved);
    }

    [Fact]
    public void ACustomHistoryStoreReplacesTheInMemoryDefault()
    {
        var resolved = Resolve<IRunHistoryStore>(cadence => cadence.UseRunHistory<CountingHistoryStore>());

        Assert.IsType<CountingHistoryStore>(resolved);
    }

    [Fact]
    public void ACustomClockReplacesTheSystemClock()
    {
        var clock = new FakeClock(Occurrences.Utc(2026, 8, 24, 2, 0));

        var resolved = Resolve<ISystemClock>(cadence => cadence.UseClock(clock));

        Assert.Same(clock, resolved);
    }

    [Fact]
    public void CallingAReplacementTwiceLeavesNoShadowedRegistration()
    {
        var services = new ServiceCollection();

        CadenceServiceCollectionExtensions.AddCadenceCore(
            services,
            cadence => cadence
                .UseCoordinator(new ScriptedCoordinator(grantAll: true))
                .UseCoordinator<CustomCoordinator>(),
            scanAssembly: null);

        // One registration, not two with the first shadowed — otherwise a later reader of the
        // collection sees a coordinator that is never used.
        Assert.Single(services, sd => sd.ServiceType == typeof(IOccurrenceCoordinator));
    }

    [Fact]
    public void RegisteringASeamOnTheServiceCollectionDirectlyAlsoWins()
    {
        // Storage packages that predate the builder methods do this, so the in-memory defaults must
        // stay TryAdd-only and must be offered after the configure callback has run.
        var resolved = Resolve<IRunHistoryStore>(cadence =>
            cadence.Services.AddSingleton<IRunHistoryStore, CountingHistoryStore>());

        Assert.IsType<CountingHistoryStore>(resolved);
    }

    private static TService Resolve<TService>(Action<CadenceBuilder> configure)
        where TService : notnull
    {
        var services = new ServiceCollection();

        CadenceServiceCollectionExtensions.AddCadenceCore(services, configure, scanAssembly: null);

        return services.BuildServiceProvider().GetRequiredService<TService>();
    }
}

/// <summary>Someone's own coordinator: constructible by the container, no Cadence internals needed.</summary>
internal sealed class CustomCoordinator : IOccurrenceCoordinator
{
    public Task<bool> TryClaimAsync(
        string jobName,
        DateTimeOffset scheduledFor,
        Guid runId,
        CancellationToken ct)
        => Task.FromResult(true);
}

/// <summary>A history store that only counts, standing in for someone's own implementation.</summary>
internal sealed class CountingHistoryStore : IRunHistoryStore
{
    public int Started { get; private set; }

    public Task<JobRun> StartAsync(JobRunStart start, CancellationToken ct)
    {
        Started++;

        return Task.FromResult(new JobRun
        {
            RunId = start.RunId,
            JobName = start.JobName,
            Trigger = start.Trigger,
            Status = RunStatus.Running,
            InstanceId = start.InstanceId,
            StartedAt = start.StartedAt,
        });
    }

    public Task CompleteAsync(Guid runId, JobRunResult result, CancellationToken ct) => Task.CompletedTask;

    public Task AppendLogAsync(Guid runId, JobLogEntry entry, CancellationToken ct) => Task.CompletedTask;

    public Task<JobRun?> GetLastRunAsync(string jobName, CancellationToken ct) => Task.FromResult<JobRun?>(null);

    public Task<JobRun?> GetLastSuccessAsync(string jobName, CancellationToken ct)
        => Task.FromResult<JobRun?>(null);

    public Task<IReadOnlyList<JobRun>> QueryAsync(RunQuery query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JobRun>>([]);

    public Task<int> CountConsecutiveFailuresAsync(string jobName, CancellationToken ct)
        => Task.FromResult(0);

    public Task PurgeAsync(DateTimeOffset olderThan, CancellationToken ct) => Task.CompletedTask;
}
