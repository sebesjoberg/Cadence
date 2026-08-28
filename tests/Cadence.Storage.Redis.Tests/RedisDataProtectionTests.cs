using Cadence.Storage.Redis.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Storage.Redis.Tests;

/// <summary>
/// The key ring in Redis, which is what makes the ticket cookie work across replicas.
/// </summary>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisDataProtectionTests : IAsyncDisposable
{
    private readonly RedisFixture _fixture;
    private readonly List<RedisConnection> _connections = [];
    private readonly List<ServiceProvider> _providers = [];

    public RedisDataProtectionTests(RedisFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public void TwoProvidersOverOneKeySpaceShareAKeyRing()
    {
        var options = _fixture.CreateOptions("dataprotection");

        var first = Protector(options);
        var protectedPayload = first.Protect("a ticket");

        // A second provider, as a second replica would build it: same store, no shared memory.
        var second = Protector(options);

        Assert.Equal("a ticket", second.Unprotect(protectedPayload));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }

    private IDataProtector Protector(RedisStorageOptions options)
    {
        var connection = new RedisConnection(options);
        _connections.Add(connection);

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDataProtection()
            .SetApplicationName("Cadence")
            .AddKeyManagementOptions(management => management.XmlRepository = Repository(connection));

        // Held rather than disposed here: the protector it hands back reads the key ring lazily.
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    private static RedisXmlRepository Repository(RedisConnection connection)
        => new RedisXmlRepository(
            () => connection.GetDatabaseAsync().GetAwaiter().GetResult(),
            connection.Keys.DataProtectionKeys);
}
