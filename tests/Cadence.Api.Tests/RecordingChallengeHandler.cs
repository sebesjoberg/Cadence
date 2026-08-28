using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Cadence.Api.Tests;

/// <summary>
/// Stands in for the OIDC handler and reports what a challenge carried, instead of redirecting to a
/// provider. The redirect target a challenge is built with is otherwise unobservable: the real
/// handler seals it into the protected <c>state</c> parameter.
/// </summary>
internal sealed class RecordingChallengeHandler : IAuthenticationHandler
{
    /// <summary>Carries the challenge's <see cref="AuthenticationProperties.RedirectUri"/>.</summary>
    public const string RedirectUriHeader = "X-Cadence-Test-Redirect";

    private HttpContext _context = null!;

    public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    public Task ChallengeAsync(AuthenticationProperties? properties)
    {
        _context.Response.StatusCode = StatusCodes.Status302Found;
        _context.Response.Headers.Location = "https://idp.test/authorize";
        _context.Response.Headers[RedirectUriHeader] = properties?.RedirectUri ?? string.Empty;

        return Task.CompletedTask;
    }

    public Task ForbidAsync(AuthenticationProperties? properties)
    {
        _context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
