using Cadence.Storage.Redis.Internal;
using StackExchange.Redis;

namespace Cadence.Storage.Redis;

/// <summary>
/// Keeps API tokens in one hash per token, keyed by digest, with expiry carried by the key's TTL.
/// </summary>
/// <remarks>
/// An expired token is not filtered out of a query here, it stops existing — so there is no purge
/// pass and no predicate to forget. The id-to-digest index exists so administration never scans.
/// </remarks>
public sealed class RedisApiTokenStore : IWritableApiTokenStore
{
    private readonly RedisConnection _connection;
    private readonly ISystemClock _clock;

    internal RedisApiTokenStore(RedisConnection connection, ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);

        _connection = connection;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ApiTokenPrincipal?> FindAsync(
        byte[] digest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        cancellationToken.ThrowIfCancellationRequested();

        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        var key = _connection.Keys.Token(Convert.ToHexStringLower(digest));

        var fields = await database
            .HashGetAsync(key, ["id", "name", "fp", "scope"])
            .ConfigureAwait(false);

        if (fields[0].IsNullOrEmpty)
        {
            return null;
        }

        return new ApiTokenPrincipal(
            Guid.Parse((string)fields[0]!),
            (string)fields[1]!,
            (string)fields[2]!,
            (ApiTokenScope)(int)fields[3]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// An expiry of <see cref="DateTimeOffset.MinValue"/> is indistinguishable from no expiry here:
    /// the sentinel for "never expires" is a tick count of zero, and that is what MinValue converts
    /// to, so the key is written with no time-to-live. Unreachable through HTTP, where an expiry not
    /// in the future is refused before the store is called, and worth knowing for a caller holding
    /// the store directly.
    /// </remarks>
    public async Task<ApiTokenInfo> CreateAsync(
        ApiTokenCreation creation,
        byte[] digest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(digest);
        cancellationToken.ThrowIfCancellationRequested();

        var info = new ApiTokenInfo(
            Guid.NewGuid(),
            creation.Name,
            ApiTokenSecret.Fingerprint(digest),
            creation.Scope,
            _clock.UtcNow,
            creation.CreatedBySubject,
            creation.CreatedByName,
            creation.ExpiresAtUtc);

        var digestHex = Convert.ToHexStringLower(digest);
        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        var key = keys.Token(digestHex);

        // One script: the hash, its TTL and the index entry cannot land apart. What each of them
        // alone would cost is in Scripts.CreateToken.
        await database.ScriptEvaluateAsync(
            Scripts.CreateToken,
            [key, keys.Tokens],
            [
                info.Id.ToString("N"),
                info.Name,
                info.Fingerprint,
                (int)info.Scope,
                RedisValues.Ticks(info.CreatedAtUtc),
                info.CreatedBySubject ?? string.Empty,
                info.CreatedByName ?? string.Empty,
                info.ExpiresAtUtc is { } expires ? RedisValues.Ticks(expires) : 0L,
                info.ExpiresAtUtc?.ToUnixTimeMilliseconds() ?? 0L,
                digestHex,
            ]).ConfigureAwait(false);

        return info;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiTokenInfo>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        var index = await database.HashGetAllAsync(keys.Tokens).ConfigureAwait(false);

        var results = new List<ApiTokenInfo>(index.Length);

        foreach (var entry in index)
        {
            var fields = await database
                .HashGetAllAsync(keys.Token((string)entry.Value!))
                .ConfigureAwait(false);

            if (fields.Length == 0)
            {
                // The TTL dropped the token but not the index entry. Tidy it on the way past rather
                // than leaving a dangling id behind every expired token.
                await database.HashDeleteAsync(keys.Tokens, entry.Name).ConfigureAwait(false);
                continue;
            }

            results.Add(ToInfo(fields));
        }

        return [.. results.OrderByDescending(info => info.CreatedAtUtc)];
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        var field = id.ToString("N");

        var digestHex = await database.HashGetAsync(keys.Tokens, field).ConfigureAwait(false);

        if (digestHex.IsNullOrEmpty)
        {
            return false;
        }

        await database.KeyDeleteAsync(keys.Token((string)digestHex!)).ConfigureAwait(false);

        // The index entry is what "known" means here, not the hash: a token whose TTL already took
        // the hash was still listed, so revoking it reports true rather than an unknown id.
        return await database.HashDeleteAsync(keys.Tokens, field).ConfigureAwait(false);
    }

    private static ApiTokenInfo ToInfo(HashEntry[] entries)
    {
        var fields = entries.ToDictionary(e => (string)e.Name!, e => e.Value, StringComparer.Ordinal);
        var expires = (long)fields["expires"];

        return new ApiTokenInfo(
            Guid.Parse((string)fields["id"]!),
            (string)fields["name"]!,
            (string)fields["fp"]!,
            (ApiTokenScope)(int)fields["scope"],
            RedisValues.FromTicks((long)fields["created"]),
            Text(fields, "sub"),
            Text(fields, "by"),
            expires == 0 ? null : RedisValues.FromTicks(expires));
    }

    // An empty field and an absent one mean the same thing: nobody said.
    private static string? Text(Dictionary<string, RedisValue> fields, string name)
        => fields.TryGetValue(name, out var value) && !value.IsNullOrEmpty ? (string)value! : null;
}
