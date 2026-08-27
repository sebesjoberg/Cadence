using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using StackExchange.Redis;

namespace Cadence.Storage.Redis;

/// <summary>
/// Schedules in a Redis hash, versioned per job, with changes pushed rather than polled for.
/// </summary>
/// <remarks>
/// <para>
/// This is the tier's reason to exist. A schedule that can be edited while the application runs is
/// the product; the coordinator and the history are what it costs.
/// </para>
/// <para>
/// Changes arrive two ways. A write publishes to a channel, so a subscribed instance reacts in
/// milliseconds; and a counter is polled, so an instance whose subscription silently dropped still
/// notices within the poll interval. Redis pub/sub is fire-and-forget with no redelivery, which
/// makes the poll a correctness backstop rather than belt and braces — a scheduler that quietly
/// stopped noticing schedule edits would look perfectly healthy while ignoring the dashboard.
/// </para>
/// </remarks>
public sealed class RedisScheduleSource : IWritableScheduleSource, IAsyncDisposable
{
    private readonly RedisConnection _connection;
    private readonly RedisStorageOptions _options;
    private readonly ILogger<RedisScheduleSource> _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private CancellationTokenSource _tokenSource = new();
    private long _knownVersion = -1;
    private Task? _watcher;
    private int _disposed;

    internal RedisScheduleSource(
        RedisConnection connection,
        RedisStorageOptions options,
        ILogger<RedisScheduleSource> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSchedule>> GetAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var documents = await database.HashGetAllAsync(keys.Schedules).ConfigureAwait(false);

        if (documents.Length == 0)
        {
            return [];
        }

        var versions = await database.HashGetAllAsync(keys.ScheduleVersions).ConfigureAwait(false);

        var versionByJob = versions.ToDictionary(
            entry => (string)entry.Name!,
            entry => entry.Value.TryParse(out int version) ? version : 0,
            StringComparer.Ordinal);

        var schedules = new List<JobSchedule>(documents.Length);

        foreach (var document in documents)
        {
            var jobName = (string)document.Name!;
            versionByJob.TryGetValue(jobName, out var version);

            if (RedisValues.DeserialiseSchedule(jobName, (string)document.Value!, version) is { } schedule)
            {
                schedules.Add(schedule);
            }
            else
            {
                // Ignored rather than thrown: one unreadable row must not stop every other job's
                // schedule from loading, and the job falls back to what the code declared.
                _logger.ScheduleUnreadable(jobName);
            }
        }

        return schedules;
    }

    /// <inheritdoc />
    public async Task<JobSchedule?> GetAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var document = await database.HashGetAsync(keys.Schedules, jobName).ConfigureAwait(false);

        if (document.IsNullOrEmpty)
        {
            return null;
        }

        var stored = await database.HashGetAsync(keys.ScheduleVersions, jobName).ConfigureAwait(false);
        var version = stored.TryParse(out int parsed) ? parsed : 0;

        var schedule = RedisValues.DeserialiseSchedule(jobName, (string)document!, version);

        if (schedule is null)
        {
            _logger.ScheduleUnreadable(jobName);
        }

        return schedule;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(JobSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var result = await database.ScriptEvaluateAsync(
            Scripts.UpsertSchedule,
            [keys.Schedules, keys.ScheduleVersions, keys.ScheduleVersion],
            [
                schedule.JobName,
                schedule.Version,
                RedisValues.SerialiseSchedule(schedule),
            ]).ConfigureAwait(false);

        var values = (RedisResult[])result!;
        var written = (long)values[0] == 1;
        var version = (int)(long)values[1];

        if (!written)
        {
            throw new ScheduleConflictException(schedule.JobName, schedule.Version, version);
        }

        // This instance made the change, so it does not wait to hear about it. Its own publish
        // would come back through the subscription eventually, but "eventually" is not a property
        // a caller that just wrote should have to rely on.
        lock (_gate)
        {
            _knownVersion = version;
        }

        Signal();

        // Published after the write, outside the script: Redis does not deliver a script's
        // publishes until the script finishes anyway, and doing it here keeps the atomic part to
        // what actually has to be atomic.
        var subscriber = await _connection.GetSubscriberAsync().ConfigureAwait(false);
        await subscriber.PublishAsync(keys.ScheduleChannel, version).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IChangeToken GetChangeToken()
    {
        EnsureWatching();

        lock (_gate)
        {
            return new CancellationChangeToken(_tokenSource.Token);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Guarded because the container captures this instance once per service type it is
        // registered under, and disposes every capture.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_watcher is { } watcher)
        {
            try
            {
                await watcher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: shutdown is how the watcher ends.
            }
        }

        _shutdown.Dispose();
        _tokenSource.Dispose();
    }

    /// <summary>Checks whether the schedule version moved, firing the token when it has.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the version had moved.</returns>
    internal async Task<bool> PollAsync(CancellationToken cancellationToken)
    {
        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var stored = await database.StringGetAsync(keys.ScheduleVersion).ConfigureAwait(false);
        var current = stored.TryParse(out long version) ? version : 0;

        cancellationToken.ThrowIfCancellationRequested();

        return Observe(current);
    }

    private bool Observe(long current)
    {
        long previous;

        lock (_gate)
        {
            previous = _knownVersion;
            _knownVersion = current;
        }

        // The first observation establishes the baseline. Firing then would make every instance
        // reload on its first tick for no reason.
        if (previous < 0 || previous == current)
        {
            return false;
        }

        _logger.ScheduleVersionMoved(previous, current);
        Signal();
        return true;
    }

    private void Signal()
    {
        CancellationTokenSource stale;

        lock (_gate)
        {
            stale = _tokenSource;
            _tokenSource = new CancellationTokenSource();
        }

        // Cancelled outside the lock: callbacks registered on the token run inline, and running
        // arbitrary user code while holding this lock is how a reload deadlocks against a read.
        stale.Cancel();
        stale.Dispose();
    }

    private void EnsureWatching()
    {
        lock (_gate)
        {
            _watcher ??= Task.Run(() => WatchAsync(_shutdown.Token));
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var subscriber = await _connection.GetSubscriberAsync().ConfigureAwait(false);

            await subscriber.SubscribeAsync(
                _connection.Keys.ScheduleChannel,
                (_, value) =>
                {
                    if (value.TryParse(out long version))
                    {
                        Observe(version);
                    }
                }).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.ScheduleSubscribeFailed(ex, _options.SchedulePollInterval);
        }

        using var timer = new PeriodicTimer(_options.SchedulePollInterval);

        while (await WaitAsync(timer, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RedisException ex)
            {
                _logger.ScheduleVersionUnreadable(ex);
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
