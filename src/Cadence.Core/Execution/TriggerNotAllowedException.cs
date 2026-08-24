namespace Cadence.Execution;

/// <summary>
/// Thrown when a job does not accept the requested trigger. The API layer maps this to
/// <c>409 Conflict</c>.
/// </summary>
public sealed class TriggerNotAllowedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="jobName">The job that refused the trigger.</param>
    /// <param name="trigger">The trigger that was attempted.</param>
    /// <param name="detail">What the job does allow.</param>
    public TriggerNotAllowedException(string jobName, TriggerKind trigger, string detail)
        : base($"'{jobName}' cannot be triggered by {trigger}. {detail}")
    {
        JobName = jobName;
        Trigger = trigger;
    }

    /// <summary>The job that refused the trigger.</summary>
    public string JobName { get; }

    /// <summary>The trigger that was attempted.</summary>
    public TriggerKind Trigger { get; }
}
