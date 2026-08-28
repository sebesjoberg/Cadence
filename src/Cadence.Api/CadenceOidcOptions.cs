namespace Cadence.Api;

/// <summary>
/// How people sign in: server-side OIDC, with the resulting ticket held in an encrypted cookie.
/// Nothing here is required — leaving <see cref="Authority"/> or <see cref="ClientId"/> unset
/// registers neither scheme, and the surface stays token-only.
/// </summary>
/// <remarks>
/// Bound from <c>Cadence:Api:Oidc:*</c>, with <c>CADENCE_OIDC_AUTHORITY</c>,
/// <c>CADENCE_OIDC_CLIENT_ID</c> and <c>CADENCE_OIDC_CLIENT_SECRET</c> overriding it for the same
/// compose-file reason <c>CADENCE_API_TOKEN</c> exists.
/// </remarks>
public sealed class CadenceOidcOptions
{
    /// <summary>The provider's issuer URL, from which its discovery document is read.</summary>
    public string? Authority { get; set; }

    /// <summary>The client registration Cadence authenticates as.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret, for a confidential client. Omitted for a public one.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Whether the provider's discovery document must be served over HTTPS. True refuses to read one
    /// over plain HTTP, and is what a deployment should leave it at.
    /// </summary>
    /// <remarks>
    /// False exists for a provider running in a container on a developer's machine — Keycloak in
    /// <c>start-dev</c>, and every other dev-mode image, serves HTTP and nothing else. It relaxes the
    /// transport for the metadata and token requests Cadence makes, not for the browser's leg, and
    /// the ticket cookie stays <c>Secure</c> either way.
    /// </remarks>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Scopes requested at sign-in. Configuring any replaces this list.</summary>
    public IList<string> Scopes { get; } = ["openid", "profile"];

    /// <summary>
    /// Claim a principal must carry to reach the dashboard at all, enforced at sign-in. Null means
    /// any user the provider authenticates, and <c>MapCadenceApi()</c> warns about it at map time.
    /// Re-checking a claim the provider revokes later is not handled — see the milestone's later
    /// tasks.
    /// </summary>
    public string? RequiredClaimType { get; set; }

    /// <summary>
    /// The value <see cref="RequiredClaimType"/> must carry. Null means the claim must be present
    /// with any value.
    /// </summary>
    public string? RequiredClaimValue { get; set; }

    /// <summary>
    /// How long a ticket lasts. Absolute, never extended by use: every user returns through the
    /// provider on a bounded schedule, and that return is where a disabled account is noticed.
    /// </summary>
    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>How recently a user must have authenticated to mint an API token.</summary>
    public TimeSpan TokenCreationMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether Cadence persists Data Protection keys into the configured storage tier. False leaves
    /// it entirely to the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When true and a storage tier is configured, the key ring goes where schedules and run history
    /// go, under the Data Protection application name <c>Cadence</c>. A ticket minted on one replica
    /// is then readable on the next, and survives a restart. With no storage tier there is nowhere
    /// to put it, and the host's own arrangement stands either way when this is false — by default
    /// that is the file system, or memory where there is no writable key directory, and keys that
    /// live in memory invalidate every ticket on every restart.
    /// </para>
    /// <para>
    /// It reaches the ticket cookie alone: the key ring is built in a container of its own and
    /// attached to the cookie scheme's own options, so the host's app-wide Data Protection — its
    /// application discriminator, its own key ring, and everything it has already protected — is
    /// left exactly as the host arranged it. What the host set for its keys is carried over, so
    /// <c>ProtectKeysWithCertificate</c> still composes with this. Certificate protection is what
    /// that covers: a key-encryption arrangement whose decryption resolves further services from the
    /// host's own container, as Azure Key Vault's does, is not carried across, and only the host's
    /// own key ring is promised to work with it.
    /// </para>
    /// <para>
    /// Keys are stored unencrypted at rest, protected by the store's own access controls — the same
    /// ones already trusted with schedules and run history. <c>ProtectKeysWithCertificate</c> is the
    /// documented step for a deployment that wants more, and it composes with this.
    /// </para>
    /// </remarks>
    public bool ManageDataProtectionKeys { get; set; } = true;

    /// <summary>Whether a provider is configured well enough to attempt a handshake.</summary>
    internal bool IsConfigured => !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(ClientId);
}
