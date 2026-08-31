namespace Cadence.Execution;

/// <summary>
/// Serialises a job that already returns <see cref="JobResult"/> by doing nothing to it.
/// </summary>
/// <remarks>
/// Registered as the exact-type serializer so a file-producing job needs no serializer of its own:
/// declaring <c>IResultJob&lt;TRequest, JobResult&gt;</c> and returning bytes is the whole story.
/// </remarks>
public sealed class JobResultPassthroughSerializer : IJobResultSerializer<JobResult>
{
    /// <inheritdoc />
    public JobResult? Serialize(JobResult value) => value;
}
