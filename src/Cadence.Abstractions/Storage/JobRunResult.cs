namespace Cadence.Storage;

/// <summary>The outcome of a run, written once when it ends.</summary>
public sealed record JobRunResult
{
    /// <summary>How the run ended.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>How long it ran.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>When it ended.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Exception detail for a failed run, otherwise null.</summary>
    public string? Error { get; init; }

    /// <summary>Creates a success result.</summary>
    /// <param name="duration">How long the run took.</param>
    /// <param name="completedAt">When it finished.</param>
    public static JobRunResult Success(TimeSpan duration, DateTimeOffset completedAt)
        => new() { Status = RunStatus.Succeeded, Duration = duration, CompletedAt = completedAt };

    /// <summary>Creates a failure result.</summary>
    /// <param name="duration">How long the run took before throwing.</param>
    /// <param name="completedAt">When it finished.</param>
    /// <param name="error">The exception.</param>
    public static JobRunResult Failed(TimeSpan duration, DateTimeOffset completedAt, Exception error)
        => new()
        {
            Status = RunStatus.Failed,
            Duration = duration,
            CompletedAt = completedAt,
            Error = error.ToString(),
        };

    /// <summary>Creates a timed-out result.</summary>
    /// <param name="duration">How long the run took before the limit was hit.</param>
    /// <param name="completedAt">When it finished.</param>
    public static JobRunResult TimedOut(TimeSpan duration, DateTimeOffset completedAt)
        => new() { Status = RunStatus.TimedOut, Duration = duration, CompletedAt = completedAt };

    /// <summary>Creates an aborted-by-shutdown result.</summary>
    /// <param name="duration">How long the run took before shutdown.</param>
    /// <param name="completedAt">When it finished.</param>
    public static JobRunResult Aborted(TimeSpan duration, DateTimeOffset completedAt)
        => new() { Status = RunStatus.Aborted, Duration = duration, CompletedAt = completedAt };

    /// <summary>Creates a skipped result, for an occurrence that was never started.</summary>
    /// <param name="completedAt">When the decision was made.</param>
    public static JobRunResult Skipped(DateTimeOffset completedAt)
        => new() { Status = RunStatus.Skipped, Duration = TimeSpan.Zero, CompletedAt = completedAt };
}
