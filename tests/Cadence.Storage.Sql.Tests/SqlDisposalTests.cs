using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// How <c>UseSqlStorage</c> registers the tier, and what disposal then does with it.
/// </summary>
/// <remarks>
/// No container and no server: the schedule source connects lazily, so everything these tests care
/// about happens before a connection is opened.
/// </remarks>
public sealed class SqlDisposalTests
{
    /// <summary>
    /// A port nothing is listening on, with SqlClient's own retry turned off so the connection
    /// fails in milliseconds rather than after its default ten-second second attempt.
    /// </summary>
    private const string Unreachable =
        "Server=127.0.0.1,1;Database=cadence;User Id=sa;Password=Unused_1234;" +
        "Encrypt=False;Connect Timeout=2;ConnectRetryCount=0";

    [Fact]
    public async Task DisposingTheProviderDoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCadence(cadence => cadence.UseSqlStorage(Unreachable));

        var provider = services.BuildServiceProvider();

        // Both interfaces, because the container owns one disposable per registration the instance
        // is reachable through.
        _ = provider.GetRequiredService<IScheduleSource>();
        _ = provider.GetRequiredService<IWritableScheduleSource>();

        var thrown = await Record.ExceptionAsync(async () => await provider.DisposeAsync());

        Assert.Null(thrown);
    }

    [Fact]
    public void TheTokenStoreWinsBothInterfacesAsOneSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCadence(cadence => cadence.UseSqlStorage(Unreachable));

        var provider = services.BuildServiceProvider();

        // IsType, not IsAssignableFrom: AddCadence offers ConfiguredApiTokenStore for IApiTokenStore,
        // and a tier that persists tokens it cannot then resolve would fail nowhere else.
        var read = Assert.IsType<SqlApiTokenStore>(provider.GetRequiredService<IApiTokenStore>());
        var writable = Assert.IsType<SqlApiTokenStore>(
            provider.GetRequiredService<IWritableApiTokenStore>());

        // One instance behind both, which is what lets the janitor's expired-token pass reach the
        // same store the request path resolves through.
        Assert.Same(read, writable);
    }

    [Fact]
    public void TheTokenStoreDisplacesAStoreRegisteredBeforeAddCadence()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The only ordering that tells Replace and TryAdd apart: AddCadence runs the callback before
        // offering its own defaults, so a TryAdd would beat those but not a registration made ahead
        // of AddCadence -- leaving the interface on a store that persists nothing.
        services.AddSingleton<IApiTokenStore>(new ConfiguredApiTokenStore());
        services.AddCadence(cadence => cadence.UseSqlStorage(Unreachable));

        var provider = services.BuildServiceProvider();

        Assert.IsType<SqlApiTokenStore>(provider.GetRequiredService<IApiTokenStore>());
    }

    [Fact]
    public async Task AFailedStartupSurfacesTheStorageFailureRatherThanADisposalError()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddCadence(cadence => cadence.UseSqlStorage(Unreachable));

        var host = builder.Build();

        // RunAsync's shape: start, then dispose in a finally. A disposal that throws replaces the
        // failure the operator needs to see on its way out.
        var thrown = await Record.ExceptionAsync(async () =>
        {
            try
            {
                await host.StartAsync(CancellationToken.None);
            }
            finally
            {
                await ((IAsyncDisposable)host).DisposeAsync();
            }
        });

        Assert.IsType<SqlException>(thrown);
    }
}
