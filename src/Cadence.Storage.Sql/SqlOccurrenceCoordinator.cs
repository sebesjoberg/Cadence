using Cadence.Storage.Sql.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Storage.Sql;

/// <summary>
/// Claims occurrences by inserting the run row, letting a unique index decide the winner.
/// </summary>
/// <remarks>
/// <para>
/// There is no lock primitive here, and that is the point. A lock held for the duration of a run
/// needs a TTL longer than the longest possible run, which is unknowable, which forces lease renewal,
/// which fails under a GC pause or a partition, which needs fencing tokens to recover from safely.
/// An <c>INSERT</c> against <c>UX_CadenceJobRun_Occurrence</c> asks one question instead — has anyone
/// already started this slot? — and once answered it never needs re-asking.
/// </para>
/// <para>
/// Because the claim <em>is</em> the run row, there is also no window in which a slot is taken but
/// unrecorded. A process that dies immediately after claiming leaves a visible row stuck at
/// <see cref="RunStatus.Running"/>, which the janitor later reaps as <see cref="RunStatus.Lost"/> —
/// rather than an invisible gap in the schedule that nothing can explain.
/// </para>
/// </remarks>
public sealed class SqlOccurrenceCoordinator : IOccurrenceCoordinator
{
    /// <summary>
    /// How many times a transient failure is retried before it is allowed to propagate.
    /// </summary>
    /// <remarks>
    /// Deliberately small, and the delays with it. The tick loop awaits this call, so a long retry
    /// budget here delays every other job due in the same tick. A slot missed because the database
    /// was unreachable for a second is recoverable; a tick loop that falls minutes behind is not.
    /// </remarks>
    private const int MaxAttempts = 3;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(100),
    ];

    private readonly SqlDatabase _database;
    private readonly ISystemClock _clock;
    private readonly CadenceOptions _cadenceOptions;
    private readonly ILogger<SqlOccurrenceCoordinator> _logger;

    internal SqlOccurrenceCoordinator(
        SqlDatabase database,
        ISystemClock clock,
        IOptions<CadenceOptions> cadenceOptions,
        ILogger<SqlOccurrenceCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cadenceOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _clock = clock;
        _cadenceOptions = cadenceOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(
        string jobName,
        DateTimeOffset scheduledFor,
        Guid runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await InsertClaimAsync(jobName, scheduledFor, runId, cancellationToken)
                    .ConfigureAwait(false);

                return true;
            }
            catch (SqlException ex) when (SqlErrors.IsUniqueViolation(ex))
            {
                // The slot is taken. Usually by another instance — but possibly by this very call's
                // own earlier attempt, whose commit succeeded and whose acknowledgement was lost.
                // Only the run id can tell those apart, which is why it is assigned by the caller
                // before the first attempt rather than generated after a successful one.
                var ours = await IsHeldByAsync(jobName, scheduledFor, runId, cancellationToken)
                    .ConfigureAwait(false);

                if (ours)
                {
                    _logger.ClaimAlreadyOurs(jobName, scheduledFor);
                }

                return ours;
            }
            catch (SqlException ex) when (SqlErrors.IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger.ClaimRetrying(ex, jobName, scheduledFor, attempt, MaxAttempts);

                await Task.Delay(RetryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);
            }

            // Every other exception propagates, on purpose. Reporting a dead connection as "someone
            // else won" would skip the run with nothing recorded and nobody alerted, which is the
            // worst failure a scheduler can have.
        }
    }

    private async Task InsertClaimAsync(
        string jobName,
        DateTimeOffset scheduledFor,
        Guid runId,
        CancellationToken cancellationToken)
    {
        // Trigger is Schedule unconditionally: only a scheduled occurrence is ever claimed, because
        // only a scheduled occurrence has an instant for two instances to contend over.
        var sql = $"""
            INSERT INTO {_database.Table("CadenceJobRun")}
                (RunId, JobName, ScheduledForUtc, [Trigger], Status, InstanceId, StartedAtUtc)
            VALUES
                (@RunId, @JobName, @ScheduledForUtc, @Trigger, @Status, @InstanceId, @StartedAtUtc);
            """;

        await _database.ExecuteAsync(
            sql,
            command =>
            {
                SqlValues.AddGuid(command, "@RunId", runId);
                SqlValues.AddText(command, "@JobName", jobName, 200);
                SqlValues.AddInstant(command, "@ScheduledForUtc", scheduledFor);
                SqlValues.AddEnum(command, "@Trigger", TriggerKind.Schedule);
                SqlValues.AddEnum(command, "@Status", RunStatus.Running);
                SqlValues.AddText(command, "@InstanceId", _cadenceOptions.InstanceId, 200);
                SqlValues.AddInstant(command, "@StartedAtUtc", _clock.UtcNow);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsHeldByAsync(
        string jobName,
        DateTimeOffset scheduledFor,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT RunId
            FROM {_database.Table("CadenceJobRun")}
            WHERE JobName = @JobName AND ScheduledForUtc = @ScheduledForUtc;
            """;

        var holder = await _database.ScalarAsync<Guid>(
            sql,
            command =>
            {
                SqlValues.AddText(command, "@JobName", jobName, 200);
                SqlValues.AddInstant(command, "@ScheduledForUtc", scheduledFor);
            },
            cancellationToken).ConfigureAwait(false);

        return holder == runId;
    }
}
