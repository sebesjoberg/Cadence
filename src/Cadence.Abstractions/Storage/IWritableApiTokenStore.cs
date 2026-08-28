namespace Cadence.Storage;

/// <summary>
/// Creates, lists and revokes API tokens. Present only on a persistent tier.
/// </summary>
/// <remarks>
/// Its absence is the mechanism, not an inconvenience: <c>MapCadenceApi</c> asks the container
/// whether this is registered and leaves the creation routes unmapped when it is not, so a
/// deployment without a storage package answers 404 from routing rather than failing inside a
/// handler that should never have been reachable.
/// </remarks>
public interface IWritableApiTokenStore : IApiTokenStore
{
    /// <summary>Stores a new token and returns what administration should see.</summary>
    /// <param name="creation">Name, scope, expiry and provenance.</param>
    /// <param name="digest">The digest of a secret the caller has already minted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<ApiTokenInfo> CreateAsync(
        ApiTokenCreation creation,
        byte[] digest,
        CancellationToken cancellationToken);

    /// <summary>Every token that has not been revoked and has not expired, newest first.</summary>
    /// <remarks>
    /// Expired tokens are excluded whether or not the tier has removed their records yet. That is the
    /// contract both tiers can keep: expiry is a key time-to-live on one of them, so an expired token
    /// is not there to list, and a tier that filters instead must not show one the other cannot. An
    /// expired token authenticates nobody either way; <see cref="RevokeAsync"/> still reports it as
    /// known for as long as the tier knows the id.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<ApiTokenInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Revokes a token. Takes effect on every instance's next request.</summary>
    /// <param name="id">The token's id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// True when the store knew the id, including a token whose expiry has already passed; false
    /// only when the id is unknown. Tiers where expiry removes the record must not report an
    /// expired-but-listed token as unknown, or the same request answers differently per tier.
    /// </returns>
    Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken);
}
