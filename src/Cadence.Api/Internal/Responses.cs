using Cadence.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadence.Api.Internal;

/// <summary>
/// The one place a storage record becomes a response. Every endpoint file projects through this, so
/// a storage type cannot reach the wire by way of a route that wrote its own mapping.
/// </summary>
/// <remarks>
/// Every instant is normalized to UTC on the way out. A store is free to record an offset of its
/// own — and <see cref="DateTimeOffset"/> would keep the instant correct either way — but a field
/// named <c>...Utc</c> serializing as <c>+02:00</c> is a wire contract contradicting itself.
/// </remarks>
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
        Utc(run.ScheduledFor),
        run.StartedAt.ToUniversalTime(),
        Utc(run.CompletedAt),
        run.Duration,
        run.Error);

    /// <summary>Projects a run together with the progress it reported.</summary>
    /// <param name="run">The recorded run.</param>
    public static RunDetailResponse ToDetail(JobRun run) => new(
        ToSummary(run),
        [.. run.Log.Select(entry => new LogEntryResponse(entry.Timestamp.ToUniversalTime(), entry.Message))]);

    /// <summary>Projects the cluster-wide pause switches.</summary>
    /// <param name="state">The stored pause state.</param>
    public static PauseResponse ToPause(PauseState state) => new(
        state.Scope.ToString(),
        state.Reason,
        state.SetBy,
        Utc(state.SetAtUtc));

    /// <summary>Projects a health report as the storage answer.</summary>
    /// <param name="report">The report, already filtered to the storage checks.</param>
    public static StorageHealthResponse ToStorageHealth(HealthReport report) => new(
        report.Status.ToString(),
        [.. report.Entries.Select(entry => new StorageCheckResponse(
            entry.Key,
            entry.Value.Status.ToString(),
            entry.Value.Description,

            // The message, not the whole exception. A stack trace on this route would be a stack
            // trace on a dashboard, and the message is what names the host that stopped answering.
            entry.Value.Exception?.Message,
            entry.Value.Duration))]);

    /// <summary>Normalizes an optional instant to UTC, so a <c>...Utc</c> field is one.</summary>
    /// <param name="instant">The instant, or null.</param>
    public static DateTimeOffset? Utc(DateTimeOffset? instant) => instant?.ToUniversalTime();
}
