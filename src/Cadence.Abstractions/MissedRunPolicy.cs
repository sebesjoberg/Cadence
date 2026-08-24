namespace Cadence;

/// <summary>What to do about occurrences that came due while nothing was watching.</summary>
/// <remarks>
/// Occurrences are missed when the host was down or the tick loop stalled long enough for more than
/// one occurrence to fall behind. A <em>disabled</em> job is different: its occurrences are treated
/// as never having existed, so re-enabling it never replays the dormant period.
/// </remarks>
public enum MissedRunPolicy
{
    /// <summary>Default. Ignore everything missed and resume from the next occurrence.</summary>
    SkipToNext = 0,

    /// <summary>Run once immediately, then resume the normal schedule.</summary>
    RunOnce = 1,

    /// <summary>
    /// Run every missed occurrence, up to <c>CadenceOptions.MaxCatchUp</c>. The cap exists because a
    /// <c>*/5</c> job whose host was down for a month would otherwise schedule thousands of runs at
    /// once.
    /// </summary>
    RunAll = 2,
}
