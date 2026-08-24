using Cadence.Storage.Sql.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Storage.Sql;

/// <summary>
/// Keeps the tables bounded and resolves runs nobody finished.
/// </summary>
/// <remarks>
/// <para>
/// Four passes, all idempotent and all expressed as set operations, which is why no leader election
/// is needed: every instance can run this and the result is the same as one instance running it. That
/// is worth more than the duplicated work it costs, because leader election is a whole distributed
/// systems problem and this is a tidying job.
/// </para>
/// <para>
/// Batched, though. A single delete of a large backlog takes enough row locks to escalate to a table
/// lock, and that table is the one the claim inserts into — so an unbatched janitor would stall
/// scheduling across the cluster while cleaning up after it.
/// </para>
/// <para>
/// Runs on its own timer, never on the tick loop.
/// </para>
/// </remarks>
public sealed class CadenceJanitor : BackgroundService
{
    private readonly SqlDatabase _database;
    private readonly SqlRunHistoryStore _history;
    private readonly SqlStorageOptions _options;
    private readonly ISystemClock _clock;
    private readonly CadenceOptions _cadenceOptions;
    private readonly ILogger<CadenceJanitor> _logger;

    internal CadenceJanitor(
        SqlDatabase database,
        SqlRunHistoryStore history,
        SqlStorageOptions options,
        ISystemClock clock,
        IOptions<CadenceOptions> cadenceOptions,
        ILogger<CadenceJanitor> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cadenceOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _history = history;
        _options = options;
        _clock = clock;
        _cadenceOptions = cadenceOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.JanitorInterval);

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
    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var retention = _cadenceOptions.Retention;
        var batch = _options.JanitorBatchSize;

        // Reap first. A run reaped to Lost becomes eligible for the age purge in the same pass,
        // whereas purging first would leave it Running for another interval.
        var reaped = await _history
            .ReapAbandonedAsync(now - _options.HeartbeatTimeout, now, batch, cancellationToken)
            .ConfigureAwait(false);

        if (reaped > 0)
        {
            _logger.RunsReaped(reaped, _options.HeartbeatTimeout);
        }

        var purged = await _history
            .PurgeByAgeAsync(now - retention.MaxAge, batch, cancellationToken)
            .ConfigureAwait(false);

        var trimmed = await _history
            .TrimPerJobAsync(retention.MaxRunsPerJob, batch, cancellationToken)
            .ConfigureAwait(false);

        var deadInstances = await PurgeDeadInstancesAsync(now, batch, cancellationToken)
            .ConfigureAwait(false);

        _logger.JanitorPass(purged, trimmed, reaped, deadInstances);
    }

    /// <summary>
    /// Removes instance rows long past their heartbeat timeout.
    /// </summary>
    /// <remarks>
    /// Kept well beyond the timeout that reaps runs, so an instance's row outlives the decision that
    /// it was gone. Deleting it at the same moment would leave a reaped run pointing at an instance
    /// nothing can explain, which is exactly the question someone reads history to answer.
    /// </remarks>
    private async Task<int> PurgeDeadInstancesAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var cutoff = now - (_options.HeartbeatTimeout * 10);

        return await _database.ExecuteAsync(
            $"""
            DELETE TOP (@BatchSize)
            FROM {_database.Table("CadenceInstance")}
            WHERE LastHeartbeatUtc < @Cutoff;
            """,
            command =>
            {
                command.Parameters.AddWithValue("@BatchSize", batchSize);
                SqlValues.AddInstant(command, "@Cutoff", cutoff);
            },
            cancellationToken).ConfigureAwait(false);
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
