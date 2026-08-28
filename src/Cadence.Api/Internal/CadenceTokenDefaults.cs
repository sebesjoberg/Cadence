namespace Cadence.Api.Internal;

/// <summary>Names the token scheme uses, kept in one place because both the handler and the policy need them.</summary>
internal static class CadenceTokenDefaults
{
    /// <summary>The authentication scheme name; public as <see cref="CadenceApiDefaults"/>.</summary>
    public const string Scheme = CadenceApiDefaults.AuthenticationScheme;

    /// <summary>Policy for the read endpoints, applied to the whole group.</summary>
    public const string ReadPolicy = "CadenceApi.Read";

    /// <summary>Policy for trigger and pause, applied on top of the group's.</summary>
    public const string OperatePolicy = "CadenceApi.Operate";

    /// <summary>Claim asserting the principal authenticated with a Cadence API token.</summary>
    public const string TokenClaim = "cadence:token";

    /// <summary>Claim naming what kind of principal this is; see <see cref="CadencePrincipal"/>.</summary>
    public const string KindClaim = "cadence:kind";

    /// <summary>Claim carrying the principal's scope.</summary>
    public const string ScopeClaim = "cadence:scope";

    /// <summary>When the provider says the user authenticated, as the OIDC claim of that name.</summary>
    public const string AuthTimeClaim = "auth_time";

    /// <summary>
    /// The provider's session identifier, kept so a remote sign-out can be matched against the
    /// ticket it claims to end.
    /// </summary>
    public const string SessionIdClaim = "sid";
}
