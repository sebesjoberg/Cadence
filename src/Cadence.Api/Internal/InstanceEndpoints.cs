using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cadence.Api.Internal;

/// <summary>
/// The cluster as the operator tree reports it: every registered process, and how stale a heartbeat
/// may be before one counts as gone.
/// </summary>
internal static class InstanceEndpoints
{
    /// <summary>Maps the instances read onto an already-policied group.</summary>
    /// <param name="group">The group the operator tree mounts under.</param>
    public static void Map(IEndpointRouteBuilder group)
    {
        // The janitor's own timeout, resolved once at map time because it is a singleton: a
        // dashboard marking staleness at a threshold of its own would contradict the reaped runs
        // shown beside it. Only a persistent tier registers JanitorOptions -- a tier that persists
        // nothing runs no janitor, so the defaults are what the number means there.
        var heartbeatTimeout = (group.ServiceProvider.GetService<JanitorOptions>() ?? new JanitorOptions())
            .HeartbeatTimeout;

        group.MapGet(
            "/instances",
            (IInstanceDirectory instances, CancellationToken cancellationToken)
                => ListAsync(instances, heartbeatTimeout, cancellationToken))
            .Produces<InstancesResponse>();
    }

    private static async Task<JsonHttpResult<InstancesResponse>> ListAsync(
        IInstanceDirectory instances,
        TimeSpan heartbeatTimeout,
        CancellationToken cancellationToken)
    {
        // Stale rows included, as the directory returns them: a view that drops the dead instance
        // hides exactly what the reader opened it to see.
        var registered = await instances.GetAllAsync(cancellationToken);

        return TypedResults.Json(
            new InstancesResponse([.. registered.Select(Responses.ToInstance)], heartbeatTimeout),
            CadenceApiJsonContext.Default.InstancesResponse);
    }
}
