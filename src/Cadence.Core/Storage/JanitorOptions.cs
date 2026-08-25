namespace Cadence.Storage;

/// <summary>What the janitor needs to know, independent of which storage tier it is tidying.</summary>
/// <remarks>
/// Each storage package keeps its own options type as the surface a caller configures — that is
/// where a connection string and a command timeout belong — and projects the three values below
/// into this one when it registers the janitor. The janitor therefore has no idea whether it is
/// running against a database or a key-value store.
/// </remarks>
public sealed class JanitorOptions
{
    /// <summary>How often to purge history, trim per job and reap abandoned runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many records to delete or update per operation, looping until a pass is done.
    /// </summary>
    /// <remarks>
    /// Batched rather than one sweeping operation: in SQL a single delete of a large backlog takes
    /// enough row locks to escalate to a table lock, which blocks the claim insert — so the janitor
    /// would stall scheduling while tidying up after it. The same bound keeps a Redis pass from
    /// occupying the server for long enough to matter, for the same reason.
    /// </remarks>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// How stale an instance's heartbeat must be before its unfinished runs are reaped.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How much longer than <see cref="HeartbeatTimeout"/> an instance record is kept after the
    /// instance is judged gone.
    /// </summary>
    /// <remarks>
    /// A multiple rather than a duration, so shortening the timeout for a demo or a test shortens
    /// this with it and the relationship between the two survives.
    /// </remarks>
    public int InstanceRetentionMultiplier { get; set; } = 10;

    /// <summary>Validates the options and throws when a value cannot work.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its supported range.</exception>
    public void Validate()
    {
        if (Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Interval), Interval, "The janitor interval must be positive.");
        }

        if (BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BatchSize), BatchSize, "The janitor batch size must be at least one.");
        }

        if (HeartbeatTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatTimeout), HeartbeatTimeout, "The heartbeat timeout must be positive.");
        }

        if (InstanceRetentionMultiplier < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InstanceRetentionMultiplier),
                InstanceRetentionMultiplier,
                "The instance retention multiplier must be at least one, so an instance record " +
                "never disappears before the runs it explains.");
        }
    }
}
