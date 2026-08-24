using System.Diagnostics;
using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Storage;
using Cadence.Validation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Cadence.Scheduling;

/// <summary>
/// The tick loop. Resolves schedules, works out which occurrences are due, claims them, and hands
/// them to the executor.
/// </summary>
internal sealed class CadenceHostedService : BackgroundService
{
    private readonly IJobRegistry _registry;
    private readonly ScheduleResolver _resolver;
    private readonly IScheduleSource _scheduleSource;
    private readonly IOccurrenceCoordinator _coordinator;
    private readonly IRunHistoryStore _history;
    private readonly JobExecutor _executor;
    private readonly JobGraphValidator _validator;
    private readonly LastSuccessCache _lastSuccess;
    private readonly ISystemClock _clock;
    private readonly CadenceMetrics _metrics;
    private readonly CadenceOptions _options;
    private readonly ILogger<CadenceHostedService> _logger;

    private readonly Dictionary<string, JobTickState> _states = new(StringComparer.Ordinal);

    private IDisposable? _changeTokenRegistration;
    private IReadOnlySet<string> _disabledByValidation = new HashSet<string>(StringComparer.Ordinal);
    private volatile bool _reloadRequested = true;
    private DateTimeOffset _lastReload = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuccessRefresh = DateTimeOffset.MinValue;

