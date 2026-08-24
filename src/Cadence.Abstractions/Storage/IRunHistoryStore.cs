namespace Cadence.Storage;

/// <summary>Records what happened. Feeds the dashboard, the watchdog and the alert rules.</summary>
public interface IRunHistoryStore
{
    /// <summary>Records that a run has begun, and returns the created record.</summary>
    /// <param name="start">Identity and timing of the starting run.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<JobRun> StartAsync(JobRunStart start, CancellationToken cancellationToken);

    /// <summary>Records a run's outcome.</summary>
    /// <param name="runId">The run to complete.</param>
    /// <param name="result">Status, duration and error, if any.</param>
    /// <param name="cancellationToken">
    /// Should be <see cref="CancellationToken.None"/> from the execution path: recording why a run
    /// ended must not be cancelled by the shutdown that ended it.
    /// </param>
    Task CompleteAsync(Guid runId, JobRunResult result, CancellationToken cancellationToken);

    /// <summary>Appends a progress entry to a run.</summary>
    /// <param name="runId">The run the entry belongs to.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task AppendLogAsync(Guid runId, JobLogEntry entry, CancellationToken cancellationToken);

    /// <summary>The most recent run of a job, whatever its outcome.</summary>
    /// <param name="jobName">The job's stable name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<JobRun?> GetLastRunAsync(string jobName, CancellationToken cancellationToken);

    /// <summary>The most recent successful run of a job. Drives the staleness watchdog.</summary>
    /// <param name="jobName">The job's stable name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<JobRun?> GetLastSuccessAsync(string jobName, CancellationToken cancellationToken);

    /// <summary>Queries run history.</summary>
    /// <param name="query">Filters and paging.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<JobRun>> QueryAsync(RunQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// How many runs have failed in a row, counting back from the most recent. Zero when the last
    /// completed run succeeded. Drives alert thresholds.
    /// </summary>
    /// <param name="jobName">The job's stable name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<int> CountConsecutiveFailuresAsync(string jobName, CancellationToken cancellationToken);

    /// <summary>Deletes history older than a cut-off. Called by the janitor.</summary>
    /// <param name="olderThan">Runs started before this instant are eligible for deletion.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
