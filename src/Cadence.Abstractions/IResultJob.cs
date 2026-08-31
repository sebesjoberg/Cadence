namespace Cadence;

/// <summary>
/// A job that takes a typed request and produces a typed result Cadence stores and can hand back.
/// </summary>
/// <remarks>
/// <para>
/// This extends <see cref="IJob"/> rather than standing beside it, so there is still one job model,
/// one registry, one history and one retention story. A result job is scheduled by cron, started by
/// the API, or submitted as a work item through exactly the same path; the only difference is that
/// the last of those has somebody waiting to collect what came back.
/// </para>
/// <para>
/// The inherited <see cref="IJob.ExecuteAsync"/> binds <typeparamref name="TRequest"/> from
/// <see cref="JobContext.Payload"/> and discards the result, which is what a cron occurrence wants:
/// nothing supplied a request, and nothing is waiting to collect one. A cron run therefore sees
/// <see langword="default"/> for its request — a reference type will be null — so a job that is
/// scheduled as well as submitted has to say what that means.
/// </para>
/// <para>
/// Returning <see cref="JobResult"/> directly is the shortest path for a job that produces a file.
/// Any other <typeparamref name="TResult"/> is turned into bytes by an
/// <see cref="IJobResultSerializer{TResult}"/>, which defaults to JSON.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">What the job is asked to process.</typeparam>
/// <typeparam name="TResult">What it produces.</typeparam>
public interface IResultJob<TRequest, TResult> : IJob
{
    /// <summary>Executes the job against a request and returns its result.</summary>
    /// <param name="request">
    /// The bound request. <see langword="default"/> when nothing supplied one, which is the case
    /// for every cron occurrence.
    /// </param>
    /// <param name="context">Metadata about the run, and the progress sink.</param>
    /// <param name="cancellationToken">
    /// Signalled on host shutdown or when the job exceeds its configured maximum duration.
    /// </param>
    Task<TResult> ExecuteAsync(
        TRequest request,
        JobContext context,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    async Task IJob.ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Null-forgiving because the contract above is that a request-less run sees default, which
        // for a reference type is null.
        await ExecuteAsync(context.Bind<TRequest>()!, context, cancellationToken).ConfigureAwait(false);
    }
}
