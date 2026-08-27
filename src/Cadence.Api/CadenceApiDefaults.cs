namespace Cadence.Api;

/// <summary>Names a host needs in order to compose its own policy over the control surface.</summary>
public static class CadenceApiDefaults
{
    /// <summary>
    /// The authentication scheme the token handler registers under. A host writing its own policy
    /// must list this scheme, because Cadence deliberately makes it no host's default.
    /// </summary>
    public const string AuthenticationScheme = "CadenceToken";
}
