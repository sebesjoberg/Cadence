using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Cadence.Diagnostics;
using Cadence.Scheduling;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Execution;

/// <summary>
/// Starts and tracks runs. Owns the gating rules (overlap, per-instance capacity), the per-run
/// DI scope, the two cancellation sources, and the drain on shutdown.
/// </summary>
public sealed class JobExecutor : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunHistoryStore _history;
    private readonly IJobResultStore _results;
    private readonly IJobProgressSink _progress;
    private readonly ISystemClock _clock;
    private readonly CadenceMetrics _metrics;
    private readonly CadenceOptions _options;
    private readonly ILogger<JobExecutor> _logger;

    private readonly ConcurrentDictionary<Guid, InFlightRun> _inFlight = new();
    private readonly Dictionary<string, int> _inFlightByJob = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private int _activeTotal;

    /// <summary>Creates the executor.</summary>
    /// <param name="scopeFactory">Creates one DI scope per run.</param>
    /// <param name="history">Where runs are recorded.</param>
    /// <param name="results">Where the bytes a run produced are kept.</param>
    /// <param name="progress">Sink handed to jobs via <see cref="JobContext.Report"/>.</param>
    /// <param name="clock">The only source of the current time.</param>
    /// <param name="metrics">Instruments to record against.</param>
    /// <param name="options">Host-wide settings.</param>
    /// <param name="logger">Receives execution diagnostics.</param>
    public JobExecutor(
        IServiceScopeFactory scopeFactory,
        IRunHistoryStore history,
        IJobResultStore results,
        IJobProgressSink progress,
        ISystemClock clock,
        CadenceMetrics metrics,
        IOptions<CadenceOptions> options,
        ILogger<JobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _history = history;
        _results = results;
        _progress = progress;
        _clock = clock;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>How many runs of a job are executing on this instance.</summary>
    /// <param name="jobName">The job's stable name.</param>
    public int InFlightCount(string jobName)
    {
        lock (_gate)
        {
            return _inFlightByJob.TryGetValue(jobName, out var count) ? count : 0;
        }
    }

    /// <summary>Total runs executing on this instance.</summary>
    public int ActiveRunCount => Volatile.Read(ref _activeTotal);

    /// <summary>
    /// Applies the gating rules and, if they pass, starts the run on a worker thread.
    /// </summary>
    /// <remarks>
    /// This method awaits only the history writes, never the run. Awaiting a run here would let
    /// one slow job stall every other schedule in the process.
    /// </remarks>
    /// <param name="descriptor">The job to run.</param>
    /// <param name="settings">Effective overlap, duration and settings for this run.</param>
    /// <param name="scheduledFor">The occurrence, or null for a non-scheduled trigger.</param>
    /// <param name="trigger">What started the run.</param>
    /// <param name="payload">Optional payload from an API trigger.</param>
    /// <param name="cancellationToken">Cancels the history writes, not the run.</param>
    /// <param name="runId">
    /// The id to record the run under. Supplied by the tick loop, which assigns it before claiming
    /// the occurrence so that the claim and the run are the same identity — a store that records
    /// both as one row would otherwise see the claim's row and this one collide on the occurrence.
    /// Null for triggers that never claim, where a fresh id is generated here.
    /// </param>
    public async Task<DispatchResult> DispatchAsync(
        JobDescriptor descriptor,
        RunSettings settings,
        DateTimeOffset? scheduledFor,
        TriggerKind trigger,
        JsonElement? payload,
        CancellationToken cancellationToken,
        Guid? runId = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(settings);

        string? skipReason;

        // Check and reserve under one lock so two occurrences in the same tick cannot both pass
        // an overlap check that only one of them should.
        lock (_gate)
        {
            var jobInFlight = _inFlightByJob.TryGetValue(descriptor.Name, out var c) ? c : 0;

            if (settings.Overlap == OverlapPolicy.Skip && jobInFlight > 0)
            {
                skipReason =
                    $"A run of '{descriptor.Name}' is already in flight on this instance and the " +
                    "overlap policy is Skip.";
            }
            else if (_activeTotal >= _options.MaxConcurrentRuns)
            {
                skipReason =
                    $"This instance is at its concurrency limit of {_options.MaxConcurrentRuns} runs. " +
                    "Raise CadenceOptions.MaxConcurrentRuns, or reduce how much is scheduled together.";
            }
            else
            {
                skipReason = null;
                _inFlightByJob[descriptor.Name] = jobInFlight + 1;
                _activeTotal++;
            }
        }

        // The claimed id is reused for the skip record, not replaced by a fresh one. The claim has
        // already consumed this occurrence, so a second identity for the same slot would collide
        // with it in any store that enforces one row per occurrence.
        var effectiveRunId = runId ?? Guid.NewGuid();

        if (skipReason is not null)
        {
            await RecordSkippedAsync(
                descriptor, scheduledFor, trigger, skipReason, effectiveRunId, cancellationToken)
                .ConfigureAwait(false);

            return DispatchResult.Skipped(skipReason);
        }

        var startedAt = _clock.UtcNow;
        JobRun? started;

        try
        {
            started = await _history.StartAsync(
                new JobRunStart
                {
                    RunId = effectiveRunId,
                    JobName = descriptor.Name,
                    ScheduledFor = scheduledFor,
                    Trigger = trigger,
                    InstanceId = _options.InstanceId,
                    StartedAt = startedAt,

                    // What makes Skip strict across the cluster rather than only inside this
                    // process. The gate above already refused an overlap this instance can see; the
                    // store is what refuses one it cannot.
                    ExclusiveKey = settings.Overlap == OverlapPolicy.Skip ? descriptor.Name : null,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Release(descriptor.Name);
            throw;
        }

        if (started is null)
        {
            Release(descriptor.Name);

            var heldReason =
                $"A run of '{descriptor.Name}' is already in flight on another instance and the " +
                "overlap policy is Skip.";

            await RecordSkippedAsync(
                descriptor, scheduledFor, trigger, heldReason, effectiveRunId, cancellationToken)
                .ConfigureAwait(false);

            return DispatchResult.Skipped(heldReason);
        }

        var context = new JobContext(_progress)
        {
            JobName = descriptor.Name,
            RunId = effectiveRunId,
            ScheduledFor = scheduledFor,
            StartedAt = startedAt,
            Trigger = trigger,
            InstanceId = _options.InstanceId,
            Payload = payload,
            Settings = settings.Settings,
        };

        var runTask = Task.Run(
            () => ExecuteAsync(descriptor, settings, context, startedAt),
            CancellationToken.None);

        _inFlight[effectiveRunId] = new InFlightRun(effectiveRunId, descriptor.Name, startedAt, runTask);

        _ = runTask.ContinueWith(
            completed =>
            {
                _inFlight.TryRemove(effectiveRunId, out _);
                Release(descriptor.Name);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return DispatchResult.Started(effectiveRunId);
    }

    /// <summary>
    /// Waits until nothing is in flight, without signalling shutdown.
    /// </summary>
    /// <remarks>
    /// Public alongside <see cref="ActiveRunCount"/> and <see cref="InFlightCount"/>, and for the
    /// same reason: dispatch is deliberately fire-and-forget, so anything that needs to observe the
    /// result of a run has no other way to know it has finished. That is what makes "advance the
    /// clock, then assert" deterministic instead of a sleep, and it is unrelated to shutdown - use
    /// <see cref="DrainAsync"/> for that.
    /// </remarks>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            var running = _inFlight.Values.Select(r => r.Task).ToArray();
            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAll(running).ConfigureAwait(false);

            // The bookkeeping continuation may not have run yet, so yield rather than spin.
            await Task.Yield();
        }
    }

    /// <summary>
    /// Waits for in-flight runs to finish, then records anything still going as aborted so history
    /// is never left claiming a run is in progress after the process has gone.
    /// </summary>
    /// <param name="timeout">How long to wait before giving up on the stragglers.</param>
    public async Task DrainAsync(TimeSpan timeout)
    {
        // Signal the runs first: a well-behaved job observes its token and unwinds.
        await _shutdown.CancelAsync().ConfigureAwait(false);

        var running = _inFlight.Values.Select(r => r.Task).ToArray();
        if (running.Length == 0)
        {
            return;
        }

        _logger.DrainWaiting(timeout, running.Length);

        try
        {
            await Task.WhenAll(running).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Expected when a job ignores its cancellation token.
        }
        catch (Exception)
        {
            // Individual run failures are already recorded against their own history rows.
        }

        foreach (var straggler in _inFlight.Values)
        {
            _logger.StragglerAborted(straggler.RunId, straggler.JobName);

            await CompleteQuietlyAsync(
                straggler.RunId,
                JobRunResult.Aborted(_clock.UtcNow - straggler.StartedAt, _clock.UtcNow)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    private async Task ExecuteAsync(
        JobDescriptor descriptor,
        RunSettings settings,
        JobContext context,
        DateTimeOffset startedAt)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["JobName"] = descriptor.Name,
            ["RunId"] = context.RunId,
            ["InstanceId"] = _options.InstanceId,
        });

        using var activity = CadenceDiagnostics.ActivitySource.StartActivity(
            CadenceDiagnostics.RunActivityName, ActivityKind.Internal);

        activity?.SetTag("job.name", descriptor.Name);
        activity?.SetTag("job.run_id", context.RunId);
        activity?.SetTag("job.trigger", context.Trigger.ToString());
        activity?.SetTag("job.instance_id", _options.InstanceId);
        if (context.ScheduledFor is { } occurrence)
        {
            activity?.SetTag("job.scheduled_for", occurrence.ToString("O"));
        }

        var jobTag = new KeyValuePair<string, object?>("job", descriptor.Name);
        _metrics.ActiveRuns.Add(1, jobTag);

        // Two sources, kept apart. Handing the job a single linked token is right; discarding the
        // individual sources is not, because then a timeout and a shutdown are indistinguishable
        // in history, and the history is what tells you whether the job is slow or the host is
        // churning.
        using var timeoutCts = new CancellationTokenSource();
        if (settings.MaxDuration is { } maxDuration)
        {
            timeoutCts.CancelAfter(maxDuration);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token, timeoutCts.Token);

        var stopwatch = Stopwatch.StartNew();
        JobRunResult result;

        try
        {
            await using var serviceScope = _scopeFactory.CreateAsyncScope();

            var job = (IJob)serviceScope.ServiceProvider.GetRequiredService(descriptor.ImplementationType);

            // A result job's IJob.ExecuteAsync would run identically and discard what came back, so
            // the typed path is taken purely to keep hold of it.
            var invoker = ResultJobInvoker.For(descriptor.ImplementationType);

            if (invoker is null)
            {
                await job.ExecuteAsync(context, linkedCts.Token).ConfigureAwait(false);
            }
            else
            {
                var produced = await invoker
                    .InvokeAsync(job, serviceScope.ServiceProvider, context, linkedCts.Token)
                    .ConfigureAwait(false);

                if (produced is not null)
                {
                    await StoreResultAsync(context.RunId, descriptor.Name, produced).ConfigureAwait(false);
                }
            }

            result = JobRunResult.Success(stopwatch.Elapsed, _clock.UtcNow);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            result = JobRunResult.TimedOut(stopwatch.Elapsed, _clock.UtcNow);
            _logger.RunTimedOut(context.RunId, descriptor.Name, settings.MaxDuration);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            result = JobRunResult.Aborted(stopwatch.Elapsed, _clock.UtcNow);
            _logger.RunAborted(context.RunId, descriptor.Name);
        }
        catch (Exception ex)
        {
            result = JobRunResult.Failed(stopwatch.Elapsed, _clock.UtcNow, ex);
            _logger.RunFailed(ex, context.RunId, descriptor.Name);
        }
        finally
        {
            _metrics.ActiveRuns.Add(-1, jobTag);
        }

        activity?.SetTag("job.status", result.Status.ToString());
        if (result.Status is not RunStatus.Succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Status.ToString());
        }

        _metrics.RunDuration.Record(result.Duration.TotalMilliseconds, jobTag);
        _metrics.Runs.Add(
            1,
            jobTag,
            new KeyValuePair<string, object?>("status", result.Status.ToString()),
            new KeyValuePair<string, object?>("trigger", context.Trigger.ToString()));

        // CancellationToken.None: recording why a run ended must not be cancelled by the shutdown
        // that ended it. This is what keeps history from filling with rows stuck at Running.
        await CompleteQuietlyAsync(context.RunId, result).ConfigureAwait(false);
    }

    private async Task StoreResultAsync(Guid runId, string jobName, JobResult produced)
    {
        if (produced.Length > _options.MaxResultBytes)
        {
            throw new InvalidOperationException(
                $"'{jobName}' produced a {produced.Length:N0} byte result, over the " +
                $"{_options.MaxResultBytes:N0} byte ceiling set by CadenceOptions.MaxResultBytes. " +
                "Nothing was stored. Raise the ceiling, or have the job produce less.");
        }

        // Uncancellable for the same reason completions are: the bytes are what the run was for,
        // and losing them to the shutdown that arrived mid-write is worse than waiting out the write.
        await _results.SaveAsync(
            runId,
            produced,
            _clock.UtcNow + _options.Retention.ResultMaxAge,
            CancellationToken.None).ConfigureAwait(false);

        _logger.ResultStored(runId, jobName, produced.Length, produced.ContentType);
    }

    private async Task RecordSkippedAsync(
        JobDescriptor descriptor,
        DateTimeOffset? scheduledFor,
        TriggerKind trigger,
        string reason,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        try
        {
            await _history.StartAsync(
                new JobRunStart
                {
                    RunId = runId,
                    JobName = descriptor.Name,
                    ScheduledFor = scheduledFor,
                    Trigger = trigger,
                    InstanceId = _options.InstanceId,
                    StartedAt = now,
                },
                cancellationToken).ConfigureAwait(false);

            // The reason is recorded, not just logged: a dashboard showing a gap in the schedule
            // has to be able to explain why nothing happened.
            await _history.AppendLogAsync(
                runId,
                new JobLogEntry { Timestamp = now, Message = reason },
                cancellationToken).ConfigureAwait(false);

            await _history.CompleteAsync(runId, JobRunResult.Skipped(now), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.SkippedRecordFailed(ex, descriptor.Name, reason);
        }

        _metrics.Runs.Add(
            1,
            new KeyValuePair<string, object?>("job", descriptor.Name),
            new KeyValuePair<string, object?>("status", RunStatus.Skipped.ToString()),
            new KeyValuePair<string, object?>("trigger", trigger.ToString()));

        _logger.OccurrenceSkipped(descriptor.Name, reason);
    }

    private async Task CompleteQuietlyAsync(Guid runId, JobRunResult result)
    {
        try
        {
            await _history.CompleteAsync(runId, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.RunCompletionWriteFailed(ex, runId);
        }
    }

    private void Release(string jobName)
    {
        lock (_gate)
        {
            if (_inFlightByJob.TryGetValue(jobName, out var count))
            {
                if (count <= 1)
                {
                    _inFlightByJob.Remove(jobName);
                }
                else
                {
                    _inFlightByJob[jobName] = count - 1;
                }
            }

            _activeTotal = Math.Max(0, _activeTotal - 1);
        }
    }

    private sealed record InFlightRun(Guid RunId, string JobName, DateTimeOffset StartedAt, Task Task);
}
