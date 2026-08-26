using Cadence.Diagnostics;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Core.Tests;

/// <summary>
/// §13.4: the probes the kubelet reads cannot fail on a store blip, because they cannot see one.
/// </summary>
public sealed class HealthCheckTests
{
    private static readonly Type[] Stores =
    [
        typeof(IRunHistoryStore),
        typeof(IScheduleSource),
        typeof(IPauseStore),
        typeof(IOccurrenceCoordinator),
    ];

    [Theory]
    [InlineData(typeof(LivenessHealthCheck))]
    [InlineData(typeof(ReadinessHealthCheck))]
    public void TheProbesAreGivenNoStoreToQuery(Type check)
    {
        var parameters = check.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);

        Assert.Empty(parameters.Intersect(Stores));
    }

    [Fact]
    public async Task LivenessIsHealthyWheneverTheProcessAnswers()
    {
        var result = await new LivenessHealthCheck()
            .CheckHealthAsync(new HealthCheckContext(), default);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReadinessIsUnhealthyUntilBootHasPassed()
    {
        var readiness = new CadenceReadiness();
        var check = new ReadinessHealthCheck(readiness, new JobRegistry([]));

        var before = await check.CheckHealthAsync(new HealthCheckContext(), default);
        readiness.MarkReady();
        var after = await check.CheckHealthAsync(new HealthCheckContext(), default);

        Assert.Equal(HealthStatus.Unhealthy, before.Status);
        Assert.Equal(HealthStatus.Healthy, after.Status);
    }

    [Fact]
    public async Task StartingTheHostMakesTheReadyProbePass()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddCadence())
            .Build();

        var readiness = host.Services.GetRequiredService<CadenceReadiness>();
        Assert.False(readiness.IsReady);

        await host.StartAsync();
        Assert.True(readiness.IsReady);

        await host.StopAsync();
    }

    [Fact]
    public async Task AHostThatFailsItsBootChecksNeverReportsReady()
    {
        // Pins the ordering inside StartAsync: MarkReady runs after the boot work, so a start that
        // throws leaves readiness false. Move the call to the top of StartAsync and this fails.
        using var host = new HostBuilder()
            .ConfigureServices(services => services
                .AddCadence()
                .Configure<CadenceOptions>(options => options.TickInterval = TimeSpan.Zero))
            .Build();

        var readiness = host.Services.GetRequiredService<CadenceReadiness>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.StartAsync());

        Assert.False(readiness.IsReady);
    }

    [Fact]
    public void BothProbesAreRegisteredWithTheirTags()
    {
        var services = new ServiceCollection().AddCadence().BuildServiceProvider();
        var registrations = services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Contains(registrations, r => r.Name == "cadence-live" && r.Tags.Contains("live"));
        Assert.Contains(registrations, r => r.Name == "cadence-ready" && r.Tags.Contains("ready"));
    }
}
