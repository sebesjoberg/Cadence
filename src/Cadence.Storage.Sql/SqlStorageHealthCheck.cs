using Cadence.Storage.Sql.Internal;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Storage.Sql;

/// <summary>
/// Reports whether the SQL tier is reachable — to humans, alerting and the dashboard, never to the
/// kubelet.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HealthStatus.Degraded"/> rather than Unhealthy, deliberately. Every replica shares one
/// database, so a check that reports Unhealthy on a blip fails on every replica at once: the
/// dashboard returns 503 during precisely the incident someone opened it to investigate, and
/// liveness tied to the store turns a hiccup into a cluster-wide crash loop, each restart re-running
/// the migrator against a database that is already struggling.
/// </para>
/// <para>
/// Registered under the name <c>cadence-sql</c> and the tag <c>cadence.storage</c> by
/// <see cref="CadenceSqlStorageExtensions.UseSqlStorage"/>.
/// </para>
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
            // Reachability, not correctness: no table, no schema, nothing the migrator has to have
            // finished. A check that needs the schema reports a healthy database as down for as long
            // as the first deploy takes.
            await _database.ScalarAsync<int>("SELECT 1;", bind: null, cancellationToken)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy("The schedule database answered.");
        }
        catch (Exception ex)
        {
            // Everything, cancellation included. An exception escaping here is recorded as Unhealthy
            // by the health check service, which is the one status this check must never produce.
            return HealthCheckResult.Degraded("The schedule database did not answer.", ex);
        }
    }
}
