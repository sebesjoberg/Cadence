using System.Collections.Immutable;
using System.Text.Json;

namespace Cadence;

/// <summary>Everything a job knows about the run it is executing.</summary>
public sealed class JobContext
{
    private readonly IJobProgressSink _progress;

    /// <summary>Creates a context. Constructed by the scheduler, not by job code.</summary>
    /// <param name="progress">Sink that <see cref="Report"/> writes through.</param>
    public JobContext(IJobProgressSink progress) => _progress = progress;

    /// <summary>The stable name of the job, as registered. Not the CLR type name.</summary>
    public required string JobName { get; init; }

    /// <summary>Identifies this run in history, logs, traces and the dashboard.</summary>
    public required Guid RunId { get; init; }

    /// <summary>
    /// The occurrence this run belongs to, in UTC. Null for runs that are not tied to a
    /// scheduled slot — API, manual and startup triggers.
    /// </summary>
    public DateTimeOffset? ScheduledFor { get; init; }

    /// <summary>When execution actually began, which lags <see cref="ScheduledFor"/> by tick jitter.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>How this run was started.</summary>
    public required TriggerKind Trigger { get; init; }

    /// <summary>The instance that won the claim and is executing this run.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Free-form payload supplied by an API trigger. Null for cron runs.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>Per-job settings from the schedule store, editable at runtime.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Records structured progress against this run. The dashboard displays it live and run
    /// history retains it, so it is the right place for "processed 400 of 12,000" rather than
    /// a log line nobody correlates.
    /// </summary>
    /// <param name="message">Human-readable progress message.</param>
    /// <param name="data">Optional structured values to attach.</param>
    public void Report(string message, IReadOnlyDictionary<string, object?>? data = null)
        => _progress.Report(RunId, message, data);
}
