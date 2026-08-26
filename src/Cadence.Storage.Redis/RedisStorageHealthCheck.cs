using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Storage.Redis;

/// <summary>
/// Reports whether the Redis tier is reachable — to humans, alerting and the dashboard, never to the
/// kubelet.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HealthStatus.Degraded"/> rather than Unhealthy, deliberately. Every replica shares one
/// Redis, so a check that reports Unhealthy on a blip fails on every replica at once: the dashboard
/// returns 503 during precisely the incident someone opened it to investigate, and liveness tied to
/// the store turns a hiccup into a cluster-wide crash loop.
/// </para>
/// <para>
/// Registered under the name <c>cadence-redis</c> and the tag <c>cadence.storage</c> by
/// <see cref="CadenceRedisStorageExtensions.UseRedisStorage"/>.
/// </para>
/// <para>
/// Cancellation reaches the ping and nothing before it. Connecting has no token to take, so the
/// first probe against a Redis that accepts nothing blocks for the driver's <c>connectTimeout</c>
/// however promptly the caller cancels, and probes arriving meanwhile queue behind the connect gate.
/// Bounded by that timeout rather than unbounded, which is what makes it acceptable here.
/// </para>
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

            // PingAsync takes no token, so the wait is the only place cancellation can be honoured
            // -- and it abandons the ping rather than stopping it. See the remarks for what that
            // leaves uncovered.
            var latency = await database.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy($"Redis answered in {latency.TotalMilliseconds:F0} ms.");
        }
        catch (Exception ex)
        {
            // Everything, cancellation included. An exception escaping here is recorded as Unhealthy
            // by the health check service, which is the one status this check must never produce.
            return HealthCheckResult.Degraded("Redis did not answer.", ex);
        }
    }
}
