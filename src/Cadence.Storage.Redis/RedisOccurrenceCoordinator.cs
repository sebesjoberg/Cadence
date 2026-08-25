using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Cadence.Storage.Redis;

/// <summary>
/// Claims occurrences by writing the run under a key only one caller can create.
/// </summary>
/// <remarks>
/// <para>
/// The obvious Redis coordinator is <c>SET key NX EX 60</c>, and it is subtly wrong for this. A
/// claim that expires is a claim that can be won twice — not within the tick's horizon, but by
/// anything replaying an older occurrence, which is exactly what catch-up after downtime does. The
/// SQL tier has no such hole because its claim is a permanent row, and a tier that quietly differs
/// on when a slot stops being taken is not an alternative to it.
/// </para>
/// <para>
/// So the claim here is permanent too, and the janitor removes it with the run it belongs to. Which
/// it can, because the same script that takes the slot also writes the run — the property the
/// design plan asks of any coordinator: no window in which a slot is taken but unrecorded. A process
/// that dies immediately after claiming leaves a run visibly stuck at <see cref="RunStatus.Running"/>
/// for the janitor to reap, rather than an unexplained gap in the schedule.
/// </para>
/// </remarks>
public sealed class RedisOccurrenceCoordinator : IOccurrenceCoordinator
{
    private readonly RedisConnection _connection;
    private readonly ISystemClock _clock;
    private readonly CadenceOptions _cadenceOptions;

    internal RedisOccurrenceCoordinator(
        RedisConnection connection,
        ISystemClock clock,
        IOptions<CadenceOptions> cadenceOptions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cadenceOptions);

        _connection = connection;
        _clock = clock;
        _cadenceOptions = cadenceOptions.Value;
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(
        string jobName,
        DateTimeOffset scheduledFor,
        Guid runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        cancellationToken.ThrowIfCancellationRequested();

        var keys = _connection.Keys;
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var member = runId.ToString("N");
        var instanceId = _cadenceOptions.InstanceId;
        var startedAt = RedisValues.Ticks(_clock.UtcNow);

        // No retry loop, and no catch. StackExchange.Redis already retries what is worth retrying
        // underneath this, and anything that reaches here is a failure the caller must see: reporting
        // an unreachable Redis as "someone else won" would skip the run with nothing recorded and
        // nobody alerted, which the interface calls the worst failure a scheduler can have.
        var result = await database.ScriptEvaluateAsync(
            Scripts.Claim,
            [
                keys.Occurrence(jobName, scheduledFor),
                keys.Run(runId),
                keys.AllRuns,
                keys.JobRuns(jobName),
                keys.InstanceRuns(instanceId),
                keys.RunningRuns,
                keys.JobNames,
            ],
            [
                member,
                jobName,
                RedisValues.Argument(RedisValues.Ticks(scheduledFor)),
                (int)TriggerKind.Schedule,
                (int)RunStatus.Running,
                instanceId,
                RedisValues.Argument(startedAt),
            ]).ConfigureAwait(false);

        return (long)result == 1;
    }
}
