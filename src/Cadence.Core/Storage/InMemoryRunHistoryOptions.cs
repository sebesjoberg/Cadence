namespace Cadence.Storage;

/// <summary>Settings for the in-memory history store.</summary>
public sealed class InMemoryRunHistoryOptions
{
    /// <summary>
    /// How many runs to keep per job before the oldest are dropped. Deliberately lower than the
    /// persistent stores' default: this history lives in the process heap and dies with it.
    /// </summary>
    public int MaxRunsPerJob { get; set; } = 100;
}
