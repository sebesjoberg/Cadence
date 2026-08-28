namespace Cadence.Storage;

/// <summary>
/// The identity a resolved token acts as.
/// </summary>
/// <remarks>
/// No creator fields, and that is the design rather than an omission. A token acts as itself, so a
/// cron job survives its author leaving and never inherits privileges they have lost. Who created it
/// is provenance for administration, which is <see cref="ApiTokenInfo"/>.
/// </remarks>
/// <param name="Id">The token's id.</param>
/// <param name="Name">Operator-supplied name, used as the audit identity.</param>
/// <param name="Fingerprint">First eight hex characters of the digest.</param>
/// <param name="Scope">What it may do.</param>
public sealed record ApiTokenPrincipal(Guid Id, string Name, string Fingerprint, ApiTokenScope Scope);

/// <summary>
/// What administration sees. Never carries the digest, and never the secret.
/// </summary>
/// <param name="Id">The token's id.</param>
/// <param name="Name">Operator-supplied name.</param>
/// <param name="Fingerprint">First eight hex characters of the digest.</param>
/// <param name="Scope">What it may do.</param>
/// <param name="CreatedAtUtc">When it was created.</param>
/// <param name="CreatedBySubject">Issuer and subject of the creator, or null.</param>
/// <param name="CreatedByName">Display name of the creator, or null.</param>
/// <param name="ExpiresAtUtc">When it stops resolving, or null for never.</param>
public sealed record ApiTokenInfo(
    Guid Id,
    string Name,
    string Fingerprint,
    ApiTokenScope Scope,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBySubject,
    string? CreatedByName,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>What a caller asks for when creating a token.</summary>
/// <param name="Name">Operator-supplied name.</param>
/// <param name="Scope">What it may do.</param>
/// <param name="ExpiresAtUtc">When it should stop resolving, or null for never.</param>
/// <param name="CreatedBySubject">Issuer and subject of the creator.</param>
/// <param name="CreatedByName">Display name of the creator.</param>
public sealed record ApiTokenCreation(
    string Name,
    ApiTokenScope Scope,
    DateTimeOffset? ExpiresAtUtc,
    string? CreatedBySubject,
    string? CreatedByName);
