namespace Cadence.Storage;

/// <summary>
/// Holds the cluster-wide pause switches: the one thing an operator can say to every instance at
/// once, and the reason it is a store rather than an option.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IScheduleSource"/> even though a persistent tier will keep both in the
/// same place. A schedule says what a job should do; this says whether the scheduler should be
/// doing anything at all, and a source that cannot be written to — code, configuration — still has
/// to be pausable.
/// </para>
/// <para>
/// Read on the same cadence as schedules, so a pause reaches other instances within one poll
/// interval. That is the property to hold implementations to: not that the write is instant, but
/// that it arrives without anyone restarting anything.
/// </para>
/// </remarks>
public interface IPauseStore
{
    /// <summary>The current switches.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<PauseState> GetAsync(CancellationToken cancellationToken);

    /// <summary>Sets both switches at once, and returns the state as stored.</summary>
    /// <param name="scope">What to pause. <see cref="PauseScope.None"/> resumes everything.</param>
    /// <param name="reason">Free text shown to operators.</param>
    /// <param name="setBy">Who is doing this, as the caller knows them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<PauseState> SetAsync(
        PauseScope scope,
        string? reason,
        string? setBy,
        CancellationToken cancellationToken);
}
