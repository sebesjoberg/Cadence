using Cadence.Storage.Sql.Internal;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Storage.Sql;

/// <summary>
/// Reports whether the SQL tier is reachable — to humans, alerting and the dashboard, never to the
/// kubelet.
/// </summary>
/// <remarks>
/// <see cref="HealthStatus.Degraded"/> rather than Unhealthy, deliberately: every replica shares one
/// database, so Unhealthy on a blip fails every replica at once, and liveness tied to the store would
/// turn a hiccup into a crash loop re-running the migrator against a struggling database.
/// </remarks>
internal sealed class SqlStorageHealthCheck : IHealthCheck
{
    private readonly SqlDatabase _database;

    public SqlStorageHealthCheck(SqlDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Reachability, not correctness: needing the schema would report a healthy database as
            // down until the first deploy's migration finishes.
            await _database.ScalarAsync<int>("SELECT 1;", bind: null, cancellationToken)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy("The schedule database answered.");
        }
        catch (Exception ex)
        {
            // Cancellation included: an escaping exception is recorded as Unhealthy, the one status
            // this check must never produce.
            return HealthCheckResult.Degraded("The schedule database did not answer.", ex);
        }
    }
}
