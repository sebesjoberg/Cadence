namespace Cadence;

/// <summary>
/// Receives progress reported by a running job. Implementations must not block or throw:
/// progress reporting is a diagnostic, and a failure to record it must never fail the run.
/// </summary>
public interface IJobProgressSink
{
    /// <summary>Records one progress entry against a run.</summary>
    /// <param name="runId">The run the entry belongs to.</param>
    /// <param name="message">Human-readable progress message.</param>
    /// <param name="data">Optional structured values.</param>
    void Report(Guid runId, string message, IReadOnlyDictionary<string, object?>? data);
}
