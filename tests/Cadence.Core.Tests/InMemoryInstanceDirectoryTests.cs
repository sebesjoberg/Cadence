using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Core.Tests;

public sealed class InMemoryInstanceDirectoryTests
{
    [Fact]
    public async Task ReportsThisProcessAsTheOnlyInstance()
    {
        var services = new ServiceCollection();
        services.AddCadence(cadence => cadence.Services.AddLogging());

        await using var provider = services.BuildServiceProvider();

        var directory = provider.GetRequiredService<IInstanceDirectory>();
        var instances = await directory.GetAllAsync(default);

        var only = Assert.Single(instances);

        Assert.Equal(Environment.MachineName, only.MachineName);
        Assert.Equal(Environment.ProcessId, only.ProcessId);
        Assert.Equal(DateTimeKind.Utc, only.StartedAtUtc.UtcDateTime.Kind);
    }
}
