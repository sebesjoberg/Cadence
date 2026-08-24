namespace Cadence;

/// <summary>The set of jobs registered in this process, keyed by their stable names.</summary>
public interface IJobRegistry
{
    /// <summary>Every registered job.</summary>
    IReadOnlyCollection<JobDescriptor> All { get; }

    /// <summary>Looks up a job by name.</summary>
    /// <param name="name">The job's stable name.</param>
    /// <param name="descriptor">The descriptor, when found.</param>
    /// <returns>True when a job with that name is registered.</returns>
    bool TryGet(string name, out JobDescriptor? descriptor);
}
