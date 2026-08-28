namespace Cadence.Storage;

/// <summary>
/// The no-storage tier's token store: it resolves nothing, because nothing was issued.
/// </summary>
/// <remarks>
/// <para>
/// Tokens from configuration are matched before the store is consulted, so this is genuinely empty
/// rather than merely unimplemented. It exists so the request path takes one non-optional
/// dependency instead of branching on an absent service, and so the conformance suite has a third
/// tier to assert the split against — the absence of the administer half is the mechanism the
/// creation endpoints rely on, which makes it worth a test rather than an assumption.
/// </para>
/// <para>
/// Deliberately not <see cref="IWritableApiTokenStore"/>. Adding it here, even throwing, would
/// mount creation endpoints on a deployment that cannot persist anything.
/// </para>
/// </remarks>
public sealed class ConfiguredApiTokenStore : IApiTokenStore
{
    /// <inheritdoc />
    public Task<ApiTokenPrincipal?> FindAsync(byte[] digest, CancellationToken cancellationToken)
        => Task.FromResult<ApiTokenPrincipal?>(null);
}
