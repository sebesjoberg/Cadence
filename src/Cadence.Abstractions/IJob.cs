namespace Cadence;

/// <summary>
/// A unit of work the scheduler can execute. Implementations are resolved from the DI
/// container once per run, in a scope of their own.
/// </summary>
/// <remarks>
/// Implementations are expected to complete. Cadence does not host long-running work:
/// a job that never returns is surfaced as a timeout, not supported as a use case.
/// </remarks>
public interface IJob
{
    /// <summary>Executes the job.</summary>
    /// <param name="context">Metadata about the run, and the progress sink.</param>
    /// <param name="cancellationToken">
    /// Signalled on host shutdown or when the job exceeds its configured maximum duration.
    /// Implementations must observe it; ignoring it turns a graceful drain into a forced
    /// abort recorded against the job.
    /// </param>
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}
