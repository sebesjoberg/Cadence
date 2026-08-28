using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Cadence.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Api.Internal;

/// <summary>Authenticates <c>Authorization: Bearer</c> against the configured tokens, then the store.</summary>
internal sealed class CadenceTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly TokenSet _tokens;
    private readonly IApiTokenStore _store;

    public CadenceTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TokenSet tokens,
        IApiTokenStore store)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _store = store;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values) ||
            !AuthenticationHeaderValue.TryParse(values.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return AuthenticateResult.NoResult();
        }

        if (_tokens.Match(header.Parameter) is { } fingerprint)
        {
            return Success(CadencePrincipal.ForConfiguredToken(fingerprint));
        }

        // A shape the store could never have issued is refused here rather than there: an
        // unauthenticated caller would otherwise cost one seek and one pooled connection per request.
        // A configured token is matched above, so its own format is not constrained by this.
        if (!ApiTokenSecret.HasSecretShape(header.Parameter))
        {
            return AuthenticateResult.Fail("The presented token was not minted by Cadence.");
        }

        // Asked on every request that got this far, and nothing is cached: a revoked token has to
        // stop working on the next call, on every instance.
        var stored = await _store.FindAsync(
            ApiTokenSecret.Digest(header.Parameter), Context.RequestAborted);

        return stored is null
            ? AuthenticateResult.Fail("The presented token is neither configured nor stored.")
            : Success(CadencePrincipal.ForStoredToken(stored));
    }

    private static AuthenticateResult Success(ClaimsPrincipal principal)
        => AuthenticateResult.Success(new AuthenticationTicket(principal, CadenceTokenDefaults.Scheme));
}
