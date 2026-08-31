namespace Cadence.Api;

/// <summary>Names a host needs in order to compose its own policy over the control surface.</summary>
public static class CadenceApiDefaults
{
    /// <summary>The prefix every Cadence route sits under. Fixed, so the SPA bundle can bake it.</summary>
    public const string BasePath = "/cadence";

    /// <summary>The machine-callable tree, and the sign-in routes.</summary>
    public const string ApiPath = BasePath + "/api";

    /// <summary>The operator tree the dashboard calls.</summary>
    public const string UiPath = BasePath + "/ui";

    /// <summary>Hashed bundle assets.</summary>
    public const string AssetsPath = BasePath + "/assets";

    /// <summary>
    /// The authentication scheme the token handler registers under. A host writing its own policy
    /// must list this scheme, because Cadence deliberately makes it no host's default.
    /// </summary>
    public const string AuthenticationScheme = "CadenceToken";

    /// <summary>
    /// The scheme holding a signed-in user's ticket. Registered only when OIDC is configured, so a
    /// host policy naming it must be applied to a deployment that configures one.
    /// </summary>
    public const string CookieScheme = "CadenceCookie";

    /// <summary>The scheme that performs the OIDC handshake. Registered alongside the cookie.</summary>
    public const string OidcScheme = "CadenceOidc";

    /// <summary>
    /// Header a cookie-authenticated request must carry. A cross-site form cannot set it, and a
    /// cross-origin fetch that does triggers a preflight nothing answers.
    /// </summary>
    public const string SessionHeader = "X-Cadence-Session";
}
