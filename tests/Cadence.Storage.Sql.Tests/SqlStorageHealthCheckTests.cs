using Cadence.Storage.Sql.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>§13.4: a store that is down is Degraded, not Unhealthy.</summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlStorageHealthCheckTests
{
    /// <summary>A connection string nothing is listening on, for the tests that must not need one.</summary>
    private const string Unreachable =
        "Server=127.0.0.1,1;Database=cadence;User ID=sa;Password=not-a-real-password;" +
        "Encrypt=False;TrustServerCertificate=True;Connect Timeout=1";

    private readonly SqlServerFixture _fixture;

    public SqlStorageHealthCheckTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task AReachableDatabaseIsHealthy()
    {
        var options = new SqlStorageOptions { ConnectionString = await _fixture.CreateDatabaseAsync("health") };
        options.Validate();

        var check = new SqlStorageHealthCheck(new SqlDatabase(options));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), default);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Null(result.Exception);
    }

    /// <summary>
    /// A plain <see cref="FactAttribute"/>, not skippable: a refused connection needs no container,
    /// and the one assertion that pins Degraded over Unhealthy must never be able to skip.
    /// </summary>
    [Fact]
    public async Task AnUnreachableDatabaseIsDegradedNotUnhealthy()
    {
        // Port 1 on loopback: nothing listens there, so the connection is refused rather than timing
        // out, and the test costs milliseconds.
        var options = new SqlStorageOptions { ConnectionString = Unreachable };
        options.Validate();

        var check = new SqlStorageHealthCheck(new SqlDatabase(options));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), default);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);

        // The exception type, not merely that there was one: an argument exception thrown before the
        // socket was touched would satisfy NotNull identically, and the only tests that prove the
        // query reaches a server are the two this machine skips.
        Assert.IsType<SqlException>(result.Exception);
    }

    [Fact]
    public void TheCheckIsRegisteredUnderItsNameAndTag()
    {
        var services = new ServiceCollection()
            .AddCadence(cadence => cadence.UseSqlStorage(Unreachable))
            .BuildServiceProvider();

        var registrations = services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Contains(
            registrations,
            r => r.Name == "cadence-sql" && r.Tags.Contains("cadence.storage"));

        // And not on the tags the kubelet reads. A storage check tagged ready is the failure mode
        // this whole section exists to prevent.
        Assert.DoesNotContain(
            registrations,
            r => r.Name == "cadence-sql" && (r.Tags.Contains("live") || r.Tags.Contains("ready")));
    }

    [Fact]
    public async Task CallingUseSqlStorageTwiceStillProducesAWorkingProvider()
    {
        // AddCheck appends unconditionally and the health check service refuses duplicate names, so
        // an unguarded registration turns a second UseSqlStorage call into a throw on every request.
        var services = new ServiceCollection().AddLogging()
            .AddCadence(cadence => cadence.UseSqlStorage(Unreachable).UseSqlStorage(Unreachable))
            .BuildServiceProvider();

        var report = await services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "cadence-sql", default);

        Assert.Equal(HealthStatus.Degraded, Assert.Single(report.Entries).Value.Status);
    }
}
