using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Diagnostics;

/// <summary>Reports that the process is up. Deliberately knows nothing else.</summary>
internal sealed class LivenessHealthCheck : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(HealthCheckResult.Healthy("The process is running."));
}
