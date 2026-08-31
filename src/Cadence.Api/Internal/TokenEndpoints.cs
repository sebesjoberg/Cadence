using System.Globalization;
using System.Security.Claims;
using Cadence.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

// Microsoft.AspNetCore.Authentication, imported above, has an obsolete type of the same name.
using ISystemClock = Cadence.ISystemClock;

namespace Cadence.Api.Internal;

/// <summary>Create, list and revoke. Mapped only when a store can persist a token.</summary>
internal static class TokenEndpoints
{
    private const int MaxNameLength = 200;

    /// <summary>Maps the token routes onto an already-policied group.</summary>
    /// <param name="group">The group the control surface mounts under.</param>
    /// <param name="requireUserPrincipal">
    /// Whether the tree requires a user principal on top of the group's own policy. False under a
    /// host-named policy, which governs alone -- the same rule <c>requireOperate</c> already follows.
    /// Reaching this at all under such a policy takes the host's explicit opt-in; see
    /// <c>CadenceApiOptions.AllowTokenAdministrationUnderHostPolicy</c>.
    /// </param>
    public static void Map(IEndpointRouteBuilder group, bool requireUserPrincipal)
    {
        var tokens = group.MapGroup("/tokens");

        if (requireUserPrincipal)
        {
            // Administration is a human act, and the schedule write is another: both draw the line
            // with the same filter.
            tokens.AddEndpointFilter<UserPrincipalFilter>()
                .WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(void)));
        }

        tokens.MapPost("", CreateAsync)
            .Produces<ApiTokenCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        tokens.MapGet("", ListAsync)
            .Produces<IReadOnlyList<ApiTokenResponse>>();

        tokens.MapDelete("/{id:guid}", RevokeAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Results<JsonHttpResult<ApiTokenCreatedResponse>, JsonHttpResult<ProblemDetails>>> CreateAsync(
        ApiTokenRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        IWritableApiTokenStore store,
        IOptions<CadenceApiOptions> options,
        ISystemClock clock,
        CancellationToken cancellationToken)
    {
        var oidc = options.Value.Oidc;

        if (oidc.IsConfigured && await IsStaleAsync(context, oidc, clock))
        {
            // 401 and not 403: the fix is one redirect, and a 403 would tell the dashboard the
            // situation is unfixable. The header names the scheme to re-challenge, and the detail
            // names the route that re-authenticates rather than re-entering a live session.
            context.Response.Headers.WWWAuthenticate = CadenceApiDefaults.CookieScheme;

            return ProblemMapper.AsResult(ProblemMapper.StaleSession(
                oidc.TokenCreationMaxAge, AuthEndpoints.FreshLoginPath(CadenceApiDefaults.ApiPath)));
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameLength)
        {
            return ProblemMapper.AsResult(ProblemMapper.InvalidTokenName(request.Name));
        }

        if (!Enum.TryParse<ApiTokenScope>(request.Scope, ignoreCase: true, out var scope) ||
            scope is not (ApiTokenScope.Read or ApiTokenScope.Operate))
        {
            return ProblemMapper.AsResult(ProblemMapper.InvalidTokenScope(request.Scope));
        }

        if (request.ExpiresAtUtc is { } expiresAt && expiresAt <= clock.UtcNow)
        {
            return ProblemMapper.AsResult(ProblemMapper.InvalidTokenExpiry(expiresAt));
        }

        var (secret, digest) = ApiTokenSecret.Create();

        // Taken from the principal, never the body: an audit field a caller can write is an audit
        // field a caller can forge.
        var creation = new ApiTokenCreation(
            request.Name,
            scope,
            request.ExpiresAtUtc,
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            user.Identity?.Name);

        var created = await store.CreateAsync(creation, digest, cancellationToken);

        return TypedResults.Json(
            Responses.ToCreatedToken(created, secret),
            CadenceApiJsonContext.Default.ApiTokenCreatedResponse,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<bool> IsStaleAsync(
        HttpContext context, CadenceOidcOptions oidc, ISystemClock clock)
    {
        var ticket = await context.AuthenticateAsync(CadenceApiDefaults.CookieScheme);

        if (!ticket.Succeeded)
        {
            // No ticket of ours: whatever else authenticated this caller owns the rule.
            return false;
        }

        // The provider's own instant first: it is what the user has to move by authenticating again,
        // and it survives a ticket reissued from a live provider session. Falling back to when the
        // ticket was minted covers a provider that sends no auth_time.
        var authenticatedAt = AuthTime(ticket.Principal) ?? ticket.Properties?.IssuedUtc;

        return authenticatedAt is not { } instant || clock.UtcNow - instant > oidc.TokenCreationMaxAge;
    }

    private static DateTimeOffset? AuthTime(ClaimsPrincipal? principal)
        => long.TryParse(
            principal?.FindFirst(CadenceTokenDefaults.AuthTimeClaim)?.Value,
            CultureInfo.InvariantCulture,
            out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

    private static async Task<JsonHttpResult<IReadOnlyList<ApiTokenResponse>>> ListAsync(
        IWritableApiTokenStore store,
        CancellationToken cancellationToken)
    {
        var tokens = await store.ListAsync(cancellationToken);

        return TypedResults.Json<IReadOnlyList<ApiTokenResponse>>(
            [.. tokens.Select(Responses.ToApiToken)],
            CadenceApiJsonContext.Default.IReadOnlyListApiTokenResponse);
    }

    private static async Task<Results<NoContent, JsonHttpResult<ProblemDetails>>> RevokeAsync(
        Guid id,
        IWritableApiTokenStore store,
        CancellationToken cancellationToken) => await store.RevokeAsync(id, cancellationToken)
            ? TypedResults.NoContent()
            : ProblemMapper.AsResult(ProblemMapper.TokenNotFound(id));
}
