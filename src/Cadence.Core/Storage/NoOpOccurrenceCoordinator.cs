namespace Cadence.Storage;

/// <summary>
/// Grants every claim. Correct for a single instance, and wrong the moment a second one starts —
/// which is why adding a storage package replaces it with a real coordinator rather than making
/// clustering a separate opt-in.
/// </summary>
public sealed class NoOpOccurrenceCoordinator : IOccurrenceCoordinator
{
    /// <inheritdoc />
    public Task<bool> TryClaimAsync(
        string jobName,
        DateTimeOffset scheduledFor,
        CancellationToken cancellationToken) => Task.FromResult(true);
}
