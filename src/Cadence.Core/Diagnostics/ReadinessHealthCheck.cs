using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Diagnostics;

/// <summary>Reports whether boot completed and jobs are registered.</summary>
internal sealed class ReadinessHealthCheck : IHealthCheck
{
    private readonly CadenceReadiness _readiness;
    private readonly IJobRegistry _registry;

    /// <summary>Creates the check.</summary>
    /// <param name="readiness">The boot flag.</param>
    /// <param name="registry">The registered jobs, for the count in the description.</param>
    public ReadinessHealthCheck(CadenceReadiness readiness, IJobRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(registry);

        _readiness = readiness;
        _registry = registry;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_readiness.IsReady
            ? HealthCheckResult.Healthy($"Scheduling {_registry.All.Count} job(s).")
            : HealthCheckResult.Unhealthy("Cadence has not finished starting."));
}
