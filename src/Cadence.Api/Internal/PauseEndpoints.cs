using System.Security.Claims;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cadence.Api.Internal;

/// <summary>The pause pair — the one schedule-affecting write on the machine-callable tree.</summary>
internal static class PauseEndpoints
{
    /// <summary>Recorded when nothing authenticated the caller, so the audit field is never blank.</summary>
    private const string AnonymousCaller = "api";

    /// <summary>Maps the pause routes onto an already-policied group.</summary>
    /// <param name="group">The group the control surface mounts under.</param>
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapGet("/pause", GetAsync)
            .Produces<PauseResponse>();

        group.MapPut("/pause", SetAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<JsonHttpResult<PauseResponse>> GetAsync(
        IPauseStore pauses,
        CancellationToken cancellationToken) => TypedResults.Json(
            Responses.ToPause(await pauses.GetAsync(cancellationToken)),
            CadenceApiJsonContext.Default.PauseResponse);

    private static async Task<Results<NoContent, JsonHttpResult<ProblemDetails>>> SetAsync(
        PauseRequest request,
        ClaimsPrincipal user,
        IPauseStore pauses,
        CancellationToken cancellationToken)
    {
        // Enum.TryParse also accepts comma-separated flag lists and bare numbers, so the mask check
        // is what keeps a scope like 7 — a bit no member defines — out of the store.
        if (!Enum.TryParse<PauseScope>(request.Scope, ignoreCase: true, out var scope) ||
            (scope & ~PauseScope.All) != 0)
        {
            return ProblemMapper.AsResult(ProblemMapper.InvalidPauseScope(request.Scope));
        }

        // Taken from the principal, never the body: an audit field a caller can write is an audit
        // field a caller can forge.
        var setBy = user.Identity?.Name is { Length: > 0 } name ? name : AnonymousCaller;

        await pauses.SetAsync(scope, request.Reason, setBy, cancellationToken);

        return TypedResults.NoContent();
    }
}
