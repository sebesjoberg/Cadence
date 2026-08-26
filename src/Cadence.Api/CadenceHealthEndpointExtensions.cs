using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace Cadence.Api;

/// <summary>
/// Maps the health endpoints. A convenience — the tags are documented so an app that already maps
/// its own <c>/health</c> can compose them itself.
/// </summary>
public static class CadenceHealthEndpointExtensions
{
    /// <summary>
    /// Maps liveness and readiness, both anonymous.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="livePath">Where liveness answers.</param>
    /// <param name="readyPath">Where readiness answers.</param>
    /// <returns>The route builder, for chaining.</returns>
    /// <exception cref="ArgumentException">A path is blank, or both paths are the same.</exception>
    /// <remarks>
    /// <para>
    /// Anonymous because the kubelet cannot present a token. Storage health is not here: it is mapped
    /// by <see cref="CadenceApiEndpointExtensions.MapCadenceApi"/> behind the gate, because it is for
    /// humans, alerting and the dashboard — never for the kubelet.
    /// </para>
    /// <para>
    /// Each path selects by tag, so composing these by hand is a two-line job: <c>live</c> selects
    /// <c>cadence-live</c>, <c>ready</c> selects <c>cadence-ready</c>, and neither selects the
    /// <c>cadence.storage</c> checks a storage package registers.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapCadenceHealth(
        this IEndpointRouteBuilder endpoints,
        string livePath = "/health/live",
        string readyPath = "/health/ready")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(livePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(readyPath);

        // Two GET endpoints on one route is an AmbiguousMatchException at request time -- on a probe
        // path, in production, long after the deploy that caused it.
        if (string.Equals(livePath, readyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Liveness and readiness cannot share the path '{livePath}'. They answer different " +
                "questions, and mapping both on one route matches neither.",
                nameof(readyPath));
        }

        endpoints.MapHealthChecks(livePath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
        }).AllowAnonymous();

        endpoints.MapHealthChecks(readyPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
        }).AllowAnonymous();

        return endpoints;
    }
}
