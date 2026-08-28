using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Storage.Redis.Tests;

/// <summary>
/// What disposal does when the tier is registered the way <c>UseRedisStorage</c> registers it.
/// </summary>
/// <remarks>
/// No container: the connection is lazy, so nothing here needs a Redis to talk to.
/// </remarks>
public sealed class RedisDisposalTests
{
    private const string Unreachable = "127.0.0.1:1,abortConnect=false,connectTimeout=200";

    [Fact]
    public async Task DisposingTheProviderDoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCadence(cadence => cadence.UseRedisStorage(Unreachable));

        var provider = services.BuildServiceProvider();

        // Both interfaces, because the container owns one disposable per registration the instance
        // is reachable through.
        _ = provider.GetRequiredService<IScheduleSource>();
        _ = provider.GetRequiredService<IWritableScheduleSource>();

        var thrown = await Record.ExceptionAsync(async () => await provider.DisposeAsync());

        Assert.Null(thrown);
    }

    [Fact]
    public async Task TheTokenStoreWinsBothInterfacesAsOneSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCadence(cadence => cadence.UseRedisStorage(Unreachable));

        // Await using, not using: RedisConnection is async-disposable only, so a synchronous
        // disposal of the container throws -- which is the sibling test's whole subject.
        await using var provider = services.BuildServiceProvider();

        // IsType, not IsAssignableFrom: AddCadence offers ConfiguredApiTokenStore for IApiTokenStore,
        // and a tier that persists tokens it cannot then resolve would fail nowhere else.
        var read = Assert.IsType<RedisApiTokenStore>(provider.GetRequiredService<IApiTokenStore>());
        var writable = Assert.IsType<RedisApiTokenStore>(
            provider.GetRequiredService<IWritableApiTokenStore>());

        Assert.Same(read, writable);
    }
}
