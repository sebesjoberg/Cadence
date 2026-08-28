using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Api.Internal;

/// <summary>Sign in, sign out, and who the caller is. Mapped only when OIDC is configured.</summary>
internal static class AuthEndpoints
{
    /// <summary>The <c>prompt</c> value asking the provider to authenticate the user again.</summary>
    private const string LoginPrompt = "login";

    /// <summary>Carries that request from the challenge to the redirect the handler writes.</summary>
    private const string PromptItem = "cadence:prompt";

    /// <summary>
    /// Where a caller re-authenticates, for the refusal that asks them to.
    /// </summary>
    /// <remarks>
    /// <c>prompt=login</c> and not a plain challenge: the freshness rule reads <c>auth_time</c>, which
    /// is when the user authenticated at the provider rather than when the ticket was minted, so a
    /// challenge the provider's live session answers returns the same instant and the same refusal.
    /// </remarks>
    /// <param name="apiPath">The prefix the machine-callable tree is mounted under.</param>
    public static string FreshLoginPath(string apiPath)
        => $"{apiPath}/auth/login?prompt={LoginPrompt}";

    /// <summary>Maps the three sign-in routes.</summary>
    /// <param name="group">A group carrying no policy.</param>
    /// <param name="basePath">The prefix a <c>returnUrl</c> must fall under.</param>
    /// <remarks>
    /// All three are anonymous, and each decides for itself: <c>login</c> answers a caller who has
    /// no ticket yet, <c>logout</c> one whose ticket has expired, and <c>me</c> authenticates the
    /// caller itself. A policy in front would refuse the first two and hand the dashboard a refusal
    /// it cannot act on.
    /// </remarks>
    public static void Map(RouteGroupBuilder group, string basePath)
    {
        // No session header on login: a sign-in is a top-level navigation and cannot carry one. The
        // handshake's own state and nonce cover this route.
        group.MapGet("/login", (string? returnUrl, string? prompt) =>
        {
            var properties = new AuthenticationProperties
            {
                RedirectUri = LocalReturnUrl(returnUrl, basePath),
            };

            // The one value, compared rather than forwarded: this becomes a protocol parameter, and
            // passing whatever a query string carried through to the provider is not what the
            // freshness refusal needs.
            if (string.Equals(prompt, LoginPrompt, StringComparison.Ordinal))
            {
                properties.Items[PromptItem] = LoginPrompt;
            }

            return TypedResults.Challenge(properties, [CadenceApiDefaults.OidcScheme]);
        });

        group.MapPost("/logout", LogoutAsync)
            .AddEndpointFilter<SessionHeaderFilter>()
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/me", MeAsync)
            .AddEndpointFilter<SessionHeaderFilter>()
            .Produces<AuthMeResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.AllowAnonymous();
    }

    /// <summary>
    /// Asks the provider to authenticate the user again, on a challenge that asked for it.
    /// </summary>
    /// <remarks>
    /// Without this the 401 the freshness rule returns has no remedy a caller can reach: following
    /// it re-enters through the provider's live session, which reports the same <c>auth_time</c>.
    /// </remarks>
    /// <param name="context">The authorization request the handler is about to redirect to.</param>
    public static Task RequestFreshSignInAsync(RedirectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Properties.Items.TryGetValue(PromptItem, out var prompt) && prompt is not null)
        {
            context.ProtocolMessage.Prompt = prompt;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Refuses a remote sign-out that does not name the provider session this ticket came from.
    /// </summary>
    /// <remarks>
    /// <c>RemoteSignOutPath</c> is handled inside the authentication middleware, before routing, so
    /// no endpoint filter reaches it and §4.5's session header rule does not apply to it. The
    /// handler's own <c>sid</c> comparison is skipped where the request carries no <c>sid</c>, which
    /// leaves an image tag pointing at this path able to sign an operator out. Both halves are
    /// required here instead: a <c>sid</c>, and one that matches the current ticket.
    /// </remarks>
    /// <param name="context">The sign-out request, as the handler parsed it.</param>
    public static async Task RefuseForgedSignOutAsync(RemoteSignOutContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ProtocolMessage?.Sid is { Length: > 0 } sid)
        {
            var ticket = await context.HttpContext.AuthenticateAsync(CadenceApiDefaults.CookieScheme);

            if (string.Equals(
                ticket.Principal?.FindFirst(CadenceTokenDefaults.SessionIdClaim)?.Value,
                sid,
                StringComparison.Ordinal))
            {
                return;
            }
        }

        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }

