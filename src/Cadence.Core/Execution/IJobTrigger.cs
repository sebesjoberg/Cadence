using System.Text.Json;

namespace Cadence.Execution;

/// <summary>Starts a run outside the schedule — from a dashboard button, an endpoint, or code.</summary>
public interface IJobTrigger
{
    /// <summary>
    /// Starts a run now. Returns as soon as the run has been recorded as started; it does not wait
    /// for the job to finish.
    /// </summary>
    /// <param name="jobName">The job's stable name.</param>
    /// <param name="trigger">
    /// What is starting the run. Must be one of the job's allowed triggers, and must not be
    /// <see cref="TriggerKind.Schedule"/> — scheduled occurrences go through the tick loop so they
    /// are claimed exactly once.
    /// </param>
    /// <param name="payload">Optional payload surfaced as <see cref="JobContext.Payload"/>.</param>
    /// <param name="cancellationToken">Cancels the bookkeeping writes, not the run.</param>
    /// <exception cref="JobNotFoundException">No job is registered under that name.</exception>
    /// <exception cref="TriggerNotAllowedException">The job does not accept that trigger.</exception>
    /// <exception cref="SchedulerPausedException">Triggers are paused cluster-wide.</exception>
    Task<DispatchResult> TriggerAsync(
        string jobName,
        TriggerKind trigger = TriggerKind.Manual,
        JsonElement? payload = null,
        CancellationToken cancellationToken = default);
}
