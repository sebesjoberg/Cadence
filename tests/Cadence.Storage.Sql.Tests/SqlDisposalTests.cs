using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// What disposal does when the tier is registered the way <c>UseSqlStorage</c> registers it.
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
