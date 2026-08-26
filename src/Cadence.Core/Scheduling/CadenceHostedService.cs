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
/// Drives the scheduler: validates the job graph at boot, ticks <see cref="ScheduleTicker"/> on a
/// timer, and drains in-flight runs on shutdown.
/// </summary>
/// <remarks>
/// Deliberately thin. Everything about <em>what</em> a tick does lives in
/// <see cref="ScheduleTicker"/>, which is public and can be driven directly; this type owns only the
/// parts that need a host - the timer, the boot probe, the change-token subscription and the drain.
/// </remarks>
internal sealed class CadenceHostedService : BackgroundService
{
    private readonly IJobRegistry _registry;
    private readonly ScheduleTicker _ticker;
    private readonly IScheduleSource _scheduleSource;
    private readonly JobExecutor _executor;
    private readonly JobGraphValidator _validator;
    private readonly ShutdownBudgetProbe _shutdownBudget;
    private readonly LastSuccessCache _lastSuccess;
    private readonly ISystemClock _clock;
    private readonly CadenceMetrics _metrics;
    private readonly CadenceOptions _options;
    private readonly ILogger<CadenceHostedService> _logger;

    private IDisposable? _changeTokenRegistration;

    public CadenceHostedService(
        IJobRegistry registry,
        ScheduleTicker ticker,
        IScheduleSource scheduleSource,
        JobExecutor executor,
        JobGraphValidator validator,
        ShutdownBudgetProbe shutdownBudget,
        LastSuccessCache lastSuccess,
        ISystemClock clock,
        CadenceMetrics metrics,
        IOptions<CadenceOptions> options,
        ILogger<CadenceHostedService> logger)
    {
        _registry = registry;
        _ticker = ticker;
        _scheduleSource = scheduleSource;
        _executor = executor;
        _validator = validator;
        _shutdownBudget = shutdownBudget;
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
        _ticker.DisableJobs(await _validator.ValidateAsync(cancellationToken).ConfigureAwait(false));

        // Advice, not a gate: the outermost timeout in the chain belongs to whatever supervises the
        // process, so this can warn but never refuse to start.
        _shutdownBudget.Report();

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
                _ticker.RequestReload();
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
                await _ticker.TickAsync(_clock.UtcNow, stoppingToken).ConfigureAwait(false);
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
}
