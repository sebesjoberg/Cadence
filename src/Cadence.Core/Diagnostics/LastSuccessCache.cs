using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Cadence.Storage;

namespace Cadence.Diagnostics;

/// <summary>
/// Caches when each job last succeeded, so the staleness gauge can be observed without blocking.
/// </summary>
/// <remarks>
/// Metric callbacks are synchronous, and history stores are not. Rather than blocking on an async
/// read from inside an observable-gauge callback, the tick loop refreshes this cache on the config
/// poll interval and the callback reads it.
/// </remarks>
public sealed class LastSuccessCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _lastSuccess = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _trackedSince = new(StringComparer.Ordinal);
    private readonly ISystemClock _clock;

    /// <summary>Creates the cache.</summary>
    /// <param name="clock">Used to compute the observed age.</param>
    public LastSuccessCache(ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Re-reads the last success of every known job.</summary>
    /// <param name="jobNames">The jobs to refresh.</param>
    /// <param name="history">Where to read from.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task RefreshAsync(
        IEnumerable<string> jobNames,
        IRunHistoryStore history,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobNames);
        ArgumentNullException.ThrowIfNull(history);

        foreach (var jobName in jobNames)
        {
            var run = await history.GetLastSuccessAsync(jobName, cancellationToken).ConfigureAwait(false);
            _lastSuccess[jobName] = run?.CompletedAt ?? run?.StartedAt;
        }
    }

    /// <summary>Ensures a job appears in the gauge even before it has ever succeeded.</summary>
    /// <param name="jobName">The job to track.</param>
    public void Track(string jobName)
    {
        _lastSuccess.TryAdd(jobName, null);
        _trackedSince.TryAdd(jobName, _clock.UtcNow);
    }

    /// <summary>
    /// Produces one measurement per tracked job.
    /// </summary>
    /// <remarks>
    /// A job that has never succeeded reports how long it has been watched, not infinity: "no
    /// series at all" and "never ran" look identical in most monitoring systems and only one of
    /// them is acceptable, while a literal infinity breaks a good number of dashboards.
    /// </remarks>
    public IEnumerable<Measurement<double>> Observe()
    {
        var now = _clock.UtcNow;

        foreach (var (jobName, lastSuccess) in _lastSuccess)
        {
            var since = lastSuccess
                ?? (_trackedSince.TryGetValue(jobName, out var tracked) ? tracked : now);

            yield return new Measurement<double>(
                Math.Max(0, (now - since).TotalSeconds),
                new KeyValuePair<string, object?>("job", jobName));
        }
    }
}
