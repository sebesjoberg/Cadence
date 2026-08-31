namespace Cadence;

/// <summary>How long run history is kept.</summary>
public sealed record RetentionOptions
{
    /// <summary>Maximum age of a retained run.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Maximum number of retained runs per job.</summary>
    public int MaxRunsPerJob { get; init; } = 500;

    /// <summary>
    /// How long a run's result is kept before the janitor deletes it.
    /// </summary>
    /// <remarks>
    /// Much shorter than <see cref="MaxAge"/>, and separate from it on purpose: a run is a row and
    /// a result is a file. Keeping a month of history costs almost nothing; keeping a month of
    /// spreadsheets is a capacity decision somebody should make deliberately. History outlives its
    /// result, so an expired one reads as "this ran and produced something you can no longer
    /// download" rather than disappearing.
    /// </remarks>
    public TimeSpan ResultMaxAge { get; init; } = TimeSpan.FromDays(7);
}
