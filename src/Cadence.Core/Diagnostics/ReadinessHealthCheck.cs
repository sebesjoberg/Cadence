using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Diagnostics;

/// <summary>Reports whether boot completed, and how many jobs were registered.</summary>
/// <remarks>
/// Holds a flag and a count, and nothing else. The count is captured at registration rather than
/// read from <see cref="IJobRegistry"/> so that the day the job list becomes store-derived, this
/// probe does not silently acquire I/O while the guarantee it advertises stays green.
/// </remarks>
internal sealed class ReadinessHealthCheck : IHealthCheck
{
    private readonly CadenceReadiness _readiness;
    private readonly int _jobCount;

    /// <summary>Creates the check.</summary>
    /// <param name="readiness">The boot flag.</param>
    /// <param name="jobCount">How many jobs were registered, for the description.</param>
    public ReadinessHealthCheck(CadenceReadiness readiness, int jobCount)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentOutOfRangeException.ThrowIfNegative(jobCount);

        _readiness = readiness;
        _jobCount = jobCount;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_readiness.IsReady
            ? HealthCheckResult.Healthy($"Scheduling {_jobCount} job(s).")
            : HealthCheckResult.Unhealthy("Cadence has not finished starting."));
}
