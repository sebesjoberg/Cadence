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
