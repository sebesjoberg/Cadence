using Cadence.Execution;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Api.Internal;

/// <summary>
/// §13.2's status table, in one place. Every status the control surface returns for a refused
/// request is decided here, so the table and the code cannot drift.
/// </summary>
internal static class ProblemMapper
{
    private const string Base = "https://cadence.dev/problems/";

    /// <summary>Describes a refusal that arrived as an exception, or null when it is not one of ours.</summary>
    /// <param name="exception">The exception to describe.</param>
    public static ProblemDetails? Describe(Exception exception) => exception switch
    {
        JobNotFoundException ex => Problem(404, "job-not-found", "Job not found", ex.Message),
        TriggerNotAllowedException ex => Problem(400, "trigger-not-allowed", "Trigger not allowed", ex.Message),
        SchedulerPausedException ex => Problem(409, "scheduler-paused", "Triggers are paused", ex.Message),
        _ => null,
    };

    /// <summary>
    /// Describes a dispatch that started nothing. Not an error and not a success — answering 200
    /// with an empty body would tell a caller a run started when none did.
    /// </summary>
    /// <param name="jobName">The job that was not started.</param>
    /// <param name="result">The skipped result, carrying its reason.</param>
    public static ProblemDetails Skipped(string jobName, DispatchResult result) => Problem(
        409,
        "run-skipped",
        "No run was started",
        $"'{jobName}' was not started: {result.SkipReason}");

    private static ProblemDetails Problem(int status, string slug, string title, string detail) => new()
    {
        Status = status,
        Type = Base + slug,
        Title = title,
        Detail = detail,
    };
}
