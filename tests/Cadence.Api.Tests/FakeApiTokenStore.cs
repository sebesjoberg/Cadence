using Cadence.Storage;

namespace Cadence.Api.Tests;

/// <summary>
/// An in-memory <see cref="IWritableApiTokenStore"/>, keyed by hex digest the way resolution looks a
/// token up. It lets these tests present a stored token without a storage package, and being
/// writable is also what tells <c>AddApi</c> this deployment can issue tokens at all.
/// </summary>
internal sealed class FakeApiTokenStore : IWritableApiTokenStore
{
    private readonly Dictionary<string, ApiTokenInfo> _tokens = [];

    private int _lookups;

    /// <summary>How many times resolution has reached the store, for the shape gate's test.</summary>
    public int Lookups => Volatile.Read(ref _lookups);

    public Task<ApiTokenPrincipal?> FindAsync(byte[] digest, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lookups);

        lock (_tokens)
        {
            var found = _tokens.TryGetValue(Key(digest), out var token) && Resolves(token) ? token : null;

            return Task.FromResult(found is null
                ? null
                : new ApiTokenPrincipal(found.Id, found.Name, found.Fingerprint, found.Scope));
        }
    }

    public Task<ApiTokenInfo> CreateAsync(
        ApiTokenCreation creation,
        byte[] digest,
        CancellationToken cancellationToken)
    {
        var info = new ApiTokenInfo(
            Guid.NewGuid(),
            creation.Name,
            ApiTokenSecret.Fingerprint(digest),
            creation.Scope,
            DateTimeOffset.UtcNow,
            creation.CreatedBySubject,
            creation.CreatedByName,
            creation.ExpiresAtUtc);

        lock (_tokens)
        {
            _tokens[Key(digest)] = info;
        }

        return Task.FromResult(info);
    }

    public Task<IReadOnlyList<ApiTokenInfo>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_tokens)
        {
            return Task.FromResult<IReadOnlyList<ApiTokenInfo>>(
                [.. _tokens.Values.Where(Resolves).OrderByDescending(token => token.CreatedAtUtc)]);
        }
    }

    public Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_tokens)
        {
            var key = _tokens.FirstOrDefault(entry => entry.Value.Id == id).Key;

            return Task.FromResult(key is not null && _tokens.Remove(key));
        }
    }

    private static bool Resolves(ApiTokenInfo token)
        => token.ExpiresAtUtc is null || token.ExpiresAtUtc > DateTimeOffset.UtcNow;

    private static string Key(byte[] digest) => Convert.ToHexStringLower(digest);
}
