namespace Cadence.Storage;

/// <summary>
/// The tidying a persistent storage tier has to support so the janitor can keep it bounded.
/// </summary>
/// <remarks>
/// <para>
/// Split out from <see cref="IRunHistoryStore"/> because the audiences differ: the store is what
/// the scheduler, the dashboard and the alert rules read and write, while this is what one
/// background timer calls. A tier that persists nothing — the in-memory default — implements the
/// store and not this, and gets no janitor.
/// </para>
/// <para>
/// The <em>policy</em> lives in the janitor and is deliberately not repeated per tier: reap before
/// purge, work in batches, never escalate a failure into a scheduling problem. Only the operations
/// below differ between a database and a key-value store, so only they are behind the seam.
/// </para>
/// <para>
/// Every operation is idempotent and expressed as a set operation, which is what lets every
/// instance run the janitor without electing a leader. Each returns how many records it touched, so
/// the janitor can both log a pass and tell a finished batch from a full one.
/// </para>
/// </remarks>
public interface IStorageMaintenance
{
    /// <summary>
    /// Marks runs abandoned by instances that stopped heartbeating as <see cref="RunStatus.Lost"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="RunStatus.Lost"/> rather than <see cref="RunStatus.Aborted"/>: aborted means a
    /// shutdown cancelled the run and something recorded that, while lost means nobody recorded
    /// anything at all. A run still marked running by an instance that has left the registry
    /// entirely is also abandoned — a graceful stop deregisters, so its absence means it did not
    /// stop gracefully.
    /// </remarks>
    /// <param name="heartbeatDeadline">
    /// An instance whose last heartbeat predates this is treated as gone.
    /// </param>
    /// <param name="now">The completion instant to stamp on the reaped records.</param>
    /// <param name="batchSize">How many records to touch per operation.</param>
    /// <param name="cancellationToken">Cancels the reap.</param>
    /// <returns>How many runs were marked.</returns>
    Task<int> ReapAbandonedRunsAsync(
        DateTimeOffset heartbeatDeadline,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>Deletes finished runs started before a cut-off.</summary>
    /// <remarks>
    /// Finished only. A run still in <see cref="RunStatus.Running"/> is left alone however old it
    /// is, because deleting it would destroy the evidence the reap above exists to record.
    /// </remarks>
    /// <param name="olderThan">Runs started before this instant are eligible.</param>
    /// <param name="batchSize">How many records to delete per operation.</param>
    /// <param name="cancellationToken">Cancels the purge.</param>
    /// <returns>How many runs were deleted.</returns>
    Task<int> PurgeRunsByAgeAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>Trims each job's history down to its most recent finished runs.</summary>
    /// <remarks>
    /// Running runs are excluded from the ranking as well as from the deletion. A job at its cap
    /// whose current run is still going should not have that run counted towards the cap and then
    /// spared only because it is running.
    /// </remarks>
    /// <param name="maxRunsPerJob">How many runs to keep per job.</param>
    /// <param name="batchSize">How many records to delete per operation.</param>
    /// <param name="cancellationToken">Cancels the trim.</param>
    /// <returns>How many runs were deleted.</returns>
    Task<int> TrimRunsPerJobAsync(
        int maxRunsPerJob,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>Removes instance records long past their heartbeat timeout.</summary>
    /// <remarks>
    /// Called with a cut-off far older than the one that reaps runs, so an instance outlives the
    /// decision that it was gone. Removing it at the same moment would leave a reaped run pointing
    /// at an instance nothing can explain, which is exactly the question someone reads history to
    /// answer.
    /// </remarks>
    /// <param name="olderThan">Instances last seen before this instant are eligible.</param>
    /// <param name="batchSize">How many records to delete per operation.</param>
    /// <param name="cancellationToken">Cancels the purge.</param>
    /// <returns>How many instances were removed.</returns>
    Task<int> PurgeDeadInstancesAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>Deletes API tokens whose expiry has passed.</summary>
    /// <remarks>
    /// A tier whose keys carry a time-to-live has nothing to do here and returns zero. An expired
    /// token already stops resolving — <see cref="IApiTokenStore.FindAsync"/> enforces that — so
    /// this pass reclaims space rather than closing a security gap, which is why it runs last.
    /// </remarks>
    /// <param name="now">Tokens whose expiry precedes this are eligible.</param>
    /// <param name="batchSize">How many records to delete per operation.</param>
    /// <param name="cancellationToken">Cancels the purge.</param>
    /// <returns>How many tokens were deleted.</returns>
    Task<int> PurgeExpiredApiTokensAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken);
}
