using Microsoft.AspNetCore.Http;

namespace Cadence.Api.Internal;

/// <summary>
/// §13.2's dividing line: a token can start work and stop work, and only a person can change when
/// work happens.
/// </summary>
/// <remarks>
/// Requires a user principal, not merely one that is not a token, so an anonymous caller --
/// <c>AllowUnauthenticated</c>, no principal at all -- is refused the same way. One filter, shared
/// by every route that draws the line, so the rule cannot drift between them.
/// </remarks>
internal sealed class UserPrincipalFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
        => context.HttpContext.User.FindFirst(CadenceTokenDefaults.KindClaim)?.Value
                == CadencePrincipal.UserKind
            ? await next(context)
            : TypedResults.StatusCode(StatusCodes.Status403Forbidden);
}
