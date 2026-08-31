using Cadence.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Storage;

/// <summary>
/// Keeps a persistent storage tier bounded and resolves runs nobody finished.
/// </summary>
/// <remarks>
/// <para>
/// Six passes, all idempotent and all expressed as set operations, which is why no leader election
/// is needed: every instance can run this and the result is the same as one instance running it.
/// That is worth more than the duplicated work it costs, because leader election is a whole
/// distributed systems problem and this is a tidying job.
/// </para>
/// <para>
/// Batched, though. In SQL a single delete of a large backlog takes enough row locks to escalate to
/// a table lock, and that table is the one the claim inserts into — so an unbatched janitor would
/// stall scheduling across the cluster while cleaning up after it.
/// </para>
/// <para>
/// Lives in Core, and talks to <see cref="IStorageMaintenance"/> rather than to any particular
/// store, because everything above is policy that does not change between a database and a
/// key-value store. Only the operations do. A tier that persists nothing registers no
/// maintenance implementation and therefore never gets one of these. Results are the exception:
/// they are swept through <see cref="IJobResultStore"/> directly, because where the bytes live
/// is a separate decision from where the rows do.
/// </para>
/// <para>
/// Runs on its own timer, never on the tick loop.
/// </para>
/// </remarks>
public sealed class CadenceJanitor : BackgroundService
{
    private readonly IStorageMaintenance _maintenance;
    private readonly IJobResultStore _results;
    private readonly JanitorOptions _options;
    private readonly ISystemClock _clock;
    private readonly CadenceOptions _cadenceOptions;
    private readonly ILogger<CadenceJanitor> _logger;

    /// <summary>Creates the janitor.</summary>
    /// <param name="maintenance">The tier's maintenance operations.</param>
    /// <param name="results">The result store, swept on its own retention.</param>
    /// <param name="options">Interval, batch size and heartbeat timeout.</param>
    /// <param name="clock">Supplies the current instant.</param>
    /// <param name="cadenceOptions">Supplies the retention policy.</param>
    /// <param name="logger">Receives one line per pass.</param>
    public CadenceJanitor(
        IStorageMaintenance maintenance,
        IJobResultStore results,
        JanitorOptions options,
        ISystemClock clock,
        IOptions<CadenceOptions> cadenceOptions,
        ILogger<CadenceJanitor> logger)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cadenceOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _maintenance = maintenance;
        _results = results;
        _options = options;
        _clock = clock;
        _cadenceOptions = cadenceOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval);

        // First pass on the interval, not at startup. A process restarting into a reap it could have
        // waited for is how a rolling deployment marks its own predecessor's in-flight runs as lost.
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // History growing is a nuisance; a dead janitor loop would be a leak. Scheduling is
                // unaffected either way, so this never escalates.
                _logger.JanitorFailed(ex);
            }
        }
    }

    /// <summary>Runs one full pass.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    public async Task RunPassAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var retention = _cadenceOptions.Retention;
        var batch = _options.BatchSize;

        // Reap first. A run reaped to Lost becomes eligible for the age purge in the same pass,
        // whereas purging first would leave it Running for another interval.
        var reaped = await _maintenance
            .ReapAbandonedRunsAsync(now - _options.HeartbeatTimeout, now, batch, cancellationToken)
            .ConfigureAwait(false);

        if (reaped > 0)
        {
            _logger.RunsReaped(reaped, _options.HeartbeatTimeout);
        }

        var purged = await _maintenance
            .PurgeRunsByAgeAsync(now - retention.MaxAge, batch, cancellationToken)
            .ConfigureAwait(false);

        var trimmed = await _maintenance
            .TrimRunsPerJobAsync(retention.MaxRunsPerJob, batch, cancellationToken)
            .ConfigureAwait(false);

        // Far older than the cut-off that reaps runs, so an instance outlives the decision that it
        // was gone: a reaped run pointing at an instance nothing can explain is exactly the question
        // someone reads history to answer.
        var instanceCutoff = now - (_options.HeartbeatTimeout * _options.InstanceRetentionMultiplier);

        var deadInstances = await _maintenance
            .PurgeDeadInstancesAsync(instanceCutoff, batch, cancellationToken)
            .ConfigureAwait(false);

        _logger.JanitorPass(purged, trimmed, reaped, deadInstances);

        // No local containment: this is the least important of the passes, and the loop in
        // ExecuteAsync already contains a failure in any of them without escalating it.
        var purgedTokens = await _maintenance
            .PurgeExpiredApiTokensAsync(now, batch, cancellationToken)
            .ConfigureAwait(false);

        if (purgedTokens > 0)
        {
            _logger.ApiTokensPurged(purgedTokens);
        }

        // Through the result store rather than IStorageMaintenance, because results are pluggable
        // independently of the tier holding history: a deployment can keep rows in SQL and bytes on
        // a filesystem, and one janitor still has to sweep both.
        var purgedResults = await _results
            .PurgeAsync(now, batch, cancellationToken)
            .ConfigureAwait(false);

        if (purgedResults > 0)
        {
            _logger.ResultsPurged(purgedResults);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
