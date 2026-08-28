using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Cadence.Storage.Redis.Tests;

/// <summary>
/// What is true of the Redis tier specifically, beyond the contract every tier shares.
/// </summary>
/// <remarks>
/// The conformance suites prove this tier behaves like the others. These prove the decisions that
/// are only visible here: that a claim does not expire, that the janitor removes it with the run it
/// belongs to, and that a schedule edit travels between instances by push rather than only by poll.
/// </remarks>
[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisStorageTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);

    private readonly RedisFixture _fixture;
    private readonly List<RedisConnection> _connections = [];
    private readonly List<IAsyncDisposable> _disposables = [];

    public RedisStorageTests(RedisFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task AClaimDoesNotExpire()
    {
        /*
            The reason this tier does not use SET NX EX. A claim with a TTL is a claim that can be
            won twice -- not within the tick's horizon, but by anything replaying an older
            occurrence, which is exactly what catch-up after downtime does. There is no way to wait
            out a TTL in a test without making the test take that long, so this asserts the property
            that makes the TTL unnecessary: the key carries no expiry at all.
        */
        var options = _fixture.CreateOptions("permanent");
        var connection = Track(new RedisConnection(options));
        var coordinator = Coordinator(connection, "one");

        Assert.True(await coordinator.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));

        var database = await connection.GetDatabaseAsync();
        var ttl = await database.KeyTimeToLiveAsync(connection.Keys.Occurrence("job", Occurrence));

        Assert.Null(ttl);
    }

    [SkippableFact]
    public async Task TokenExpiryIsTheKeyTimeToLive()
    {
        // Both halves of the tier's expiry decision. A token with an expiry gets a TTL, so it stops
        // existing rather than failing a predicate somebody could forget; a token without one gets
        // no TTL at all, or it would disappear on its own. The listing at the end is the third
        // effect of the same atomic write: an index entry, without which neither token could be
        // revoked.
        var options = _fixture.CreateOptions("tokenttl");
        var connection = Track(new RedisConnection(options));
        var store = new RedisApiTokenStore(connection, new FixedClock());

        var (_, expiring) = ApiTokenSecret.Create();
        var (_, permanent) = ApiTokenSecret.Create();

        await store.CreateAsync(
            new ApiTokenCreation(
                "expiring", ApiTokenScope.Read, DateTimeOffset.UtcNow.AddHours(1), null, null),
            expiring,
            default);

        await store.CreateAsync(
            new ApiTokenCreation("permanent", ApiTokenScope.Read, null, null, null),
            permanent,
            default);

        var database = await connection.GetDatabaseAsync();

        var ttlOfExpiring = await database.KeyTimeToLiveAsync(
            connection.Keys.Token(Convert.ToHexStringLower(expiring)));

        Assert.NotNull(ttlOfExpiring);
        // Generously bounded either side of the hour: the TTL is resolved against the server's
        // clock and reported to the millisecond, so an exact bound would flake on a little skew.
        Assert.InRange(
            ttlOfExpiring.Value, TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(65));

        Assert.Null(await database.KeyTimeToLiveAsync(
            connection.Keys.Token(Convert.ToHexStringLower(permanent))));

        Assert.Equal(2, (await store.ListAsync(default)).Count);
    }

    [SkippableFact]
    public async Task TheClaimIsTheRun()
    {
        // The property the design plan asks of any coordinator: no window in which a slot is taken
        // but unrecorded. A process dying here leaves a visible Running run for the janitor, not an
        // unexplained gap in the schedule.
        var options = _fixture.CreateOptions("claimisrun");
        var connection = Track(new RedisConnection(options));
        var store = Track(History(connection, options));

        var runId = Guid.NewGuid();
        Assert.True(await Coordinator(connection, "one").TryClaimAsync("job", Occurrence, runId, default));

        var run = await store.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(runId, run.RunId);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(Occurrence, run.ScheduledFor);
        Assert.Equal("one", run.InstanceId);
    }

    [SkippableFact]
    public async Task PurgingARunReleasesItsOccurrence()
    {
        /*
            Deliberate, and worth stating plainly: once a run has aged out of retention, its
            occurrence can be claimed again. The alternative is a key per occurrence kept forever,
            which for a per-minute job is half a million keys a year holding nothing but "this
            already happened". Retention is the bound on how far back double-execution is prevented,
            and it is measured in weeks by default.
        */
        var options = _fixture.CreateOptions("purgeclaim");
        var connection = Track(new RedisConnection(options));
        var store = Track(History(connection, options));
        var coordinator = Coordinator(connection, "one");

        var first = Guid.NewGuid();
        Assert.True(await coordinator.TryClaimAsync("job", Occurrence, first, default));

        await store.CompleteAsync(first, JobRunResult.Success(TimeSpan.Zero, Occurrence), default);
        Assert.False(await coordinator.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));

        var maintenance = new RedisStorageMaintenance(connection);
        var purged = await maintenance.PurgeRunsByAgeAsync(Occurrence.AddDays(1), 100, default);

        Assert.Equal(1, purged);
        Assert.True(await coordinator.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task ARunWhoseInstanceNeverHeartbeatsIsReapedAsLost()
    {
        var options = _fixture.CreateOptions("reap");
        var connection = Track(new RedisConnection(options));
        var store = Track(History(connection, options));

        var runId = Guid.NewGuid();

        await store.StartAsync(
            new JobRunStart
            {
                RunId = runId,
                JobName = "job",
                Trigger = TriggerKind.Schedule,
                InstanceId = "gone",
                StartedAt = Origin,
            },
            default);

        var maintenance = new RedisStorageMaintenance(connection);
        var reaped = await maintenance.ReapAbandonedRunsAsync(Origin, Origin.AddMinutes(5), 100, default);

        Assert.Equal(1, reaped);

        var run = await store.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(RunStatus.Lost, run.Status);
        Assert.Equal(Origin.AddMinutes(5), run.CompletedAt);
    }

    [SkippableFact]
    public async Task AReapPassDoesNotStallOnRunsItMustKeep()
    {
        /*
            Redis has no "update the first N rows matching a predicate", so the reap script reads a
            window of the running index and decides per entry. Live runs stay in that index, so a
            pass that advanced its offset only by what it changed would rescan the same healthy runs
            forever. This is that loop, with a healthy run at the head of the index.
        */
        var options = _fixture.CreateOptions("reapstall");
        var connection = Track(new RedisConnection(options));
        var store = Track(History(connection, options));

        var database = await connection.GetDatabaseAsync();

        // Alive, and oldest -- so it sits at the head of the running index.
        await database.SortedSetAddAsync(
            connection.Keys.Heartbeats, "alive", RedisValues.Ticks(Origin.AddMinutes(10)));

        await store.StartAsync(Start(Guid.NewGuid(), "job", "alive", Origin), default);
        await store.StartAsync(Start(Guid.NewGuid(), "job", "gone", Origin.AddMinutes(1)), default);

        var maintenance = new RedisStorageMaintenance(connection);

        // A batch size of one forces the pass to walk the index an entry at a time, which is the
        // shape that stalls if the offset is wrong.
        var reaped = await maintenance
            .ReapAbandonedRunsAsync(Origin.AddMinutes(5), Origin.AddMinutes(20), 1, default)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, reaped);
    }

    [SkippableFact]
    public async Task TrimmingKeepsTheNewestRunsPerJob()
    {
        var options = _fixture.CreateOptions("trim");
        var connection = Track(new RedisConnection(options));
        var store = Track(History(connection, options));

        for (var i = 0; i < 5; i++)
        {
            var runId = Guid.NewGuid();
            var startedAt = Origin.AddMinutes(i);

            await store.StartAsync(Start(runId, "job", "one", startedAt), default);
            await store.CompleteAsync(runId, JobRunResult.Success(TimeSpan.Zero, startedAt), default);
        }

        var maintenance = new RedisStorageMaintenance(connection);
        var trimmed = await maintenance.TrimRunsPerJobAsync(2, 100, default);

        Assert.Equal(3, trimmed);

        var remaining = await store.QueryAsync(new RunQuery { JobName = "job" }, default);

        Assert.Equal(2, remaining.Count);
        Assert.Equal(Origin.AddMinutes(4), remaining[0].StartedAt);
        Assert.Equal(Origin.AddMinutes(3), remaining[1].StartedAt);
    }

    [SkippableFact]
    public async Task PurgingLeavesNothingBehind()
    {
        /*
            The scripts assemble key names from fragments rather than declaring them in KEYS, because
            a completion is handed a run id and has to reach that run's job index. The fragments come
            from RedisKeys so the two halves cannot disagree about the prefix -- but nothing in the
            compiler checks that they are concatenated in the right order, and a purge that built the
            wrong name would silently leak rather than fail.

            So this asserts the outcome instead of the spelling: after a purge, the key space holds
            nothing about that run. An orphaned log list or job-index entry fails here.
        */
        var options = _fixture.CreateOptions("noleak");
        var connection = Track(new RedisConnection(options));
        var store = Track(History(connection, options));

        var runId = Guid.NewGuid();

        Assert.True(await Coordinator(connection, "one").TryClaimAsync("job", Occurrence, runId, default));

        await store.AppendLogAsync(
            runId, new JobLogEntry { Timestamp = Occurrence, Message = "working" }, default);

        await store.FlushProgressAsync();
        await store.CompleteAsync(runId, JobRunResult.Success(TimeSpan.Zero, Occurrence), default);

        var before = await KeysAsync(options);
        Assert.Contains(before, k => k.Contains(runId.ToString("N"), StringComparison.Ordinal));

        var purged = await new RedisStorageMaintenance(connection)
            .PurgeRunsByAgeAsync(Occurrence.AddDays(1), 100, default);

        Assert.Equal(1, purged);

        var after = await KeysAsync(options);

        Assert.DoesNotContain(after, k => k.Contains(runId.ToString("N"), StringComparison.Ordinal));
        Assert.DoesNotContain(after, k => k.StartsWith(options.KeyPrefix + "runs:job:", StringComparison.Ordinal));
        Assert.DoesNotContain(after, k => k.StartsWith(options.KeyPrefix + "runs:instance:", StringComparison.Ordinal));
        Assert.DoesNotContain(after, k => k.StartsWith(options.KeyPrefix + "occ:", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ARunningRunIsNeverPurgedHoweverOldItIs()
    {
        var options = _fixture.CreateOptions("keeprunning");
        var connection = Track(new RedisConnection(options));
        var store = Track(History(connection, options));

        await store.StartAsync(Start(Guid.NewGuid(), "job", "one", Origin), default);

        var maintenance = new RedisStorageMaintenance(connection);
        var purged = await maintenance.PurgeRunsByAgeAsync(Origin.AddYears(1), 100, default);

        Assert.Equal(0, purged);
        Assert.NotNull(await store.GetLastRunAsync("job", default));
    }

    [SkippableFact]
    public async Task AnEditOnOneInstanceIsPushedToAnother()
    {
        /*
            The poll is covered by the shared conformance suite, which drives it directly. This is
            the other path: a write publishes, and an instance that never polls still notices. That
            is what makes a dashboard edit feel immediate rather than arriving up to a poll interval
            later.
        */
        var options = _fixture.CreateOptions("push");

        var writerConnection = Track(new RedisConnection(options));
        var readerConnection = Track(new RedisConnection(options));

        var writer = Track(Schedules(writerConnection, options));
        var reader = Track(Schedules(readerConnection, options));

        // Establishes the reader's baseline and starts its subscription.
        var token = reader.GetChangeToken();
        await reader.PollAsync(default);
        Assert.False(token.HasChanged);

        await writer.UpsertAsync(
            new JobSchedule
            {
                JobName = "job",
                CronExpression = "0 * * * *",
                TimeZoneId = "UTC",
                Enabled = true,
            },
            default);

        // No PollAsync here on purpose: only the push can fire this token.
        await WaitForAsync(() => token.HasChanged, TimeSpan.FromSeconds(10));

        Assert.True(token.HasChanged, "a published schedule change has to reach a subscribed instance");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }

    private static JobRunStart Start(Guid runId, string jobName, string instanceId, DateTimeOffset startedAt)
        => new()
        {
            RunId = runId,
            JobName = jobName,
            Trigger = TriggerKind.Schedule,
            InstanceId = instanceId,
            StartedAt = startedAt,
        };

    /// <summary>Every key in this test's key space, read straight from the server.</summary>
    /// <remarks>
    /// Its own connection rather than the one under test, so the assertion does not depend on the
    /// abstraction it is checking.
    /// </remarks>
    private static async Task<List<string>> KeysAsync(RedisStorageOptions options)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options.ConnectionString);

        var endpoint = multiplexer.GetEndPoints()[0];
        var server = multiplexer.GetServer(endpoint);

        return [.. server.Keys(pattern: $"{options.KeyPrefix}*").Select(k => (string)k!)];
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private static RedisOccurrenceCoordinator Coordinator(RedisConnection connection, string instanceId)
        => new(connection, new FixedClock(), Options.Create(new CadenceOptions { InstanceId = instanceId }));

    private static RedisRunHistoryStore History(RedisConnection connection, RedisStorageOptions options)
        => new(connection, options, NullLogger<RedisRunHistoryStore>.Instance);

    private static RedisScheduleSource Schedules(RedisConnection connection, RedisStorageOptions options)
        => new(connection, options, NullLogger<RedisScheduleSource>.Instance);

    private RedisConnection Track(RedisConnection connection)
    {
        _connections.Add(connection);
        return connection;
    }

    private T Track<T>(T disposable)
        where T : IAsyncDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }
}
