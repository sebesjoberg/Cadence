using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Api.Internal;

/// <summary>Authenticates <c>Authorization: Bearer</c> against the configured tokens.</summary>
internal sealed class CadenceTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly TokenSet _tokens;

    public CadenceTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TokenSet tokens)
        : base(options, logger, encoder)
        => _tokens = tokens;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values) ||
            !AuthenticationHeaderValue.TryParse(values.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (_tokens.Match(header.Parameter) is not { } fingerprint)
        {
            return Task.FromResult(AuthenticateResult.Fail("The presented token is not configured."));
        }

        // The name is a fingerprint of the token, not the token: it is stable across restarts, so an
        // audit trail attributes a pause to the same caller each time, and it is not a secret.
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, $"token:{fingerprint}"),
                new Claim(CadenceTokenDefaults.TokenClaim, fingerprint),
            ],
            CadenceTokenDefaults.Scheme,
            ClaimTypes.Name,
            roleType: null);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), CadenceTokenDefaults.Scheme)));
    }
}
