using Cadence.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Api.Internal;

/// <summary>
/// §13.2's status table, in one place. Every status the control surface returns for a refused
/// request is decided here, so the table and the code cannot drift.
/// </summary>
internal static class ProblemMapper
{
    // A URN, not an http URL: RFC 9457 wants an identifier, and we host no documentation to point
    // at. One constant to change if the package is ever renamed.
    private const string Base = "urn:cadence:problem:";

    /// <summary>Describes a refusal that arrived as an exception, or null when it is not one of ours.</summary>
    /// <param name="exception">The exception to describe.</param>
    public static ProblemDetails? Describe(Exception exception) => exception switch
    {
        JobNotFoundException ex => JobNotFound(ex.JobName),
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

    /// <summary>Describes a name that matches no registered job.</summary>
    /// <param name="jobName">The name that was not found.</param>
    public static ProblemDetails JobNotFound(string jobName) => Problem(
        404,
        "job-not-found",
        "Job not found",
        $"No job is registered under the name '{jobName}'.");

    /// <summary>Describes a pause scope that names no combination of the defined flags.</summary>
    /// <param name="scope">The scope as the caller wrote it.</param>
    public static ProblemDetails InvalidPauseScope(string? scope) => Problem(
        400,
        "invalid-pause-scope",
        "Unknown pause scope",
        $"'{scope}' is not a pause scope. Use None, Schedule, Triggers or All.");

    /// <summary>
    /// Describes a caller refused because the surface is mounted on the Development branch of the
    /// gate, which authenticates nobody. The remedy is in the detail: whoever meets this is more
    /// likely to be scanning a misconfigured container than holding the deployment's own runbook.
    /// </summary>
    public static ProblemDetails NotLoopback() => Problem(
        403,
        "not-loopback",
        "Loopback callers only",
        "Cadence's API is mapped with nothing that would authenticate it, which is allowed in " +
        "Development only, so it answers loopback callers alone. Configure a token " +
        "(CADENCE_API_TOKEN, or Cadence:Api:Tokens), name an authorization policy with " +
        "CadenceApiOptions.RequireAuthorization, or — if something in front of this application " +
        "already authenticates callers — set CadenceApiOptions.AllowUnauthenticated.");

    /// <summary>Describes a run that no longer exists, or never did.</summary>
    /// <param name="runId">The id that matched nothing.</param>
    public static ProblemDetails RunNotFound(Guid runId) => Problem(
        404,
        "run-not-found",
        "Run not found",
        $"No run is recorded under the id '{runId}'.");

    /// <summary>
    /// Renders a problem as a response. Serialized through the control surface's own JSON context,
    /// so the wire shape is the library's and not whatever the host has configured globally.
    /// </summary>
    /// <param name="problem">The problem to return.</param>
    public static JsonHttpResult<ProblemDetails> AsResult(ProblemDetails problem) => TypedResults.Json(
        problem,
        CadenceApiJsonContext.Default.ProblemDetails,
        contentType: "application/problem+json",

        // A refusal with no status is a bug here rather than a success; 200 would bury it.
        statusCode: problem.Status ?? StatusCodes.Status500InternalServerError);

    private static ProblemDetails Problem(int status, string slug, string title, string detail) => new()
    {
        Status = status,
        Type = Base + slug,
        Title = title,
        Detail = detail,
    };
}
