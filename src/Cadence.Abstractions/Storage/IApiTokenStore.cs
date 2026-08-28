namespace Cadence.Storage;

/// <summary>
/// Resolves a presented API token. The half every tier has.
/// </summary>
/// <remarks>
/// Split from <see cref="IWritableApiTokenStore"/> so that "this deployment cannot create tokens at
/// runtime" is a fact about which services are registered rather than a flag somebody can
/// misconfigure. The tier with no storage package implements this and not the other, and the
/// creation endpoints are then not mapped at all.
/// </remarks>
public interface IApiTokenStore
{
    /// <summary>
    /// The token matching a digest, or null when none does.
    /// </summary>
    /// <remarks>
    /// Expiry is enforced here, not by the caller: one place, and the only place that can push the
    /// predicate into an index or a key's time-to-live. Implementations must not cache — revocation
    /// is expected to take effect on the next request, on every instance.
    /// </remarks>
    /// <param name="digest">A digest from <see cref="ApiTokenSecret.Digest"/>.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<ApiTokenPrincipal?> FindAsync(byte[] digest, CancellationToken cancellationToken);
}
