namespace Cadence.Validation;

/// <summary>
/// Checks the part of the shutdown chain that is inside the process.
/// </summary>
internal static class ShutdownBudget
{
    /// <summary>Reports every way the configured shutdown budget truncates a run.</summary>
    /// <param name="hostShutdownTimeout">The host's own shutdown budget.</param>
    /// <param name="shutdownDrainTimeout">How long in-flight runs are given to finish.</param>
    /// <param name="jobs">The registered jobs, whose longest maximum duration sets the floor.</param>
    /// <returns>One message per violation; empty when the budget is consistent.</returns>
    public static IReadOnlyList<string> Check(
        TimeSpan hostShutdownTimeout,
        TimeSpan shutdownDrainTimeout,
        IReadOnlyCollection<JobDescriptor> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var problems = new List<string>();

        var longest = jobs
            .Where(job => job.MaxDuration is not null)
            .OrderByDescending(job => job.MaxDuration!.Value)
            .FirstOrDefault();

        if (longest is not null && shutdownDrainTimeout < longest.MaxDuration!.Value)
        {
            problems.Add(
                $"{nameof(CadenceOptions)}.{nameof(CadenceOptions.ShutdownDrainTimeout)} is " +
                $"{shutdownDrainTimeout}, shorter than the longest registered MaxDuration " +
                $"({longest.MaxDuration!.Value}, on '{longest.Name}'). A run that uses its full " +
                "duration will be aborted by shutdown rather than allowed to finish.");
        }

        if (hostShutdownTimeout < shutdownDrainTimeout)
        {
            problems.Add(
                $"HostOptions.ShutdownTimeout is {hostShutdownTimeout}, shorter than " +
                $"{nameof(CadenceOptions)}.{nameof(CadenceOptions.ShutdownDrainTimeout)} " +
                $"({shutdownDrainTimeout}). The host stops waiting before the drain is finished, so " +
                "the drain cannot use the time it was given.");
        }

        return problems;
    }
}
