using Cadence.Storage.Conformance;
using Cadence.Storage.Sql.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// Runs the shared run-history contract against SQL Server.
/// </summary>
/// <remarks>
/// The same suite runs against the in-memory tier in <c>Cadence.Core.Tests</c>. Anything the two
/// tiers disagree about shows up here, which is the only reliable way to keep "just add a connection
/// string" from quietly changing behaviour.
/// </remarks>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlRunHistoryStoreConformanceTests : RunHistoryStoreConformance, IAsyncDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly List<SqlRunHistoryStore> _stores = [];

    public SqlRunHistoryStoreConformanceTests(SqlServerFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override async Task<IRunHistoryStore> CreateAsync()
    {
        var options = await _fixture.CreateMigratedAsync("history");

        var store = new SqlRunHistoryStore(
            new SqlDatabase(options), options, NullLogger<SqlRunHistoryStore>.Instance);

        _stores.Add(store);
        return store;
    }

    /// <inheritdoc />
    protected override Task SettleAsync(IRunHistoryStore store)
        // Progress appends are buffered, so a test that reports and then reads has to wait for the
        // batch. Deterministic, not a sleep: the flush barrier goes through the same queue.
        => ((SqlRunHistoryStore)store).FlushProgressAsync(default);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }
    }
}

/// <summary>
/// Runs the clustering contract against SQL Server. This is where the guarantee is actually proven —
/// it cannot be tested against the no-op coordinator, which grants everything.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlOccurrenceCoordinatorConformanceTests : OccurrenceCoordinatorConformance
{
    private readonly SqlServerFixture _fixture;
    private SqlStorageOptions? _shared;

    public SqlOccurrenceCoordinatorConformanceTests(SqlServerFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override async Task<IOccurrenceCoordinator> CreateAsync(string instanceId)
    {
        // One database per test, shared by every coordinator the test creates — so the instances
        // genuinely contend instead of each getting its own private table.
        _shared ??= await _fixture.CreateMigratedAsync("claims");

        return new SqlOccurrenceCoordinator(
            new SqlDatabase(_shared),
            new FixedClock(),
            Options.Create(new CadenceOptions { InstanceId = instanceId }),
            NullLogger<SqlOccurrenceCoordinator>.Instance);
    }
}

/// <summary>
/// Runs the schedule-source contract against SQL Server, including the polling change token.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlScheduleSourceConformanceTests : ScheduleSourceConformance
{
    private readonly SqlServerFixture _fixture;

    public SqlScheduleSourceConformanceTests(SqlServerFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override async Task<IWritableScheduleSource> CreateAsync()
    {
        var options = await _fixture.CreateMigratedAsync("schedules");

        return new SqlScheduleSource(
            new SqlDatabase(options), options, new FixedClock(), NullLogger<SqlScheduleSource>.Instance);
    }

    /// <inheritdoc />
    protected override Task PollAsync(IWritableScheduleSource source)
        // Drives the version-row poll directly rather than waiting out the interval, so the test is
        // deterministic and instant.
        => ((SqlScheduleSource)source).PollAsync(default);
}

/// <summary>A clock that never moves, for tests that do not care what time it is.</summary>
internal sealed class FixedClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Runs the shared pause contract against SQL Server.</summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlPauseStoreConformanceTests : PauseStoreConformance
{
    private readonly SqlServerFixture _fixture;
    private SqlStorageOptions? _shared;

    public SqlPauseStoreConformanceTests(SqlServerFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override async Task<IPauseStore> CreateAsync()
    {
        // One database per test, shared by every store the test creates, so "another instance"
        // means another instance rather than another database.
        _shared ??= await _fixture.CreateMigratedAsync("pause");

        return new SqlPauseStore(new SqlDatabase(_shared), new FixedClock());
    }
}

/// <summary>Runs the shared token contract against SQL Server.</summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlApiTokenStoreConformanceTests : ApiTokenStoreConformance
{
    private readonly SqlServerFixture _fixture;

    // Started at the real clock rather than the fixed default: the suite's expiry test builds its
    // expiry instant from DateTimeOffset.UtcNow, so a store reading 2026-08-24 would see it as long
    // past. Advancing from here still moves only when AdvanceAsync says so.
    private readonly FixedClock _clock = new() { UtcNow = DateTimeOffset.UtcNow };

    private SqlStorageOptions? _shared;

    public SqlApiTokenStoreConformanceTests(SqlServerFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override async Task<IApiTokenStore> CreateAsync()
    {
        // One database per test, shared by every store the test creates, so "another instance"
        // means another instance rather than another database.
        _shared ??= await _fixture.CreateMigratedAsync("tokens");

        return new SqlApiTokenStore(new SqlDatabase(_shared), _clock);
    }

    /// <inheritdoc />
    protected override Task AdvanceAsync(IApiTokenStore store, TimeSpan by)
    {
        _clock.Advance(by);
        return Task.CompletedTask;
    }
}

/// <summary>Runs the shared result contract against SQL Server.</summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlJobResultStoreConformanceTests : JobResultStoreConformance
{
    private readonly SqlServerFixture _fixture;
    private SqlStorageOptions? _shared;

    public SqlJobResultStoreConformanceTests(SqlServerFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override async Task<IJobResultStore> CreateAsync()
    {
        _shared ??= await _fixture.CreateMigratedAsync("results");

        return new SqlJobResultStore(new SqlDatabase(_shared));
    }
}
