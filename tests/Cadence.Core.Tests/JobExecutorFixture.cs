using System.Diagnostics.Metrics;
using System.Text.Json;
using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Scheduling;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cadence.Core.Tests;

/// <summary>Builds a JobExecutor over in-memory stores, with the jobs the tests dispatch.</summary>
internal sealed class JobExecutorFixture : IAsyncDisposable
{
    private ServiceProvider _provider = null!;

    public JobExecutor Executor { get; private set; } = null!;

    public InMemoryRunHistoryStore History { get; } = new();

    public InMemoryJobResultStore Results { get; } = new();

    public JobSpy Spy { get; } = new();

    public DateTimeOffset Now { get; private set; }

    public static JobExecutorFixture Create(Action<CadenceOptions>? configureOptions = null)
    {
        var fixture = new JobExecutorFixture();

        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton(fixture.Spy);
        services.AddScoped<ScopeMarker>();
        services.AddTransient<SucceedingJob>();
        services.AddTransient<FailingJob>();
        services.AddTransient<GatedJob>();
        services.AddTransient<NeverEndingJob>();
        services.AddTransient<ReportingJob>();
        services.AddTransient<FileResultJob>();
        services.AddTransient<PocoResultJob>();
        services.AddTransient<OversizedResultJob>();
        services.AddTransient<NullResultJob>();
    services.AddTransient<AmbiguousResultJob>();

        services.AddSingleton(typeof(IJobResultSerializer<>), typeof(JsonJobResultSerializer<>));
        services.AddSingleton<IJobResultSerializer<JobResult>, JobResultPassthroughSerializer>();

        fixture._provider = services.BuildServiceProvider();

        var clock = new FakeClock(Occurrences.Utc(2026, 8, 24, 2, 0));
        var options = new CadenceOptions { InstanceId = "test:1:aaaaaaaa" };
        configureOptions?.Invoke(options);

        fixture.Now = clock.UtcNow;

    var metrics = new CadenceMetrics(fixture._provider.GetRequiredService<IMeterFactory>());

        fixture.Executor = new JobExecutor(
            fixture._provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.History,
            fixture.Results,
            new RunHistoryProgressSink(fixture.History, clock, NullLogger<RunHistoryProgressSink>.Instance),
            clock,
            metrics,
            Options.Create(options),
            NullLogger<JobExecutor>.Instance);

        return fixture;
    }

    public Task<DispatchResult> DispatchAsync<TJob>(RunSettings settings, JsonElement? payload = null)
        where TJob : IJob
        => Executor.DispatchAsync(
            new JobDescriptor { Name = "job", ImplementationType = typeof(TJob) },
            settings,
            scheduledFor: null,
            TriggerKind.Manual,
            payload,
            CancellationToken.None);

    public static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    public async ValueTask DisposeAsync()
    {
        Spy.Gate.TrySetResult();
        await Executor.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
