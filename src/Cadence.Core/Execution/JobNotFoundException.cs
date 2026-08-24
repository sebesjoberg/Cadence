namespace Cadence.Execution;

/// <summary>Thrown when a name does not match any registered job.</summary>
public sealed class JobNotFoundException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="jobName">The name that was not found.</param>
    public JobNotFoundException(string jobName)
        : base($"No job is registered under the name '{jobName}'.")
        => JobName = jobName;

    /// <summary>The name that was not found.</summary>
    public string JobName { get; }
}
