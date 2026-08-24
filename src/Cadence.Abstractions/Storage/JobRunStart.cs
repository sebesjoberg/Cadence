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
}
