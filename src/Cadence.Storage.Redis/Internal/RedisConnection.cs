using StackExchange.Redis;

namespace Cadence.Storage.Redis.Internal;

/// <summary>
/// Owns the multiplexer and hands out the configured database, connecting on first use.
/// </summary>
/// <remarks>
/// <para>
/// Lazily, and deliberately. Constructing this at registration time would make a Redis that happens
/// to be down at boot into a process that will not start, and the design plan is explicit that a
/// store blip must never stop the whole application from starting. The first operation that needs
/// Redis is the one that fails.
/// </para>
/// <para>
/// One multiplexer per application: StackExchange.Redis pipelines over a single connection by
/// design, and creating more is the standard way to make it slower.
/// </para>
/// </remarks>
internal sealed class RedisConnection : IAsyncDisposable
{
    private readonly RedisStorageOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _multiplexer;

    public RedisConnection(RedisStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        Keys = new RedisKeys(options.KeyPrefix);
    }

    /// <summary>The key layout, prefixed as configured.</summary>
    public RedisKeys Keys { get; }

    /// <summary>Connects if needed and returns the configured database.</summary>
    public async Task<IDatabase> GetDatabaseAsync()
        => (await GetMultiplexerAsync().ConfigureAwait(false)).GetDatabase(_options.Database);

    /// <summary>Connects if needed and returns the subscriber.</summary>
    public async Task<ISubscriber> GetSubscriberAsync()
        => (await GetMultiplexerAsync().ConfigureAwait(false)).GetSubscriber();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync().ConfigureAwait(false);
            _multiplexer = null;
        }

        _gate.Dispose();
    }

    private async Task<ConnectionMultiplexer> GetMultiplexerAsync()
    {
        if (_multiplexer is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            // Re-checked inside the gate: several callers can arrive together on the first tick.
            if (_multiplexer is { } raced)
            {
                return raced;
            }

            var configuration = ConfigurationOptions.Parse(_options.ConnectionString);

            // A failed connection must throw rather than hand back a multiplexer that quietly
            // queues commands. Reporting "someone else won" because Redis was unreachable is the
            // silent skipped run the coordinator contract forbids.
            configuration.AbortOnConnectFail = true;

            _multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
            return _multiplexer;
        }
        finally
        {
            _gate.Release();
        }
    }
}
