namespace Cadence;

/// <summary>
/// Turns what a job returned into the bytes Cadence stores and serves.
/// </summary>
/// <remarks>
/// This is the seam that keeps Cadence from having an opinion about what a result is. The default
/// registration serialises any <typeparamref name="TResult"/> as JSON; a job that returns
/// <see cref="JobResult"/> is passed through untouched. Register your own implementation for
/// <typeparamref name="TResult"/> to produce anything else.
/// </remarks>
/// <typeparam name="TResult">The type a job returns.</typeparam>
public interface IJobResultSerializer<in TResult>
{
    /// <summary>Serialises one result.</summary>
    /// <param name="value">What the job returned.</param>
    /// <returns>
    /// The bytes to store, or null to record that the run produced nothing to collect.
    /// </returns>
    JobResult? Serialize(TResult value);
}
