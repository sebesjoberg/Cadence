using Cadence.Storage.Redis.Internal;

namespace Cadence.Storage.Redis;

/// <summary>
/// Keeps the pause switches in one Redis hash, pushed to other instances on the same channel
/// schedule edits use.
/// </summary>
public sealed class RedisPauseStore : IPauseStore
{
    private readonly RedisConnection _connection;
    private readonly ISystemClock _clock;

    internal RedisPauseStore(RedisConnection connection, ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);

        _connection = connection;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<PauseState> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        var entries = await database.HashGetAllAsync(_connection.Keys.Pause).ConfigureAwait(false);

        if (entries.Length == 0)
        {
            return PauseState.None;
        }

        var fields = entries.ToDictionary(e => (string)e.Name!, e => e.Value, StringComparer.Ordinal);

        return new PauseState
        {
            Scope = fields.TryGetValue("scope", out var scope) && scope.TryParse(out int value)
                ? (PauseScope)value
                : PauseScope.None,
            Reason = Text(fields, "reason"),
            SetBy = Text(fields, "by"),
            SetAtUtc = fields.TryGetValue("at", out var at) && at.TryParse(out long ticks)
                ? RedisValues.FromTicks(ticks)
                : null,
        };
    }

    /// <inheritdoc />
    public async Task<PauseState> SetAsync(
        PauseScope scope,
        string? reason,
        string? setBy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var setAt = _clock.UtcNow;
        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var result = await database.ScriptEvaluateAsync(
            Scripts.SetPause,
            [keys.Pause, keys.ScheduleVersion],
            [
                (int)scope,
                reason ?? string.Empty,
                setBy ?? string.Empty,
                RedisValues.Ticks(setAt),
            ]).ConfigureAwait(false);

        var subscriber = await _connection.GetSubscriberAsync().ConfigureAwait(false);
        await subscriber.PublishAsync(keys.ScheduleChannel, (long)result).ConfigureAwait(false);

        return new PauseState { Scope = scope, Reason = reason, SetBy = setBy, SetAtUtc = setAt };
    }

    // An empty field and an absent one mean the same thing: nobody said.
    private static string? Text(Dictionary<string, StackExchange.Redis.RedisValue> fields, string name)
        => fields.TryGetValue(name, out var value) && !value.IsNullOrEmpty ? (string)value! : null;
}
