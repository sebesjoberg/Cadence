using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Scheduling;

/// <summary>
/// One pass of the scheduler: work out which occurrences are due, claim them, and hand the winners
/// to the executor.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the hosted service that drives it, because a timer and a decision are different
/// things. <see cref="CadenceHostedService"/> owns when a tick happens — the periodic timer, the
/// boot-time validation, the change-token subscription, the drain on shutdown. This owns what a tick
/// does, and does it on demand.
/// </para>
/// <para>
/// That split is what makes the scheduler testable without friend assemblies or a real clock: a test
/// calls <see cref="TickAsync"/> at whatever instants it likes and asserts on the result. It is also
/// the seam the clustered tests use, where several tickers over one database have to be driven in
/// lockstep to prove that only one of them starts a given occurrence.
/// </para>
/// <para>
/// Nothing here blocks. A tick must never wait on a run, or one slow job stalls every other schedule
/// in the process.
/// </para>
/// </remarks>
public sealed class ScheduleTicker
{
    private readonly IJobRegistry _registry;
    private readonly ScheduleResolver _resolver;
    private readonly IOccurrenceCoordinator _coordinator;
    private readonly IRunHistoryStore _history;
    private readonly IPauseStore _pauses;
    private readonly JobExecutor _executor;
    private readonly LastSuccessCache _lastSuccess;
    private readonly ISystemClock _clock;
    private readonly CadenceMetrics _metrics;
    private readonly CadenceOptions _options;
    private readonly ILogger<ScheduleTicker> _logger;

    private readonly Dictionary<string, JobTickState> _states = new(StringComparer.Ordinal);

    private IReadOnlySet<string> _disabledByValidation = new HashSet<string>(StringComparer.Ordinal);
    private volatile bool _reloadRequested = true;
    private DateTimeOffset _lastReload = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuccessRefresh = DateTimeOffset.MinValue;
    private PauseState _pause = PauseState.None;

    /// <summary>Creates the ticker.</summary>
    /// <param name="registry">The registered jobs.</param>
    /// <param name="resolver">Layers stored schedules over code-declared defaults.</param>
    /// <param name="coordinator">Decides which instance runs an occurrence.</param>
    /// <param name="history">Read to seed how far back a newly loaded job should look.</param>
    /// <param name="pauses">The cluster-wide pause switches, re-read with the schedules.</param>
    /// <param name="executor">Starts the runs this ticker claims.</param>
    /// <param name="lastSuccess">Backs the staleness gauge; refreshed on the poll interval.</param>
    /// <param name="clock">The only source of the current time.</param>
    /// <param name="metrics">Instruments to record against.</param>
    /// <param name="options">Host-wide settings.</param>
    /// <param name="logger">Receives scheduling diagnostics.</param>
    public ScheduleTicker(
        IJobRegistry registry,
        ScheduleResolver resolver,
        IOccurrenceCoordinator coordinator,
        IRunHistoryStore history,
        IPauseStore pauses,
        JobExecutor executor,
        LastSuccessCache lastSuccess,
        ISystemClock clock,
        CadenceMetrics metrics,
        IOptions<CadenceOptions> options,
        ILogger<ScheduleTicker> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(pauses);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(lastSuccess);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _resolver = resolver;
        _coordinator = coordinator;
        _history = history;
        _pauses = pauses;
        _executor = executor;
        _lastSuccess = lastSuccess;
        _clock = clock;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Marks the cached schedules stale, so the next tick re-reads them.
    /// </summary>
    /// <remarks>
    /// Called from the schedule source's change token, which fires on whatever thread noticed the
    /// change — hence the volatile flag rather than a reload from here. Re-reading on the caller's
    /// thread would put a database round trip inside a change callback.
    /// </remarks>
    public void RequestReload() => _reloadRequested = true;

    /// <summary>
    /// Records which jobs the boot-time validation could not construct, so they are never scheduled.
    /// </summary>
    /// <param name="jobNames">Names of the jobs to leave alone.</param>
    public void DisableJobs(IReadOnlySet<string> jobNames)
    {
        ArgumentNullException.ThrowIfNull(jobNames);
        _disabledByValidation = jobNames;
    }

    /// <summary>Runs one pass.</summary>
    /// <param name="now">The instant to evaluate against. Comes from the clock in production.</param>
    /// <param name="cancellationToken">Cancels the reads and the claims, not the runs.</param>
    public async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
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
        // while it was off, which is a footgun even with the catch-up cap in place. A cluster-wide
        // pause takes the same branch, and gets the same property: resuming replays nothing.
        if (!schedule.Enabled
            || _pause.IsSchedulePaused
            || _disabledByValidation.Contains(schedule.Descriptor.Name))
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

        await ReloadPauseAsync(cancellationToken).ConfigureAwait(false);

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

    private async Task ReloadPauseAsync(CancellationToken cancellationToken)
    {
        PauseState state;

        try
        {
            state = await _pauses.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Keep the switches where they were. A store blip must neither pause a running cluster
            // nor resume a paused one.
            _logger.PauseReadFailed(ex, _pause.Scope);
            return;
        }

        if (state.Scope != _pause.Scope)
        {
            _logger.PauseChanged(_pause.Scope, state.Scope, state.SetBy, state.Reason);
        }

        _pause = state;
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
