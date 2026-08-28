using Cadence.Storage.Conformance;
using Cadence.Storage.Redis.Internal;
using Xunit;

namespace Cadence.Storage.Redis.Tests;

/// <summary>Runs the shared instance-directory contract against Redis.</summary>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisInstanceDirectoryTests : InstanceDirectoryConformance, IAsyncDisposable
{
    private readonly RedisFixture _fixture;
    private readonly List<RedisConnection> _connections = [];

    public RedisInstanceDirectoryTests(RedisFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override Task<(IInstanceDirectory Directory, Func<InstanceInfo, CancellationToken, Task> Beat)>
        CreateAsync(CancellationToken cancellationToken)
    {
        var options = _fixture.CreateOptions("instances");
        var connection = new RedisConnection(options);
        _connections.Add(connection);

        var directory = new RedisInstanceDirectory(connection);

        return Task.FromResult<(IInstanceDirectory, Func<InstanceInfo, CancellationToken, Task>)>(
            (directory, (instance, ct) => RedisFixture.WriteInstanceAsync(options, instance, ct)));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }
}
