namespace Cadence.Storage;

/// <summary>The cluster-wide pause switches, and who last moved them.</summary>
public sealed record PauseState
{
    /// <summary>Nothing paused, and no record of anyone having paused anything.</summary>
    public static PauseState None { get; } = new();

    /// <summary>What is currently paused.</summary>
    public PauseScope Scope { get; init; } = PauseScope.None;

    /// <summary>Why, as given by whoever set it. Surfaced to operators, never interpreted.</summary>
    public string? Reason { get; init; }

    /// <summary>Who set it, as given by the caller.</summary>
    public string? SetBy { get; init; }

    /// <summary>When it was last set. Null when it has never been set.</summary>
    public DateTimeOffset? SetAtUtc { get; init; }

    /// <summary>Whether the tick loop should claim occurrences.</summary>
    public bool IsSchedulePaused => Scope.HasFlag(PauseScope.Schedule);

    /// <summary>Whether out-of-band triggers should be refused.</summary>
    public bool AreTriggersPaused => Scope.HasFlag(PauseScope.Triggers);
}
