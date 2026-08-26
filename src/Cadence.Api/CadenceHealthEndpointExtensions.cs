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
    // Written by Cadence.Core's AddCadence and read here. Namespaced so a host check tagged
    // "live" or "ready" -- the ASP.NET Core documentation's own convention -- joins neither
    // probe, and cannot turn a storage blip into a 503 on every replica at once.
    private const string LiveTag = "cadence.live";
    private const string ReadyTag = "cadence.ready";

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
    /// Each path selects by tag, so composing these by hand is a two-line job: the liveness path
    /// selects <c>cadence.live</c>, the readiness path selects <c>cadence.ready</c>, and neither
    /// selects the <c>cadence.storage</c> checks a storage package registers. All three are
    /// namespaced, so a host check tagged <c>live</c> or <c>ready</c> joins neither probe.
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
            Predicate = registration => registration.Tags.Contains(LiveTag),
        }).AllowAnonymous();

        endpoints.MapHealthChecks(readyPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
        }).AllowAnonymous();

        return endpoints;
    }
}
