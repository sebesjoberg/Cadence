using Cadence.Storage;

namespace Cadence.Execution;

/// <summary>
/// Thrown when a trigger is refused because triggers are paused cluster-wide. The API layer maps
/// this to <c>409 Conflict</c>.
/// </summary>
public sealed class SchedulerPausedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="jobName">The job that was not started.</param>
    /// <param name="state">The pause state that refused it.</param>
    public SchedulerPausedException(string jobName, PauseState state)
        : base(BuildMessage(jobName, state))
    {
        JobName = jobName;
        State = state;
    }

    /// <summary>The job that was not started.</summary>
    public string JobName { get; }

    /// <summary>The pause state that refused it.</summary>
    public PauseState State { get; }

    private static string BuildMessage(string jobName, PauseState state)
    {
        var who = state.SetBy is { Length: > 0 } setBy ? $" by {setBy}" : string.Empty;
        var why = state.Reason is { Length: > 0 } reason ? $": {reason}" : ".";

        return $"'{jobName}' was not started because triggers are paused{who}{why}";
    }
}
