namespace Cadence;

/// <summary>How long run history is kept.</summary>
public sealed record RetentionOptions
{
    /// <summary>Maximum age of a retained run.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Maximum number of retained runs per job.</summary>
    public int MaxRunsPerJob { get; init; } = 500;
}