    public CadenceHostedService(
        IJobRegistry registry,
        ScheduleResolver resolver,
        IScheduleSource scheduleSource,
        IOccurrenceCoordinator coordinator,
        IRunHistoryStore history,
        JobExecutor executor,
        JobGraphValidator validator,
        LastSuccessCache lastSuccess,
        ISystemClock clock,
        CadenceMetrics metrics,
        IOptions<CadenceOptions> options,
        ILogger<CadenceHostedService> logger)
    {
        _registry = registry;
        _resolver = resolver;
        _scheduleSource = scheduleSource;
        _coordinator = coordinator;
        _history = history;
        _executor = executor;
        _validator = validator;
        _lastSuccess = lastSuccess;
        _clock = clock;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _options.Validate();

        // Before anything is scheduled, and on the startup path so a failure stops the host
        // deterministically rather than surfacing as a dead background service.
        _disabledByValidation = await _validator.ValidateAsync(cancellationToken).ConfigureAwait(false);

        foreach (var descriptor in _registry.All)
        {
            _lastSuccess.Track(descriptor.Name);
        }

        _metrics.RegisterSecondsSinceSuccessGauge(_lastSuccess.Observe);

        _changeTokenRegistration = ChangeToken.OnChange(
            _scheduleSource.GetChangeToken,
            () =>
            {
                _logger.ConfigurationChanged();
                _reloadRequested = true;
            });

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.SchedulerStarted(_options.InstanceId, _registry.All.Count, _options.TickInterval);

        using var timer = new PeriodicTimer(_options.TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await TickAsync(_clock.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must never die. A store that is down now will come back, and when it
                // does, scheduling has to resume without a restart.
                _metrics.TickFailures.Add(1);
                _logger.TickFailed(ex);
            }
            finally
            {
                _metrics.TickDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _changeTokenRegistration?.Dispose();

        // Stop claiming first, then let in-flight work finish.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _executor.DrainAsync(_options.ShutdownDrainTimeout).ConfigureAwait(false);

        _logger.SchedulerStopped(_options.InstanceId);
    }

    internal async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_reloadRequested || now - _lastReload >= _options.ConfigPollInterval)
        {
            await ReloadSchedulesAsync(now, cancellationToken).ConfigureAwait(false);
        }

        if (now - _lastSuccessRefresh >= _options.ConfigPollInterval)
        {
            _lastSuccessRefresh = now;
            await _lastSuccess
                .RefreshAsync(_registry.All.Select(d => d.Name), _history, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var state in _states.Values)
        {
            await TickJobAsync(state, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TickJobAsync(JobTickState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var schedule = state.Schedule;

        // A disabled job's occurrences are treated as never having existed: the evaluation point
        // moves forward with the clock. Otherwise re-enabling a job would replay everything missed
        // while it was off, which is a footgun even with the catch-up cap in place.
        if (!schedule.Enabled || _disabledByValidation.Contains(schedule.Descriptor.Name))
        {
            state.LastEvaluated = now;
            return;
        }

        var plan = OccurrencePlanner.Plan(schedule, state.LastEvaluated, now, _options.MaxCatchUp);
        state.LastEvaluated = now;

        if (plan.TooFarBehind)
        {
            _logger.BacklogAbandoned(schedule.Descriptor.Name, OccurrencePlanner.MaxEnumeratedOccurrences);
        }
        else if (plan.TruncatedByCap)
        {
            _logger.CatchUpTruncated(schedule.Descriptor.Name, _options.MaxCatchUp, plan.DroppedCount);
        }
        else if (plan.DroppedCount > 0)
        {
            _logger.MissedOccurrencesSkipped(
                plan.DroppedCount, schedule.Descriptor.Name, schedule.Descriptor.OnMissed);
        }

        foreach (var occurrence in plan.Occurrences)
        {
            // Assigned before the claim, not after it, so that a store which records the claim and
            // the run as one row has the id it needs, and so a claim whose acknowledgement was lost
            // can be retried and recognised as ours. See IOccurrenceCoordinator.TryClaimAsync.
            var runId = Guid.NewGuid();

            var claimed = await _coordinator
                .TryClaimAsync(schedule.Descriptor.Name, occurrence, runId, cancellationToken)
                .ConfigureAwait(false);

            if (!claimed)
            {
                _metrics.ClaimsLost.Add(1, new KeyValuePair<string, object?>("job", schedule.Descriptor.Name));

                _logger.ClaimLost(schedule.Descriptor.Name, occurrence);
                continue;
            }

            // Claim before the overlap check, deliberately. Checking overlap first would mean an
            // instance that is busy declines the slot locally and a different instance runs it
            // anyway, so Skip would do nothing at all in a cluster. Claiming first makes one
            // instance responsible for the slot's outcome: it either runs it or records why not.
            await _executor.DispatchAsync(
                schedule.Descriptor,
                schedule.ToRunSettings(),
                occurrence,
                TriggerKind.Schedule,
                payload: null,
                cancellationToken,
                runId).ConfigureAwait(false);
        }
    }

    private async Task ReloadSchedulesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Cleared before the read, so a change arriving during it triggers another pass rather
        // than being swallowed.
        _reloadRequested = false;
        _lastReload = now;

        ScheduleResolution resolution;

        try
        {
            resolution = await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_states.Count > 0)
        {
            // Keep running on the schedules already loaded. A store blip must not stop scheduling.
            _logger.ScheduleReloadFailed(ex, _states.Count);
            return;
        }

        foreach (var problem in resolution.Problems)
        {
            _logger.ScheduleProblem(problem.JobName, problem.Message);
        }

        foreach (var (jobName, schedule) in resolution.Schedules)
        {
            if (_states.TryGetValue(jobName, out var existing))
            {
                existing.Schedule = schedule;
                continue;
            }

            _states[jobName] = new JobTickState
            {
                Schedule = schedule,
                LastEvaluated = await SeedLastEvaluatedAsync(jobName, now, cancellationToken)
                    .ConfigureAwait(false),
            };
        }

        // Drop jobs whose configuration disappeared or became unusable.
        foreach (var jobName in _states.Keys.Where(k => !resolution.Schedules.ContainsKey(k)).ToList())
        {
            _states.Remove(jobName);
        }
    }

    /// <summary>
    /// Works out how far back a newly loaded job should look for missed occurrences.
    /// </summary>
    /// <remarks>
    /// Seeded from the last recorded occurrence so that, with a persistent history store, downtime
    /// catch-up works without any extra bookkeeping. With the in-memory store there is nothing to
    /// read after a restart, so catch-up covers stalls within a process lifetime only.
    /// </remarks>
    private async Task<DateTimeOffset> SeedLastEvaluatedAsync(
        string jobName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var lastRun = await _history.GetLastRunAsync(jobName, cancellationToken).ConfigureAwait(false);
            return lastRun?.ScheduledFor ?? now;
        }
        catch (Exception ex)
        {
            _logger.LastRunReadFailed(ex, jobName);
            return now;
        }
    }

    private sealed class JobTickState
    {
        public required EffectiveSchedule Schedule { get; set; }

        public required DateTimeOffset LastEvaluated { get; set; }
    }
}
