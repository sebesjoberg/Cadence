using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Storage.Redis;

/// <summary>
/// Reports whether the Redis tier is reachable — to humans, alerting and the dashboard, never to the
/// kubelet.
/// </summary>
/// <remarks>
/// <see cref="HealthStatus.Degraded"/> rather than Unhealthy, deliberately: every replica shares one
/// Redis, so Unhealthy on a blip fails every replica at once. Connecting takes no cancellation token,
/// so the first probe against an unreachable Redis blocks for the driver's <c>connectTimeout</c>.
/// </remarks>
internal sealed class RedisStorageHealthCheck : IHealthCheck
{
    private readonly RedisConnection _connection;

    public RedisStorageHealthCheck(RedisConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

            // PingAsync takes no token, so the wait abandons the ping rather than stopping it.
            var latency = await database.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy($"Redis answered in {latency.TotalMilliseconds:F0} ms.");
        }
        catch (Exception ex)
        {
            // Cancellation included: an escaping exception is recorded as Unhealthy, the one status
            // this check must never produce.
            return HealthCheckResult.Degraded("Redis did not answer.", ex);
        }
    }
}
