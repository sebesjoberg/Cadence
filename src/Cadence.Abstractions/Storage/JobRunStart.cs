namespace Cadence.Storage;

/// <summary>Identity and timing of a run that is about to begin.</summary>
public sealed record JobRunStart
{
    /// <summary>Pre-assigned run id, so callers can correlate before the write completes.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The job's stable name.</summary>
    public required string JobName { get; init; }

    /// <summary>The occurrence this run belongs to, or null for a non-scheduled trigger.</summary>
    public DateTimeOffset? ScheduledFor { get; init; }

    /// <summary>How the run was started.</summary>
    public required TriggerKind Trigger { get; init; }

    /// <summary>The instance executing the run.</summary>
    public required string InstanceId { get; init; }

    /// <summary>When execution began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// A key at most one running run may hold, or null for a run that excludes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how <see cref="OverlapPolicy.Skip"/> becomes strict across a cluster rather than
    /// only within one process. The executor sets it to the job name; the store enforces that no
    /// two runs hold the same key at once, and <see cref="IRunHistoryStore.StartAsync"/> answers
    /// null when another run already does.
    /// </para>
    /// <para>
    /// It is carried on the run rather than held as a lock beside it for the same reason the
    /// occurrence claim is — see <see cref="IOccurrenceCoordinator"/>. A separate lock would need a
    /// TTL longer than the longest possible run, and would leave a window where the key is held but
    /// no run records who holds it. Because the key lives on the row, a process that dies holding
    /// one leaves visible evidence, and the janitor's existing reap is what releases it.
    /// </para>
    /// </remarks>
    public string? ExclusiveKey { get; init; }
}
