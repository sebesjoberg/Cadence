namespace Cadence.Storage;

/// <summary>Thrown when a schedule write loses an optimistic-concurrency check.</summary>
public sealed class ScheduleConflictException : Exception
{
    /// <summary>Creates the exception for a named job.</summary>
    /// <param name="jobName">The job whose schedule could not be written.</param>
    public ScheduleConflictException(string jobName)
        : base($"The schedule for '{jobName}' was modified by someone else. Reload and retry.")
        => JobName = jobName;

    /// <summary>Creates the exception with the versions that disagreed.</summary>
    /// <param name="jobName">The job whose schedule could not be written.</param>
    /// <param name="expectedVersion">The version the caller believed was stored.</param>
    /// <param name="actualVersion">The version actually stored.</param>
    public ScheduleConflictException(string jobName, int expectedVersion, int actualVersion)
        : base($"The schedule for '{jobName}' was modified by someone else: expected version " +
               $"{expectedVersion}, found {actualVersion}. Reload and retry.")
    {
        JobName = jobName;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>The job whose schedule could not be written.</summary>
    public string JobName { get; }

    /// <summary>The version the caller believed was stored, or null when it did not say.</summary>
    public int? ExpectedVersion { get; }

    /// <summary>The version actually stored, or null when it was not read back.</summary>
    public int? ActualVersion { get; }
}
