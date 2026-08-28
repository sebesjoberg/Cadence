using System.Security.Claims;
using Cadence.Storage;

namespace Cadence.Api.Internal;

/// <summary>Builds the principals the control surface authenticates, so the claim layout is written once.</summary>
internal static class CadencePrincipal
{
    public const string TokenKind = "token";
    public const string UserKind = "user";

    // Operate: a configured token carries no metadata to say otherwise. Named for its fingerprint,
    // which is all it has; a stored token is named for the token itself, per §3.5.
    public static ClaimsPrincipal ForConfiguredToken(string fingerprint)
        => ForToken($"token:{fingerprint}", fingerprint, ApiTokenScope.Operate);

    public static ClaimsPrincipal ForStoredToken(ApiTokenPrincipal token)
        => ForToken($"token:{token.Name}", token.Fingerprint, token.Scope);

    /// <summary>A signed-in user, which always carries Operate — the surface has no finer grain for one.</summary>
    public static ClaimsPrincipal ForUser(string subject, string name)
        => Build(
            CadenceApiDefaults.CookieScheme,
            UserKind,
            name,
            ApiTokenScope.Operate,
            new Claim(ClaimTypes.NameIdentifier, subject));

    private static ClaimsPrincipal ForToken(string name, string fingerprint, ApiTokenScope scope)
        => Build(
            CadenceTokenDefaults.Scheme,
            TokenKind,
            name,
            scope,
            new Claim(CadenceTokenDefaults.TokenClaim, fingerprint));

    // The scheme the principal actually came from: it is what ClaimsIdentity.AuthenticationType
    // reports, and a cookie-borne user did not arrive on the token scheme.
    private static ClaimsPrincipal Build(
        string scheme, string kind, string name, ApiTokenScope scope, Claim identifying)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, name),
                identifying,
                new Claim(CadenceTokenDefaults.KindClaim, kind),
                new Claim(CadenceTokenDefaults.ScopeClaim, scope.ToString()),
            ],
            scheme,
            ClaimTypes.Name,
            roleType: null));
}
