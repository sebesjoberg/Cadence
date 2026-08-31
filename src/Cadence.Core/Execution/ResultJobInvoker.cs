using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Cadence.Execution;

/// <summary>
/// Calls a job's typed <see cref="IResultJob{TRequest, TResult}.ExecuteAsync"/> and serialises what
/// came back, without the executor having to know either type argument.
/// </summary>
/// <remarks>
/// The executor could call <see cref="IJob.ExecuteAsync"/> and get identical behaviour — the
/// default interface implementation binds the same request — but the result would be discarded on
/// the way out. This exists to keep hold of it.
/// </remarks>
internal abstract class ResultJobInvoker
{
    private static readonly ConcurrentDictionary<Type, ResultJobInvoker?> Cache = new();

    /// <summary>
    /// The invoker for an implementation type, or null when the type produces no result.
    /// </summary>
    /// <param name="implementationType">The registered job type.</param>
    /// <exception cref="CadenceStartupException">
    /// The type implements <see cref="IResultJob{TRequest, TResult}"/> more than once, so which
    /// result a run produces has no answer.
    /// </exception>
    public static ResultJobInvoker? For(Type implementationType)
        => Cache.GetOrAdd(implementationType, Build);

    /// <summary>Runs the job and serialises its result.</summary>
    /// <param name="job">The resolved job instance.</param>
    /// <param name="scope">The run's scope, which the serializer is resolved from.</param>
    /// <param name="context">The run's context.</param>
    /// <param name="cancellationToken">The run's linked cancellation token.</param>
    public abstract Task<JobResult?> InvokeAsync(
        object job,
        IServiceProvider scope,
        JobContext context,
        CancellationToken cancellationToken);

    private static ResultJobInvoker? Build(Type implementationType)
    {
        var interfaces = implementationType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IResultJob<,>))
            .ToArray();

        if (interfaces.Length == 0)
        {
            return null;
        }

        if (interfaces.Length > 1)
        {
            throw new CadenceStartupException(
                $"'{implementationType.FullName}' implements IResultJob<,> {interfaces.Length} times, so " +
                "there is no single result a run of it produces. Implement it once, and dispatch " +
                "between shapes inside the job.");
        }

        var arguments = interfaces[0].GetGenericArguments();

        return (ResultJobInvoker)Activator.CreateInstance(
            typeof(TypedInvoker<,>).MakeGenericType(arguments))!;
    }

    private sealed class TypedInvoker<TRequest, TResult> : ResultJobInvoker
    {
        public override async Task<JobResult?> InvokeAsync(
            object job,
            IServiceProvider scope,
            JobContext context,
            CancellationToken cancellationToken)
        {
            var typed = (IResultJob<TRequest, TResult>)job;

            var value = await typed
                .ExecuteAsync(context.Bind<TRequest>()!, context, cancellationToken)
                .ConfigureAwait(false);

            return scope.GetRequiredService<IJobResultSerializer<TResult>>().Serialize(value);
        }
    }
}
