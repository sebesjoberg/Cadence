using Cadence.Storage.Conformance;
using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Storage.Redis.Tests;

/// <summary>
/// Runs the shared run-history contract against Redis.
/// </summary>
/// <remarks>
/// The same suite runs against the in-memory tier and against SQL Server. It is the whole reason a
/// second persistent tier is credible: "an alternative to SQL Server" is a claim about behaviour,
/// and this is where the claim is checked rather than asserted.
/// </remarks>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisRunHistoryStoreConformanceTests : RunHistoryStoreConformance, IAsyncDisposable
{
    private readonly RedisFixture _fixture;
    private readonly List<RedisRunHistoryStore> _stores = [];
    private readonly List<RedisConnection> _connections = [];

    public RedisRunHistoryStoreConformanceTests(RedisFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override Task<IRunHistoryStore> CreateAsync()
    {
        var options = _fixture.CreateOptions("history");
        var connection = new RedisConnection(options);
        _connections.Add(connection);

        var store = new RedisRunHistoryStore(
            connection, options, NullLogger<RedisRunHistoryStore>.Instance);

        _stores.Add(store);
        return Task.FromResult<IRunHistoryStore>(store);
    }

    /// <inheritdoc />
    protected override Task SettleAsync(IRunHistoryStore store)
        // Progress appends are buffered, so a test that reports and then reads has to wait for the
        // batch. Deterministic, not a sleep: the flush barrier goes through the same queue.
        => ((RedisRunHistoryStore)store).FlushProgressAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }
}

/// <summary>
/// Runs the clustering contract against Redis.
/// </summary>
/// <remarks>
/// The suite this inherits is the clustering guarantee written down. Passing it is what makes the
/// Redis coordinator an alternative to the SQL one rather than a different set of behaviours wearing
/// the same interface.
/// </remarks>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisOccurrenceCoordinatorConformanceTests
    : OccurrenceCoordinatorConformance, IAsyncDisposable
{
    private readonly RedisFixture _fixture;
    private readonly List<RedisConnection> _connections = [];
    private RedisStorageOptions? _shared;

    public RedisOccurrenceCoordinatorConformanceTests(RedisFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override Task<IOccurrenceCoordinator> CreateAsync(string instanceId)
    {
        // One key space per test, shared by every coordinator the test creates — so the instances
        // genuinely contend instead of each getting its own private prefix.
        _shared ??= _fixture.CreateOptions("claims");

        var connection = new RedisConnection(_shared);
        _connections.Add(connection);

        return Task.FromResult<IOccurrenceCoordinator>(new RedisOccurrenceCoordinator(
            connection,
            new FixedClock(),
            Options.Create(new CadenceOptions { InstanceId = instanceId })));
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

/// <summary>
/// Runs the schedule-source contract against Redis, including the change token.
/// </summary>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisScheduleSourceConformanceTests : ScheduleSourceConformance, IAsyncDisposable
{
    private readonly RedisFixture _fixture;
    private readonly List<RedisScheduleSource> _sources = [];
    private readonly List<RedisConnection> _connections = [];

    public RedisScheduleSourceConformanceTests(RedisFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override Task<IWritableScheduleSource> CreateAsync()
    {
        var options = _fixture.CreateOptions("schedules");
        var connection = new RedisConnection(options);
        _connections.Add(connection);

        var source = new RedisScheduleSource(
            connection, options, NullLogger<RedisScheduleSource>.Instance);

        _sources.Add(source);
        return Task.FromResult<IWritableScheduleSource>(source);
    }

    /// <inheritdoc />
    protected override Task PollAsync(IWritableScheduleSource source)
        // Drives the version poll directly rather than waiting out the interval, so the test is
        // deterministic and instant. The push path is exercised separately, in RedisStorageTests.
        => ((RedisScheduleSource)source).PollAsync(default);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }

        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }
}

/// <summary>A clock that never moves, for tests that do not care what time it is.</summary>
internal sealed class FixedClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Runs the shared pause contract against Redis.</summary>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisPauseStoreConformanceTests : PauseStoreConformance, IAsyncDisposable
{
    private readonly RedisFixture _fixture;
    private readonly List<RedisConnection> _connections = [];

    private RedisStorageOptions? _shared;

    public RedisPauseStoreConformanceTests(RedisFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override Task<IPauseStore> CreateAsync()
    {
        _shared ??= _fixture.CreateOptions("pause");

        var connection = new RedisConnection(_shared);
        _connections.Add(connection);

        return Task.FromResult<IPauseStore>(new RedisPauseStore(connection, new FixedClock()));
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

/// <summary>
/// Runs the shared token contract against Redis.
/// </summary>
/// <remarks>
/// Expiry is a key time-to-live here, so this binding has no clock to move. <c>AdvanceAsync</c>
/// makes Redis drop what the horizon would have dropped, which is also what keeps the test instant.
/// </remarks>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisApiTokenStoreConformanceTests : ApiTokenStoreConformance, IAsyncDisposable
{
    private readonly RedisFixture _fixture;
    private readonly List<RedisConnection> _connections = [];

    private RedisStorageOptions? _shared;

    public RedisApiTokenStoreConformanceTests(RedisFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override Task<IApiTokenStore> CreateAsync()
    {
        // One key space per test, shared by every store the test creates, so "another instance"
        // means another instance rather than another key space.
        _shared ??= _fixture.CreateOptions("tokens");

        var connection = new RedisConnection(_shared);
        _connections.Add(connection);

        return Task.FromResult<IApiTokenStore>(
            new RedisApiTokenStore(connection, new FixedClock()));
    }

    /// <inheritdoc />
    protected override async Task AdvanceAsync(IApiTokenStore store, TimeSpan by)
    {
        // Every store this test made shares one key space, so any of their connections will do.
        var connection = _connections[0];
        var keys = connection.Keys;
        var database = await connection.GetDatabaseAsync();

        foreach (var entry in await database.HashGetAllAsync(keys.Tokens))
        {
            var key = keys.Token((string)entry.Value!);

            // Only keys that carry a TTL, so advancing a clock cannot expire a token that was
            // created without an expiry.
            if (await database.KeyTimeToLiveAsync(key) is not null)
            {
                await database.KeyExpireAsync(key, DateTime.UtcNow.AddSeconds(-1));
            }
        }
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