    /// <summary>
    /// Names the client on the end-session request, in place of the id_token this ticket cannot hint
    /// with.
    /// </summary>
    /// <remarks>
    /// RP-Initiated Logout 1.0 §2 permits <c>client_id</c> where <c>id_token_hint</c> is absent, and
    /// it is absent on every sign-out Cadence performs: <c>SaveTokens</c> is false, so the ticket
    /// holds no provider tokens. A provider that insists on one of the two answers the *user* an
    /// error page rather than signing them out, which is how Keycloak behaves.
    /// </remarks>
    /// <param name="context">The redirect the handler is about to write.</param>
    public static Task IdentifyClientAsync(RedirectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrEmpty(context.ProtocolMessage.ClientId))
        {
            context.ProtocolMessage.ClientId = context.Options.ClientId;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The <c>returnUrl</c> to come back to, or the base path when the request named none we accept.
    /// </summary>
    private static string LocalReturnUrl(string? returnUrl, string basePath)
    {
        if (returnUrl is null || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        {
            return basePath;
        }

        // A network-path reference passes as a relative URI and reads as a host in a browser.
        if (returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return basePath;
        }

        var boundary = basePath.EndsWith('/') ? basePath : basePath + "/";

        var underBasePath = string.Equals(returnUrl, basePath, StringComparison.Ordinal)
            || returnUrl.StartsWith(boundary, StringComparison.Ordinal);

        return underBasePath && !returnUrl.Split('/', '\\').Contains("..")
            ? returnUrl
            : basePath;
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
        ILoggerFactory loggers)
    {
        // First, and outside the try: every return below then carries the clearing cookie.
        await context.SignOutAsync(CadenceApiDefaults.CookieScheme);

        try
        {
            // Without the provider's leg, the next /auth/login is answered by its still-live session
            // and signs the same user straight back in. Only where it advertises the endpoint: the
            // handler would otherwise redirect to an empty address.
            if (await AdvertisesEndSessionAsync(oidcOptions, context.RequestAborted))
            {
                await context.SignOutAsync(CadenceApiDefaults.OidcScheme);

                // The handler has written its own redirect; Empty leaves it as it stands.
                return TypedResults.Empty;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Safe to swallow anything: the ticket is already cleared, and failing the request would
            // discard that Set-Cookie and leave the caller with no way to sign out at all.
            loggers.CreateLogger("Cadence.Api").ProviderSignOutUnavailable(exception);
        }

        return TypedResults.NoContent();
    }

    private static async Task<bool> AdvertisesEndSessionAsync(
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions, CancellationToken cancellationToken)
    {
        var options = oidcOptions.Get(CadenceApiDefaults.OidcScheme);

        var configuration = options.Configuration
            ?? await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);

        return !string.IsNullOrEmpty(configuration.EndSessionEndpoint);
    }

    private static async Task<Results<JsonHttpResult<AuthMeResponse>, UnauthorizedHttpResult>> MeAsync(
        ClaimsPrincipal user, HttpContext context)
    {
        var principal = await ResolveAsync(user, context);

        if (principal?.FindFirst(CadenceTokenDefaults.KindClaim)?.Value is not { } kind)
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Json(
            new AuthMeResponse(
                kind,
                principal.Identity?.Name,
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                principal.FindFirst(CadenceTokenDefaults.ScopeClaim)?.Value),
            CadenceApiJsonContext.Default.AuthMeResponse);
    }

    /// <summary>
    /// The caller, authenticated here rather than by a policy: this route carries none, so that it
    /// answers the same way whatever the group in front of it requires.
    /// </summary>
    private static async Task<ClaimsPrincipal?> ResolveAsync(ClaimsPrincipal user, HttpContext context)
    {
        // Cadence's own schemes first: a host's default scheme has already filled in the principal
        // parameter, which would name the host's user for a caller holding a Cadence ticket.
        if (await context.AuthenticateAsync(CadenceApiDefaults.CookieScheme) is { Succeeded: true } ticket)
        {
            return ticket.Principal;
        }

        if (await context.AuthenticateAsync(CadenceTokenDefaults.Scheme) is { Succeeded: true } bearer)
        {
            return bearer.Principal;
        }

        return user.Identity?.IsAuthenticated == true ? user : null;
    }
}
