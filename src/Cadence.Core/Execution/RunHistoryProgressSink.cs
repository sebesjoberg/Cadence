using System.Diagnostics;
using Cadence.Diagnostics;
using Cadence.Storage;
using Microsoft.Extensions.Logging;

namespace Cadence.Execution;

/// <summary>
/// Fans job-reported progress out to the three places it belongs: the OpenTelemetry trace, the
/// host's <see cref="ILogger"/> pipeline, and run history.
/// </summary>
/// <remarks>
/// <para>
/// All three, because they answer different questions. The <b>activity event</b> puts progress on
/// the run's span, so it shows up in Jaeger or Application Insights next to the HTTP and database
/// calls the job made. The <b>log</b> goes through the standard <see cref="ILogger"/> pipeline, so
/// any OTLP log exporter the host has configured picks it up with no Cadence-specific wiring —
/// and it inherits the run's logging scope, so <c>JobName</c>, <c>RunId</c> and <c>InstanceId</c>
/// are attached without the job author doing anything. <b>History</b> is what the dashboard reads
/// back, which the other two cannot serve: a trace is sampled and a log is not queryable per run.
/// </para>
/// <para>
/// Progress is a diagnostic, so this never blocks the job and never lets a storage failure surface
/// as a job failure. Entries are timestamped when reported rather than when written, so an
/// out-of-order write still sorts correctly.
/// </para>
/// </remarks>
public sealed class RunHistoryProgressSink : IJobProgressSink
{
    private readonly IRunHistoryStore _history;
    private readonly ISystemClock _clock;
    private readonly ILogger<RunHistoryProgressSink> _logger;

    /// <summary>Creates the sink.</summary>
    /// <param name="history">Where entries are appended.</param>
    /// <param name="clock">Supplies entry timestamps.</param>
    /// <param name="logger">Carries progress into the host's logging pipeline, and receives write failures.</param>
    public RunHistoryProgressSink(
        IRunHistoryStore history,
        ISystemClock clock,
        ILogger<RunHistoryProgressSink> logger)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _history = history;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Report(Guid runId, string message, IReadOnlyDictionary<string, object?>? data)
    {
        var entry = new JobLogEntry
        {
            Timestamp = _clock.UtcNow,
            Message = message,
            Data = data,
        };

        AddActivityEvent(entry);

        // The enclosing scope already carries JobName, RunId and InstanceId, so this line is
        // correlated to its run in any log sink without the job author doing anything.
        _logger.JobProgress(message);

        // Deliberately not awaited: a slow history store must not become a slow job. The append is
        // a diagnostic write, and losing one on shutdown is preferable to blocking on it.
        _ = AppendAsync(runId, entry);
    }

    private static void AddActivityEvent(JobLogEntry entry)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection { { "message", entry.Message } };

        if (entry.Data is not null)
        {
            foreach (var (key, value) in entry.Data)
            {
                // Namespaced so a caller's key cannot shadow "message".
                tags[$"data.{key}"] = value;
            }
        }

        activity.AddEvent(new ActivityEvent(
            CadenceDiagnostics.ProgressEventName, entry.Timestamp, tags));
    }

    private async Task AppendAsync(Guid runId, JobLogEntry entry)
    {
        try
        {
            await _history.AppendLogAsync(runId, entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.ProgressWriteFailed(ex, runId);
        }
    }
}
