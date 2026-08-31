namespace Cadence.Storage;

/// <summary>
/// Keeps a bounded ring of recent runs per job, in process memory.
/// </summary>
/// <remarks>
/// History is per-instance and lost on restart. That is a supported configuration — it is what
/// makes the zero-infrastructure path work — but it means the dashboard can only show the runs
/// this instance executed, and it must say so rather than implying it has the whole picture.
/// </remarks>
public sealed class InMemoryRunHistoryStore : IRunHistoryStore
{
    // A single gate rather than concurrent collections: history writes are one per run plus the
    // occasional progress entry, so contention is nil and obvious correctness is worth more.
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, MutableRun> _byId = [];
    private readonly Dictionary<string, List<MutableRun>> _byJob = new(StringComparer.Ordinal);
    private readonly int _maxRunsPerJob;

    /// <summary>Creates the store.</summary>
    /// <param name="options">Ring size. Defaults are used when null.</param>
    public InMemoryRunHistoryStore(InMemoryRunHistoryOptions? options = null)
        => _maxRunsPerJob = Math.Max(1, (options ?? new InMemoryRunHistoryOptions()).MaxRunsPerJob);

    /// <inheritdoc />
    public Task<JobRun?> StartAsync(JobRunStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);

        lock (_gate)
        {
            // Derived rather than tracked in an index of its own: a key held by a run the ring has
            // since trimmed would be a key nothing releases, and a scan of a bounded ring cannot
            // leak one. The run's own id is excluded so claiming and then starting the same run
            // does not block itself.
            if (start.ExclusiveKey is { } key &&
                _byId.Values.Any(existing =>
                    existing.Status == RunStatus.Running &&
                    existing.RunId != start.RunId &&
                    string.Equals(existing.ExclusiveKey, key, StringComparison.Ordinal)))
            {
                return Task.FromResult<JobRun?>(null);
            }

            var run = new MutableRun
            {
                RunId = start.RunId,
                JobName = start.JobName,
                ScheduledFor = start.ScheduledFor,
                Trigger = start.Trigger,
                InstanceId = start.InstanceId,
                StartedAt = start.StartedAt,
                Status = RunStatus.Running,
                ExclusiveKey = start.ExclusiveKey,
            };

            _byId[run.RunId] = run;

            if (!_byJob.TryGetValue(run.JobName, out var runs))
            {
                runs = [];
                _byJob[run.JobName] = runs;
            }

            runs.Add(run);
            TrimLocked(runs);

            return Task.FromResult<JobRun?>(Snapshot(run));
        }
    }

    /// <inheritdoc />
    public Task CompleteAsync(Guid runId, JobRunResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            if (_byId.TryGetValue(runId, out var run))
            {
                run.Status = result.Status;
                run.Duration = result.Duration;
                run.CompletedAt = result.CompletedAt;
                run.Error = result.Error;

                // Released by the same write that records the outcome, so there is no instant in
                // which the run is finished and the key it held is not free.
                run.ExclusiveKey = null;
            }
        }

        // A run that has already been trimmed out of the ring is not an error: the outcome write
        // simply has nothing left to update.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AppendLogAsync(Guid runId, JobLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (_byId.TryGetValue(runId, out var run))
            {
                run.Log.Add(entry);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<JobRun?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_byId.TryGetValue(runId, out var run) ? Snapshot(run) : null);
        }
    }

    /// <inheritdoc />
    public Task<JobRun?> GetLastRunAsync(string jobName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var run = RunsForLocked(jobName)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefault();

            return Task.FromResult(run is null ? null : Snapshot(run));
        }
    }

    /// <inheritdoc />
    public Task<JobRun?> GetLastSuccessAsync(string jobName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var run = RunsForLocked(jobName)
                .Where(r => r.Status == RunStatus.Succeeded)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefault();

            return Task.FromResult(run is null ? null : Snapshot(run));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobRun>> QueryAsync(RunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            var source = query.JobName is null
                ? _byJob.Values.SelectMany(runs => runs)
                : RunsForLocked(query.JobName);

            if (query.Statuses is { Count: > 0 })
            {
                source = source.Where(r => query.Statuses.Contains(r.Status));
            }

            if (query.From is { } from)
            {
                source = source.Where(r => r.StartedAt >= from);
            }

            if (query.To is { } to)
            {
                source = source.Where(r => r.StartedAt < to);
            }

            if (query.InstanceId is not null)
            {
                source = source.Where(r => string.Equals(r.InstanceId, query.InstanceId, StringComparison.Ordinal));
            }

            IReadOnlyList<JobRun> results =
            [
                .. source
                    .OrderByDescending(r => r.StartedAt)
                    .Skip(Math.Max(0, query.Offset))
                    .Take(Math.Max(0, query.Limit))
                    .Select(run => Snapshot(run, query.IncludeLog)),
            ];

            return Task.FromResult(results);
        }
    }

    /// <inheritdoc />
    public Task<int> CountConsecutiveFailuresAsync(string jobName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var count = 0;

            foreach (var run in RunsForLocked(jobName).OrderByDescending(r => r.StartedAt))
            {
                switch (run.Status)
                {
                    case RunStatus.Failed:
                    case RunStatus.TimedOut:
                    case RunStatus.Lost:
                        count++;
                        break;

                    case RunStatus.Succeeded:
                        // A success ends the streak.
                        return Task.FromResult(count);

                    default:
                        // Running, Skipped and Aborted say nothing about whether the job's own
                        // logic is broken, so they neither extend nor break the streak.
                        break;
                }
            }

            return Task.FromResult(count);
        }
    }

    /// <inheritdoc />
    public Task PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var (_, runs) in _byJob)
            {
                for (var i = runs.Count - 1; i >= 0; i--)
                {
                    if (runs[i].StartedAt < olderThan && runs[i].Status != RunStatus.Running)
                    {
                        _byId.Remove(runs[i].RunId);
                        runs.RemoveAt(i);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private List<MutableRun> RunsForLocked(string jobName)
        => _byJob.TryGetValue(jobName, out var runs) ? runs : [];

    private void TrimLocked(List<MutableRun> runs)
    {
        while (runs.Count > _maxRunsPerJob)
        {
            _byId.Remove(runs[0].RunId);
            runs.RemoveAt(0);
        }
    }

    private static JobRun Snapshot(MutableRun run, bool includeLog = true) => new()
    {
        RunId = run.RunId,
        JobName = run.JobName,
        ScheduledFor = run.ScheduledFor,
        Trigger = run.Trigger,
        Status = run.Status,
        InstanceId = run.InstanceId,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        Duration = run.Duration,
        Error = run.Error,
        Log = includeLog ? [.. run.Log] : [],
    };

    private sealed class MutableRun
    {
        public required Guid RunId { get; init; }

        public required string JobName { get; init; }

        public DateTimeOffset? ScheduledFor { get; init; }

        public required TriggerKind Trigger { get; init; }

        public required string InstanceId { get; init; }

        public required DateTimeOffset StartedAt { get; init; }

        public RunStatus Status { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public TimeSpan? Duration { get; set; }

        public string? Error { get; set; }

        /// <summary>Set while running and exclusive; nulled by the outcome write.</summary>
        public string? ExclusiveKey { get; set; }

        public List<JobLogEntry> Log { get; } = [];
    }
}
