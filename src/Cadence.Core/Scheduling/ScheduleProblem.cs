namespace Cadence.Scheduling;

/// <summary>
/// A job whose configuration could not be used. Reported rather than thrown, so one bad job does
/// not stop the rest of the schedule from running.
/// </summary>
/// <param name="JobName">The job's stable name.</param>
/// <param name="Message">What is wrong with it.</param>
public sealed record ScheduleProblem(string JobName, string Message);
