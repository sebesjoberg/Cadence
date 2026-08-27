using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Api.Internal;

/// <summary>The storage answer — the half of §13.4 that is for humans rather than the kubelet.</summary>
internal static class HealthEndpoints
{
    /// <summary>Selects the checks the storage packages register.</summary>
    private const string StorageTag = "cadence.storage";

    /// <summary>Maps the storage-health route onto an already-policied group.</summary>
    /// <param name="group">The group the control surface mounts under.</param>
    public static void Map(IEndpointRouteBuilder group)
    {
        // On the group, not the bare endpoint builder: this route returns the last store error,
        // which is operator information, and a route mapped outside the group is an open one.
        group.MapGet("/health/storage", GetAsync)
            .Produces<StorageHealthResponse>();
    }

    private static async Task<JsonHttpResult<StorageHealthResponse>> GetAsync(
        HealthCheckService checks,
        CancellationToken cancellationToken)
    {
        var report = await checks.CheckHealthAsync(
            registration => registration.Tags.Contains(StorageTag), cancellationToken);

        // 200 whatever the report says, with the status in the body. Answering 503 would take the
        // route down during exactly the incident it exists to explain.
        return TypedResults.Json(
            Responses.ToStorageHealth(report), CadenceApiJsonContext.Default.StorageHealthResponse);
    }
}
