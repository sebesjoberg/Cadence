using Cadence.Storage;

namespace Cadence.Api.Internal;

/// <summary>
/// The one place a storage record becomes a response. Every endpoint file projects through this, so
/// a storage type cannot reach the wire by way of a route that wrote its own mapping.
/// </summary>
internal static class Responses
{
    /// <summary>Projects a run without its log.</summary>
    /// <param name="run">The recorded run.</param>
    public static RunSummaryResponse ToSummary(JobRun run) => new(
        run.RunId,
        run.JobName,
        run.Status.ToString(),
        run.Trigger.ToString(),
        run.InstanceId,
        run.ScheduledFor,
        run.StartedAt,
        run.CompletedAt,
        run.Duration,
        run.Error);

    /// <summary>Projects a run together with the progress it reported.</summary>
    /// <param name="run">The recorded run.</param>
    public static RunDetailResponse ToDetail(JobRun run) => new(
        ToSummary(run),
        [.. run.Log.Select(entry => new LogEntryResponse(entry.Timestamp, entry.Message))]);

    /// <summary>Projects the cluster-wide pause switches.</summary>
    /// <param name="state">The stored pause state.</param>
    public static PauseResponse ToPause(PauseState state) => new(
        state.Scope.ToString(),
        state.Reason,
        state.SetBy,
        state.SetAtUtc);
}
