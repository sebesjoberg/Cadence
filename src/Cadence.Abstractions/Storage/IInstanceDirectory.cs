namespace Cadence.Storage;

/// <summary>
/// Reads the instance registry the storage tiers write heartbeats into. Named apart from those
/// registries because they are write-only background services and this is the read side.
/// </summary>
public interface IInstanceDirectory
{
    /// <summary>
    /// Every recorded instance, including ones whose heartbeat has lapsed. Stale rows are the
    /// point: a view that drops the dead instance hides what the reader opened it to see.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<InstanceInfo>> GetAllAsync(CancellationToken cancellationToken);
}
