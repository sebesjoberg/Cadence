using Xunit;

namespace Cadence.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IApiTokenStore"/> must have.
/// </summary>
/// <remarks>
/// Two properties carry the design. Expiry is the store's job, so a caller cannot forget it. And a
/// revocation made through one instance is visible to another on its next call, with no poll and no
/// wait — which is what "nothing is cached" means, stated as a test rather than as a comment.
/// </remarks>
public abstract class ApiTokenStoreConformance
{
    /// <summary>
    /// Creates a store. Called more than once in a test, and every store a single test creates must
    /// share one backing store, so instances genuinely see each other's writes.
    /// </summary>
    protected abstract Task<IApiTokenStore> CreateAsync();

    /// <summary>
    /// Whether this tier implements the administer half. False for the no-storage tier, which says
    /// so rather than throwing from methods it does not have.
    /// </summary>
    protected virtual bool IsWritable => true;

    /// <summary>Moves the tier's clock, for the expiry tests. Default advances nothing.</summary>
    /// <param name="store">The store to age.</param>
    /// <param name="by">How far forward.</param>
    protected virtual Task AdvanceAsync(IApiTokenStore store, TimeSpan by) => Task.CompletedTask;

    private async Task<IWritableApiTokenStore> CreateWritableAsync()
    {
        var store = await CreateAsync();
        Skip.IfNot(IsWritable, "This tier does not implement IWritableApiTokenStore.");
        return Assert.IsAssignableFrom<IWritableApiTokenStore>(store);
    }

    [SkippableFact]
    public async Task AnUnknownDigestResolvesToNothing()
    {
        var store = await CreateAsync();

        Assert.Null(await store.FindAsync(ApiTokenSecret.Digest("nobody-issued-this"), default));
    }

    [SkippableFact]
    public async Task ACreatedTokenResolvesWithItsNameAndScope()
    {
        var store = await CreateWritableAsync();
        var (secret, digest) = ApiTokenSecret.Create();

        var created = await store.CreateAsync(
            new ApiTokenCreation("nightly-report", ApiTokenScope.Read, null, "https://idp|u1", "Ada"),
            digest,
            default);

        var principal = await store.FindAsync(ApiTokenSecret.Digest(secret), default);

        Assert.NotNull(principal);
        Assert.Equal(created.Id, principal.Id);
        Assert.Equal("nightly-report", principal.Name);
        Assert.Equal(ApiTokenScope.Read, principal.Scope);
        Assert.Equal(ApiTokenSecret.Fingerprint(digest), principal.Fingerprint);
    }

    [SkippableFact]
    public async Task ProvenanceRoundTripsIntoTheListing()
    {
        var store = await CreateWritableAsync();
        var (_, digest) = ApiTokenSecret.Create();

        await store.CreateAsync(
            new ApiTokenCreation("deploy", ApiTokenScope.Operate, null, "https://idp|u1", "Ada"),
            digest,
            default);

        var listed = Assert.Single(await store.ListAsync(default));

        Assert.Equal("https://idp|u1", listed.CreatedBySubject);
        Assert.Equal("Ada", listed.CreatedByName);
    }

    [SkippableFact]
    public async Task RevocationIsVisibleToAnotherInstanceImmediately()
    {
        var a = await CreateWritableAsync();
        var b = await CreateAsync();
        var (secret, digest) = ApiTokenSecret.Create();

        var created = await a.CreateAsync(
            new ApiTokenCreation("doomed", ApiTokenScope.Operate, null, null, null), digest, default);

        Assert.NotNull(await b.FindAsync(ApiTokenSecret.Digest(secret), default));

        Assert.True(await a.RevokeAsync(created.Id, default));

        // No poll, no settle, no wait. This assertion is the no-cache decision.
        Assert.Null(await b.FindAsync(ApiTokenSecret.Digest(secret), default));
    }

    [SkippableFact]
    public async Task RevokingAnUnknownIdReportsNothingRemoved()
    {
        var store = await CreateWritableAsync();

        Assert.False(await store.RevokeAsync(Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task AnExpiredTokenDoesNotResolve()
    {
        var store = await CreateWritableAsync();
        var (secret, digest) = ApiTokenSecret.Create();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);

        await store.CreateAsync(
            new ApiTokenCreation("short-lived", ApiTokenScope.Read, expires, null, null),
            digest,
            default);

        Assert.NotNull(await store.FindAsync(ApiTokenSecret.Digest(secret), default));

        await AdvanceAsync(store, TimeSpan.FromMinutes(10));

        Assert.Null(await store.FindAsync(ApiTokenSecret.Digest(secret), default));
    }

    [SkippableFact]
    public async Task RevokingAnExpiredTokenStillReportsItRemoved()
    {
        var store = await CreateWritableAsync();
        var (_, digest) = ApiTokenSecret.Create();

        var created = await store.CreateAsync(
            new ApiTokenCreation(
                "lapsed", ApiTokenScope.Read, DateTimeOffset.UtcNow.AddMinutes(5), null, null),
            digest,
            default);

        await AdvanceAsync(store, TimeSpan.FromMinutes(10));

        // True means the store knew the token, not that it was still resolving. A tier where expiry
        // removes the record rather than failing a predicate must not report the id as unknown, or
        // one revocation request answers 204 on one tier and 404 on another.
        Assert.True(await store.RevokeAsync(created.Id, default));
    }

    [SkippableFact]
    public async Task ListingNeverExposesADigest()
    {
        var store = await CreateWritableAsync();
        var (_, digest) = ApiTokenSecret.Create();

        await store.CreateAsync(
            new ApiTokenCreation("visible", ApiTokenScope.Read, null, null, null), digest, default);

        var listed = Assert.Single(await store.ListAsync(default));

        // The shape has no digest member at all; this asserts the fingerprint is not the whole one.
        Assert.Equal(8, listed.Fingerprint.Length);
    }

    [SkippableFact]
    public async Task AnExpiredTokenLeavesTheListing()
    {
        var store = await CreateWritableAsync();
        var (_, digest) = ApiTokenSecret.Create();

        await store.CreateAsync(
            new ApiTokenCreation(
                "lapsing", ApiTokenScope.Read, DateTimeOffset.UtcNow.AddMinutes(5), null, null),
            digest,
            default);

        Assert.Single(await store.ListAsync(default));

        await AdvanceAsync(store, TimeSpan.FromMinutes(10));

        // The one contract both tiers can keep: expiry as a key time-to-live leaves nothing to list,
        // so a tier that filters instead must not show a row the other cannot -- otherwise the same
        // GET answers differently per tier until a janitor happens to run.
        Assert.Empty(await store.ListAsync(default));
    }

    [SkippableFact]
    public async Task ARevokedTokenLeavesTheListing()
    {
        var store = await CreateWritableAsync();
        var (_, digest) = ApiTokenSecret.Create();

        var created = await store.CreateAsync(
            new ApiTokenCreation("gone", ApiTokenScope.Read, null, null, null), digest, default);

        await store.RevokeAsync(created.Id, default);

        Assert.Empty(await store.ListAsync(default));
    }

    [SkippableFact]
    public async Task TheNoStorageTierHasNoAdministerHalf()
    {
        var store = await CreateAsync();

        Skip.If(IsWritable, "This tier implements the administer half by design.");
        Assert.IsNotAssignableFrom<IWritableApiTokenStore>(store);
    }
}
