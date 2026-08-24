namespace Cadence.Scheduling;

/// <summary>What the planner decided for one job in one tick.</summary>
public sealed record OccurrencePlan
{
    /// <summary>Nothing was due.</summary>
    public static readonly OccurrencePlan Empty = new();

    /// <summary>Occurrences to attempt, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> Occurrences { get; init; } = [];

    /// <summary>How many due occurrences the missed-run policy or the cap discarded.</summary>
    public int DroppedCount { get; init; }

    /// <summary>True when <see cref="CadenceOptions.MaxCatchUp"/> truncated a replay.</summary>
    public bool TruncatedByCap { get; init; }

    /// <summary>True when the backlog exceeded what is worth enumerating and was abandoned.</summary>
    public bool TooFarBehind { get; init; }
}
