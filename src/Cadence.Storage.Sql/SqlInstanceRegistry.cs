using System.Reflection;
using Cadence.Storage.Sql.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Storage.Sql;

/// <summary>
/// Keeps a row saying this instance is alive, refreshed on an interval.
/// </summary>
/// <remarks>
/// <para>
/// This exists for one reason: to tell an instance that crashed apart from an instance that is
/// merely slow. A run left at <see cref="RunStatus.Running"/> could mean either, and the difference
/// matters — reaping a live instance's run would report a failure that never happened, while never
/// reaping a dead one leaves history claiming work is in progress forever. The heartbeat is the only
/// evidence that separates the two.
/// </para>
/// <para>
/// It runs as its own background service, never on the tick loop. A stalled heartbeat write must not
/// be able to delay dispatching work that is due.
/// </para>
/// </remarks>
public sealed class SqlInstanceRegistry : BackgroundService
{
    private readonly SqlDatabase _database;
    private readonly SqlStorageOptions _options;
    private readonly ISystemClock _clock;
    private readonly CadenceOptions _cadenceOptions;
    private readonly ILogger<SqlInstanceRegistry> _logger;

    internal SqlInstanceRegistry(
        SqlDatabase database,
        SqlStorageOptions options,
        ISystemClock clock,
        IOptions<CadenceOptions> cadenceOptions,
        ILogger<SqlInstanceRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cadenceOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _clock = clock;
        _cadenceOptions = cadenceOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Registered before the first wait, so an instance is visible from the moment it starts
        // rather than one interval later -- otherwise a janitor pass in that window would see runs
        // owned by an instance it has no record of.
        await BeatQuietlyAsync(register: true, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.HeartbeatInterval);

        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            await BeatQuietlyAsync(register: false, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // A graceful stop removes the row, so the janitor does not have to wait out the heartbeat
        // timeout to reap anything this instance abandoned. On an ungraceful stop the row stays and
        // the timeout does the work instead.
        try
        {
            await _database.ExecuteAsync(
                $"DELETE FROM {_database.Table("CadenceInstance")} WHERE InstanceId = @InstanceId;",
                command => SqlValues.AddText(command, "@InstanceId", _cadenceOptions.InstanceId, 200),
                CancellationToken.None).ConfigureAwait(false);

            _logger.InstanceDeregistered(_cadenceOptions.InstanceId);
        }
        catch (Exception ex)
        {
            // Leaving the row behind is harmless: the janitor reaps it once the heartbeat goes stale.
            _logger.HeartbeatFailed(ex, _cadenceOptions.InstanceId);
        }
    }

    /// <summary>Writes the heartbeat row now.</summary>
    /// <param name="register">True to fill in the static columns as well as the heartbeat.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal async Task BeatAsync(bool register, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // Update-then-insert rather than MERGE: MERGE on a single key under concurrency has known
        // deadlock and unique-violation edge cases, and there is nothing here that needs it.
        var sql = $"""
            UPDATE {_database.Table("CadenceInstance")}
            SET LastHeartbeatUtc = @Now
            WHERE InstanceId = @InstanceId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO {_database.Table("CadenceInstance")}
                    (InstanceId, MachineName, ProcessId, AssemblyVersion, StartedAtUtc, LastHeartbeatUtc)
                VALUES
                    (@InstanceId, @MachineName, @ProcessId, @AssemblyVersion, @Now, @Now);
            END
            """;

        await _database.ExecuteAsync(
            sql,
            command =>
            {
                SqlValues.AddText(command, "@InstanceId", _cadenceOptions.InstanceId, 200);
                SqlValues.AddText(command, "@MachineName", Environment.MachineName, 200);
                command.Parameters.AddWithValue("@ProcessId", Environment.ProcessId);
                SqlValues.AddText(command, "@AssemblyVersion", AssemblyVersion, 50);
                SqlValues.AddInstant(command, "@Now", now);
            },
            cancellationToken).ConfigureAwait(false);

        if (register)
        {
            _logger.InstanceRegistered(_cadenceOptions.InstanceId);
        }
    }

    private async Task BeatQuietlyAsync(bool register, CancellationToken cancellationToken)
    {
        try
        {
            await BeatAsync(register, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // Never fatal, and never allowed to stop the loop. The next beat may well succeed, and
            // if none does, the janitor reaping this instance's runs is the correct outcome.
            _logger.HeartbeatFailed(ex, _cadenceOptions.InstanceId);
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

    private static string? AssemblyVersion { get; } =
        typeof(SqlInstanceRegistry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
