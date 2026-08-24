namespace Cadence.Storage;

/// <summary>Thrown when a schedule write loses an optimistic-concurrency check.</summary>
public sealed class ScheduleConflictException : Exception
{
    /// <summary>Creates the exception for a named job.</summary>
    /// <param name="jobName">The job whose schedule could not be written.</param>
    public ScheduleConflictException(string jobName)
        : base($"The schedule for '{jobName}' was modified by someone else. Reload and retry.")
        => JobName = jobName;

    /// <summary>The job whose schedule could not be written.</summary>
    public string JobName { get; }
}
