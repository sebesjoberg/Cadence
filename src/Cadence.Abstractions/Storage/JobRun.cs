namespace Cadence.Storage;

/// <summary>A recorded run.</summary>
public sealed record JobRun
{
    /// <summary>Identifies the run.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The job's stable name.</summary>
    public required string JobName { get; init; }

    /// <summary>The occurrence the run belongs to, or null for a non-scheduled trigger.</summary>
    public DateTimeOffset? ScheduledFor { get; init; }

    /// <summary>How the run was started.</summary>
    public required TriggerKind Trigger { get; init; }

    /// <summary>Current status.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>The instance that executed the run.</summary>
    public required string InstanceId { get; init; }

    /// <summary>When execution began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When execution ended, or null while still running.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>How long the run took, or null while still running.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Exception detail for a failed run.</summary>
    public string? Error { get; init; }

    /// <summary>Progress reported by the job.</summary>
    public IReadOnlyList<JobLogEntry> Log { get; init; } = [];
}
