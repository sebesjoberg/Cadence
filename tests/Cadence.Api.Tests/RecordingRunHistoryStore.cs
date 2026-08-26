using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cadence.Api.Tests;

/// <summary>
/// Wraps the in-memory store and records every <see cref="RunQuery"/> it is handed. The response
/// bodies cannot show whether a list query asked for progress entries — they carry no log field
/// either way — so the only place that flag is observable is the query itself.
/// </summary>
internal sealed class RecordingRunHistoryStore(IRunHistoryStore inner) : IRunHistoryStore
{
    private readonly List<RunQuery> _queries = [];

    /// <summary>Every query seen so far, oldest first.</summary>
    public IReadOnlyList<RunQuery> Queries
    {
        get
        {
            lock (_queries)
            {
                return [.. _queries];
            }
        }
    }

    /// <summary>Replaces the registered store with a recorder over a fresh in-memory one.</summary>
    /// <param name="services">The collection to replace the registration in.</param>
    public static RecordingRunHistoryStore Install(IServiceCollection services)
    {
        var store = new RecordingRunHistoryStore(new InMemoryRunHistoryStore());
        services.Replace(ServiceDescriptor.Singleton<IRunHistoryStore>(store));
        return store;
    }

    /// <summary>Forgets what has been recorded, so a test can ignore anything host startup did.</summary>
    public void Clear()
    {
        lock (_queries)
        {
            _queries.Clear();
        }
    }

    public Task<IReadOnlyList<JobRun>> QueryAsync(RunQuery query, CancellationToken cancellationToken)
    {
        lock (_queries)
        {
            _queries.Add(query);
        }

        return inner.QueryAsync(query, cancellationToken);
    }

    public Task<JobRun> StartAsync(JobRunStart start, CancellationToken cancellationToken)
        => inner.StartAsync(start, cancellationToken);

    public Task CompleteAsync(Guid runId, JobRunResult result, CancellationToken cancellationToken)
        => inner.CompleteAsync(runId, result, cancellationToken);

    public Task AppendLogAsync(Guid runId, JobLogEntry entry, CancellationToken cancellationToken)
        => inner.AppendLogAsync(runId, entry, cancellationToken);

    public Task<JobRun?> GetAsync(Guid runId, CancellationToken cancellationToken)
        => inner.GetAsync(runId, cancellationToken);

    public Task<JobRun?> GetLastRunAsync(string jobName, CancellationToken cancellationToken)
        => inner.GetLastRunAsync(jobName, cancellationToken);

    public Task<JobRun?> GetLastSuccessAsync(string jobName, CancellationToken cancellationToken)
        => inner.GetLastSuccessAsync(jobName, cancellationToken);

    public Task<int> CountConsecutiveFailuresAsync(string jobName, CancellationToken cancellationToken)
        => inner.CountConsecutiveFailuresAsync(jobName, cancellationToken);

    public Task PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
        => inner.PurgeAsync(olderThan, cancellationToken);
}
