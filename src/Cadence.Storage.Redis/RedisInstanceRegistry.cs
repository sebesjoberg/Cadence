using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Cadence.Storage.Redis;

/// <summary>
/// Keeps this instance visible to the janitor by refreshing a heartbeat.
/// </summary>
/// <remarks>
/// <para>
/// Two keys rather than one, and deliberately: the registration details live in a hash and the
/// heartbeat in a sorted set scored by its instant. The reap pass asks "which instances have not
/// been seen since X", which a sorted set answers with a range query and a hash of JSON documents
/// answers by reading every one of them.
/// </para>
/// <para>
/// No key expiry. Letting Redis expire a dead instance would be less code and would delete the
/// record at the moment its runs are being reaped — leaving history pointing at an instance nothing
/// can explain, which is the question someone reads history to answer. The janitor removes it later,
/// on purpose.
/// </para>
/// </remarks>
public sealed class RedisInstanceRegistry : BackgroundService
{
    private readonly RedisConnection _connection;
    private readonly RedisStorageOptions _options;
    private readonly ISystemClock _clock;
    private readonly CadenceOptions _cadenceOptions;
    private readonly ILogger<RedisInstanceRegistry> _logger;

    internal RedisInstanceRegistry(
        RedisConnection connection,
        RedisStorageOptions options,
        ISystemClock clock,
        IOptions<CadenceOptions> cadenceOptions,
        ILogger<RedisInstanceRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cadenceOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _options = options;
        _clock = clock;
        _cadenceOptions = cadenceOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Registered before the first wait, so an instance is visible from the moment it starts
        // rather than one interval later — otherwise a janitor pass in that window would see runs
        // owned by an instance it has no record of.
        await BeatQuietlyAsync(register: true, stoppingToken).ConfigureAwait(false);

        _logger.InstanceRegistered(_cadenceOptions.InstanceId, _options.HeartbeatInterval);

        using var timer = new PeriodicTimer(_options.HeartbeatInterval);

        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            await BeatQuietlyAsync(register: false, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // A graceful stop removes the record, so the janitor does not have to wait out the heartbeat
        // timeout to reap anything this instance abandoned. On an ungraceful stop the record stays
        // and the timeout does the work instead.
        try
        {
            var keys = _connection.Keys;
            var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

            await database.HashDeleteAsync(keys.Instances, _cadenceOptions.InstanceId)
                .ConfigureAwait(false);

            await database.SortedSetRemoveAsync(keys.Heartbeats, _cadenceOptions.InstanceId)
                .ConfigureAwait(false);

            _logger.InstanceDeregistered(_cadenceOptions.InstanceId);
        }
        catch (RedisException ex)
        {
            // Leaving the record behind is harmless: the janitor reaps it once the heartbeat is stale.
            _logger.HeartbeatFailed(ex, _cadenceOptions.InstanceId);
        }
    }

    /// <summary>Writes the heartbeat now.</summary>
    /// <param name="register">True to write the registration details as well as the heartbeat.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal async Task BeatAsync(bool register, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        var now = _clock.UtcNow;
        var instanceId = _cadenceOptions.InstanceId;

        if (register)
        {
            var details = JsonSerializer.Serialize(new StoredInstance
            {
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                AssemblyVersion = typeof(RedisInstanceRegistry).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                StartedAtUtc = RedisValues.Ticks(now),
            });

            await database.HashSetAsync(keys.Instances, instanceId, details).ConfigureAwait(false);
        }

        await database
            .SortedSetAddAsync(keys.Heartbeats, instanceId, RedisValues.Ticks(now))
            .ConfigureAwait(false);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task BeatQuietlyAsync(bool register, CancellationToken cancellationToken)
    {
        try
        {
            await BeatAsync(register, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (RedisException ex)
        {
            // A missed beat is survivable; a registry loop that died on one would not be. The next
            // interval tries again, and the heartbeat timeout is several intervals wide for
            // exactly this.
            _logger.HeartbeatFailed(ex, _cadenceOptions.InstanceId);
        }
    }

    private sealed class StoredInstance
    {
        public string? MachineName { get; init; }

        public int ProcessId { get; init; }

        public string? AssemblyVersion { get; init; }

        public long StartedAtUtc { get; init; }
    }
}
