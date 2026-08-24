using System.Diagnostics.Metrics;

namespace Cadence.Diagnostics;

/// <summary>
/// The instruments described in the design plan. Registered as a singleton so the meter and its
/// instruments are created once per host.
/// </summary>
public sealed class CadenceMetrics : IDisposable
{
    private readonly Meter _meter;

    /// <summary>Creates the meter and its instruments.</summary>
    /// <param name="meterFactory">
    /// Factory supplied by the host so the meter participates in the host's metrics pipeline.
    /// </param>
    public CadenceMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(CadenceDiagnostics.SourceName);

        Runs = _meter.CreateCounter<long>(
            "cadence.runs", unit: "{run}", description: "Completed runs, tagged by status and trigger.");

        RunDuration = _meter.CreateHistogram<double>(
            "cadence.run.duration", unit: "ms", description: "How long runs take.");

        ActiveRuns = _meter.CreateUpDownCounter<long>(
            "cadence.runs.active", unit: "{run}", description: "Runs currently executing on this instance.");

        ClaimsLost = _meter.CreateCounter<long>(
            "cadence.claims.lost", unit: "{claim}", description: "Occurrences another instance claimed first.");

        TickDuration = _meter.CreateHistogram<double>(
            "cadence.tick.duration", unit: "ms", description: "How long one pass of the tick loop takes.");

        TickFailures = _meter.CreateCounter<long>(
            "cadence.tick.failures", unit: "{failure}", description: "Tick passes that threw.");
    }

    /// <summary>Completed runs. Tags: <c>job</c>, <c>status</c>, <c>trigger</c>.</summary>
    public Counter<long> Runs { get; }

    /// <summary>Run duration in milliseconds. Tag: <c>job</c>.</summary>
    public Histogram<double> RunDuration { get; }

    /// <summary>Runs in flight on this instance. Tag: <c>job</c>.</summary>
    public UpDownCounter<long> ActiveRuns { get; }

    /// <summary>Occurrences lost to another instance's claim. Tag: <c>job</c>.</summary>
    public Counter<long> ClaimsLost { get; }

    /// <summary>Tick-loop pass duration in milliseconds.</summary>
    public Histogram<double> TickDuration { get; }

    /// <summary>Tick passes that threw. A non-zero rate here means schedules are not being evaluated.</summary>
    public Counter<long> TickFailures { get; }

    /// <summary>
    /// Registers the observable gauge that reports, per job, how long it has been since a successful
    /// run. This is the instrument to alert on externally: it is the only one that goes wrong when
    /// the scheduler dies silently, because absence of failure is not evidence of success.
    /// </summary>
    /// <param name="observe">Callback producing one measurement per known job.</param>
    public void RegisterSecondsSinceSuccessGauge(Func<IEnumerable<Measurement<double>>> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);

        _meter.CreateObservableGauge(
            "cadence.job.seconds_since_success",
            observe,
            unit: "s",
            description: "Seconds since each job last succeeded.");
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
