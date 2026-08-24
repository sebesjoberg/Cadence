namespace Cadence;

/// <summary>What to do when an occurrence comes due while a previous run is still going.</summary>
/// <remarks>
/// Enforcement is strict within one instance, which tracks its own in-flight runs exactly. Across a
/// cluster it is best-effort: the occurrence claim guarantees only that one instance
/// <em>starts</em> a given slot, so a run overrunning into the next occurrence can be followed by a
/// second run on a different instance. If you need a hard cross-instance guarantee, take an
/// application-level lock inside the job.
/// </remarks>
public enum OverlapPolicy
{
    /// <summary>
    /// Default. Do not start; record the occurrence as skipped so the dashboard can show why nothing
    /// happened.
    /// </summary>
    Skip = 0,

    /// <summary>
    /// Start anyway. Each run gets its own DI scope, so scoped state is <em>not</em> shared between
    /// concurrent runs of the same job.
    /// </summary>
    AllowConcurrent = 1,
}
