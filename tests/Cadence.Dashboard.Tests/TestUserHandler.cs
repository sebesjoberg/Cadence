using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Dashboard.Tests;

/// <summary>
/// Mints a user principal from a header instead of a real sign-in, so a test can write the host
/// policy an application with its own identity provider would write.
/// </summary>
internal sealed class TestUserHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Cadence.Tests.User";
    public const string HeaderName = "X-Cadence-Test-User";

    /// <summary>The claims a user principal carries, as <c>CadencePrincipal.ForUser</c> builds them.</summary>
    /// <param name="subject">The user's subject.</param>
    /// <param name="name">The user's display name.</param>
    /// <param name="authenticationType">The scheme the identity claims to come from.</param>
    public static ClaimsIdentity UserIdentity(string subject, string name, string authenticationType)
        => new(
            [
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim("cadence:kind", "user"),
                new Claim("cadence:scope", "Operate"),
            ],
            authenticationType,
            ClaimTypes.Name,
            roleType: null);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var parts = values.ToString().Split('|', 2);
        var subject = parts[0];
        var name = parts.Length > 1 ? parts[1] : subject;

        var principal = new ClaimsPrincipal(UserIdentity(subject, name, SchemeName));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
