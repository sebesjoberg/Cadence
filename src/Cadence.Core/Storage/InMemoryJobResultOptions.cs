namespace Cadence.Storage;

/// <summary>Settings for the in-memory result store.</summary>
public sealed class InMemoryJobResultOptions
{
    /// <summary>
    /// Total bytes to hold before the oldest results are dropped. Results live on the process heap
    /// here, so this is a ceiling on how much of it the scheduler may take: exceed it and the
    /// oldest go, whatever their expiry says.
    /// </summary>
    public long MaxTotalBytes { get; set; } = 64L * 1024 * 1024;
}
