using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cadence.Api.Internal;

/// <summary>What a completed handshake becomes: who is admitted, and what rides in the ticket.</summary>
internal static class TicketIdentity
{
    /// <summary>
    /// Admits the user, then rebuilds the identity from an allow-list: subject, name,
    /// <c>auth_time</c>, <c>sid</c> and one value of the configured required claim.
    /// </summary>
    /// <remarks>
    /// A provider token can carry a great deal — group memberships, directory attributes — and every
    /// claim kept here becomes cookie bytes on every subsequent request.
    /// </remarks>
    /// <param name="context">The validated token, as the handler built it.</param>
    /// <param name="options">The configured sign-in settings.</param>
    public static Task BuildAsync(TokenValidatedContext context, CadenceOidcOptions options)
    {
        var provider = context.Principal;
        var subject = provider?.FindFirst("sub")?.Value;
        var required = options.RequiredClaimType;

        if (string.IsNullOrEmpty(subject))
        {
            context.Fail("The provider returned a token with no subject claim.");

            return Task.CompletedTask;
        }

        var admitting = required is null
            ? null
            : Admitting(provider, required, options.RequiredClaimValue);

        if (required is not null && admitting is null)
        {
            context.Fail(
                $"The provider's token carries no '{required}' claim that this deployment admits.");

            return Task.CompletedTask;
        }

        var name = provider?.FindFirst("name")?.Value
            ?? provider?.FindFirst("preferred_username")?.Value
            ?? subject;

        var user = CadencePrincipal.ForUser(subject, name);
        var identity = (ClaimsIdentity)user.Identity!;

        Add(identity, provider?.FindFirst(CadenceTokenDefaults.AuthTimeClaim));

        // Kept for one comparison: AuthEndpoints.RefuseForgedSignOutAsync matches a remote sign-out
        // against the session it names.
        Add(identity, provider?.FindFirst(CadenceTokenDefaults.SessionIdClaim));
        Add(identity, admitting);

        context.Principal = user;

        return Task.CompletedTask;
    }

    /// <summary>Answers a handshake that did not complete, in place of the framework's exception.</summary>
    /// <param name="context">The failure, as the handler reported it.</param>
    public static Task RefuseAsync(RemoteFailureContext context)
    {
        context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Cadence.Api")
            .SignInRefused(context.Failure);

        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }

    /// <summary>The claim admitting this user, or null when none does.</summary>
    /// <remarks>
    /// A null <paramref name="value"/> admits the claim carrying anything. A value is compared
    /// ordinally: a role or group name is not culture-sensitive.
    /// </remarks>
    private static Claim? Admitting(ClaimsPrincipal? provider, string claimType, string? value)
        => value is null
            ? provider?.FindFirst(claimType)
            : provider?.FindAll(claimType)
                .FirstOrDefault(claim => string.Equals(claim.Value, value, StringComparison.Ordinal));

    // Rebuilt rather than carried over, so the provider's issuer and original type stay out of the
    // ticket.
    private static void Add(ClaimsIdentity identity, Claim? claim)
    {
        if (claim is not null)
        {
            identity.AddClaim(new Claim(claim.Type, claim.Value));
        }
    }
}
