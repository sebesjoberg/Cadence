namespace Cadence.Api.Internal;

/// <summary>Names the token scheme uses, kept in one place because both the handler and the policy need them.</summary>
internal static class CadenceTokenDefaults
{
    /// <summary>The authentication scheme name.</summary>
    public const string Scheme = "CadenceToken";

    /// <summary>The built-in policy name, used when the host names none of its own.</summary>
    public const string Policy = "CadenceApi";

    /// <summary>Claim asserting the principal authenticated with a Cadence API token.</summary>
    public const string TokenClaim = "cadence:token";
}
