using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Cadence.Storage.Redis.Tests;

/// <summary>§13.4: a store that is down is Degraded, not Unhealthy.</summary>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisStorageHealthCheckTests
{
    /// <summary>A configuration string nothing is listening on, for the tests that must not need one.</summary>
    private const string Unreachable = "127.0.0.1:1,abortConnect=true,connectTimeout=500,connectRetry=1";

    private readonly RedisFixture _fixture;

    public RedisStorageHealthCheckTests(RedisFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task AReachableRedisIsHealthy()
    {
        _fixture.RequireContainer();

        await using var connection = new RedisConnection(_fixture.CreateOptions("health"));
        var check = new RedisStorageHealthCheck(connection);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), default);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Null(result.Exception);
    }

    /// <summary>
    /// A plain <see cref="FactAttribute"/>, not skippable: a refused connection needs no container,
    /// and the one assertion that pins Degraded over Unhealthy must never be able to skip.
    /// </summary>
    [Fact]
    public async Task AnUnreachableRedisIsDegradedNotUnhealthy()
    {
        // Port 1 on loopback: nothing listens there, so the connection is refused rather than timing
        // out. abortConnect is what RedisConnection already forces, spelled out here so the intent of
        // the connection string is readable on its own.
        var options = new RedisStorageOptions { ConnectionString = Unreachable };
        options.Validate();

        await using var connection = new RedisConnection(options);
        var check = new RedisStorageHealthCheck(connection);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), default);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);

        // The exception type, not merely that there was one: an argument exception thrown before the
        // socket was touched would satisfy NotNull identically, and the only test that proves the
        // ping reaches a server is the one this machine skips.
        Assert.IsType<RedisConnectionException>(result.Exception);
    }

    [Fact]
    public void TheCheckIsRegisteredUnderItsNameAndTag()
    {
        var services = new ServiceCollection()
            .AddCadence(cadence => cadence.UseRedisStorage(Unreachable))
            .BuildServiceProvider();

        var registrations = services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Contains(
            registrations,
            r => r.Name == "cadence-redis" && r.Tags.Contains("cadence.storage"));

        // And not on the tags the kubelet reads. A storage check tagged ready is the failure mode
        // this whole section exists to prevent.
        Assert.DoesNotContain(
            registrations,
            r => r.Name == "cadence-redis" && (r.Tags.Contains("live") || r.Tags.Contains("ready")));
    }

    [Fact]
    public async Task CallingUseRedisStorageTwiceStillProducesAWorkingProvider()
    {
        // AddCheck appends unconditionally and the health check service refuses duplicate names, so
        // an unguarded registration turns a second UseRedisStorage call into a throw on every request.
        var services = new ServiceCollection().AddLogging()
            .AddCadence(cadence => cadence.UseRedisStorage(Unreachable).UseRedisStorage(Unreachable))
            .BuildServiceProvider();

        var report = await services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "cadence-redis", default);

        Assert.Equal(HealthStatus.Degraded, Assert.Single(report.Entries).Value.Status);
    }
}
