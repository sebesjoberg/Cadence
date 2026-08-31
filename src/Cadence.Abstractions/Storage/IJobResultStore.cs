namespace Cadence.Storage;

/// <summary>
/// Holds the bytes runs produce, keyed by run id, until retention sweeps them.
/// </summary>
/// <remarks>
/// <para>
/// Split from <see cref="IRunHistoryStore"/> because the sizes differ by orders of magnitude. Run
/// history is rows a dashboard pages through; results are blobs somebody downloads one at a time,
/// and a tier that can hold the first cheaply may want somewhere else entirely for the second.
/// Keeping them apart is what lets a deployment put results on a filesystem or an object store
/// without moving its history.
/// </para>
/// <para>
/// Writes are buffered and reads stream, which follows from where the bytes come from: a job built
/// its result in memory, while a caller downloading one should not require the whole thing to be
/// resident first.
/// </para>
/// </remarks>
public interface IJobResultStore
{
    /// <summary>Stores the result of a run, replacing any result already held for it.</summary>
    /// <param name="runId">The run that produced it.</param>
    /// <param name="result">The bytes, their media type, and any suggested filename.</param>
    /// <param name="expiresAt">
    /// When the result becomes eligible for deletion. A tier with native expiry may honour this
    /// directly; the rest leave it to <see cref="PurgeAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SaveAsync(
        Guid runId,
        JobResult result,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Describes a run's result without reading its bytes, or null when there is none.
    /// </summary>
    /// <remarks>
    /// What a status endpoint calls. Answering "is there something to download" must not cost the
    /// download.
    /// </remarks>
    /// <param name="runId">The run to describe.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<JobResultInfo?> DescribeAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Opens a run's result for reading, or null when there is none.</summary>
    /// <remarks>The caller owns the returned object and must dispose it.</remarks>
    /// <param name="runId">The run to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<StoredJobResult?> OpenAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Deletes the result held for a run, if any.</summary>
    /// <param name="runId">The run whose result to delete.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task DeleteAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Deletes results whose expiry has passed. Called by the janitor.</summary>
    /// <param name="now">Results expiring before this instant are eligible.</param>
    /// <param name="batchSize">How many to delete per operation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many results were deleted.</returns>
    Task<int> PurgeAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}
