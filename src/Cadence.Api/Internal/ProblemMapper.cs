using Cadence.Execution;
using Cadence.Storage;
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

    /// <summary>How much of a value the caller wrote a detail repeats back to them.</summary>
    private const int MaxEchoLength = 40;

    /// <summary>Describes a refusal that arrived as an exception, or null when it is not one of ours.</summary>
    /// <param name="exception">The exception to describe.</param>
    /// <param name="registered">
    /// How many jobs this replica has, where the caller knows it. The trigger does, and its 404 is
    /// the one §13.6 wants the count on.
    /// </param>
    public static ProblemDetails? Describe(Exception exception, int? registered = null) => exception switch
    {
        JobNotFoundException ex => JobNotFound(ex.JobName, registered),
        TriggerNotAllowedException ex => Problem(400, "trigger-not-allowed", "Trigger not allowed", ex.Message),
        SchedulerPausedException ex => Problem(409, "scheduler-paused", "Triggers are paused", ex.Message),
        ScheduleConflictException ex => ScheduleConflict(ex.JobName),
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
    /// <param name="registered">
    /// How many jobs this replica has, where the caller knows. §13.6: a replica that serves the
    /// dashboard and registers no jobs answers 404 to every name, and a count of zero is what says
    /// so from the response body rather than from somebody's deployment diagram.
    /// </param>
    public static ProblemDetails JobNotFound(string jobName, int? registered = null) => Problem(
        404,
        "job-not-found",
        "Job not found",
        $"No job is registered under the name '{jobName}'."
        + (registered is { } count
            ? $" This replica has {count} registered job(s); a replica that hosts only the " +
              "dashboard has none."
            : string.Empty));

    /// <summary>Describes a pause scope that names no combination of the defined flags.</summary>
    /// <param name="scope">The scope as the caller wrote it.</param>
    public static ProblemDetails InvalidPauseScope(string? scope) => Problem(
        400,
        "invalid-pause-scope",
        "Unknown pause scope",
        $"'{Echo(scope)}' is not a pause scope. Use None, Schedule, Triggers or All.");

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
        "(CADENCE_API_TOKEN, or Cadence:Api:Tokens), configure CadenceApiOptions.Oidc so people can " +
        "sign in, name an authorization policy with CadenceApiOptions.RequireAuthorization, or — if " +
        "something in front of this application already authenticates callers — set " +
        "CadenceApiOptions.AllowUnauthenticated.");

    /// <summary>Describes a run that no longer exists, or never did.</summary>
    /// <param name="runId">The id that matched nothing.</param>
    public static ProblemDetails RunNotFound(Guid runId) => Problem(
        404,
        "run-not-found",
        "Run not found",
        $"No run is recorded under the id '{runId}'.");

    /// <summary>Describes a run that produced nothing to collect, or whose result has aged out.</summary>
    /// <param name="runId">The run asked for.</param>
    public static ProblemDetails ResultNotFound(Guid runId) => Problem(
        404,
        "result-not-found",
        "No result to collect",
        $"Run '{runId}' has no result. Either the job produced none, or its result has passed " +
        "Retention.ResultMaxAge and been swept -- the run itself is kept far longer than what it produced.");

    /// <summary>Describes a token name that is blank or exceeds what every tier can store.</summary>
    /// <param name="name">The name as the caller wrote it.</param>
    public static ProblemDetails InvalidTokenName(string? name) => Problem(
        400,
        "invalid-token-name",
        "Invalid token name",
        $"'{Echo(name)}' is not a token name: it must be non-blank and at most 200 characters.");

    /// <summary>Describes a scope that names no combination of the defined flags.</summary>
    /// <param name="scope">The scope as the caller wrote it.</param>
    public static ProblemDetails InvalidTokenScope(string? scope) => Problem(
        400,
        "invalid-token-scope",
        "Unknown token scope",
        $"'{Echo(scope)}' is not a token scope. Use Read or Operate.");

    /// <summary>Describes an expiry that is not in the future.</summary>
    /// <param name="expiresAtUtc">The expiry as the caller wrote it.</param>
    public static ProblemDetails InvalidTokenExpiry(DateTimeOffset? expiresAtUtc) => Problem(
        400,
        "invalid-token-expiry",
        "Invalid token expiry",
        $"'{expiresAtUtc}' is not in the future.");

    /// <summary>Describes a cookie-authenticated request that carried no session header.</summary>
    public static ProblemDetails MissingSessionHeader() => Problem(
        401,
        "missing-session-header",
        "Session header required",
        "A request authenticated by the session cookie must also carry the " +
        $"{CadenceApiDefaults.SessionHeader} header.");

    /// <summary>Describes a sign-in too old to mint a token.</summary>
    /// <param name="maxAge">How recently the user must have authenticated.</param>
    /// <param name="loginPath">Where to re-authenticate, which is one redirect from here.</param>
    public static ProblemDetails StaleSession(TimeSpan maxAge, string loginPath) => Problem(
        401,
        "stale-session",
        "Sign-in too old",
        "Creating an API token requires having authenticated with the identity provider within the " +
        $"last {maxAge}. Re-authenticate at {loginPath} and retry.");

    /// <summary>Describes a cron expression the parser refused.</summary>
    /// <param name="expression">The expression as the caller wrote it.</param>
    public static ProblemDetails InvalidCron(string? expression) => Problem(
        400,
        "invalid-cron",
        "Invalid cron expression",
        $"cronExpression: '{Echo(expression)}' is not a cron expression. It needs 5 fields, or 6 " +
        "to include seconds.");

    /// <summary>Describes a timezone id this host resolves to nothing.</summary>
    /// <param name="id">The id as the caller wrote it.</param>
    public static ProblemDetails UnknownTimeZone(string? id) => Problem(
        400,
        "unknown-time-zone",
        "Unknown timezone",
        $"timeZoneId: '{Echo(id)}' is not a timezone on this host. Use an IANA id such as " +
        "'Europe/Stockholm'. A container image with InvariantGlobalization enabled resolves none " +
        "of them.");

    /// <summary>Describes an overlap policy that names no defined member.</summary>
    /// <param name="overlap">The policy as the caller wrote it.</param>
    public static ProblemDetails InvalidOverlapPolicy(string? overlap) => Problem(
        400,
        "invalid-overlap-policy",
        "Unknown overlap policy",
        $"overlap: '{Echo(overlap)}' is not an overlap policy. Use Skip or AllowConcurrent.");

    /// <summary>
    /// Describes a maximum duration that is not positive — the rule <c>[ScheduledJob]</c> and
    /// <c>JobBuilder.MaxDuration</c> enforce at startup, on the third way into a schedule. Zero
    /// cancels every run the instant it begins, and a negative value throws inside the executor.
    /// </summary>
    /// <param name="maxDuration">The duration as the caller wrote it.</param>
    public static ProblemDetails InvalidMaxDuration(TimeSpan maxDuration) => Problem(
        400,
        "invalid-max-duration",
        "Invalid maximum duration",
        $"maxDuration: {maxDuration} is not positive. Omit it for no limit, or use a form like " +
        "'00:10:00'.");

    /// <summary>Describes a schedule write that lost its optimistic-concurrency check.</summary>
    /// <param name="jobName">The job whose schedule could not be written.</param>
    public static ProblemDetails ScheduleConflict(string jobName) => Problem(
        409,
        "schedule-conflict",
        "Schedule was modified",
        $"The schedule for '{Echo(jobName)}' moved since the editor loaded it. Reload it and " +
        "reapply the change.");

    /// <summary>
    /// Describes a write over an existing row that sent no version. Refused rather than defaulted:
    /// the storage tier reads version zero as "just make it so", so accepting the omission would
    /// make forgetting the field indistinguishable from asking for last-write-wins.
    /// </summary>
    /// <param name="jobName">The job whose schedule was being written.</param>
    public static ProblemDetails MissingScheduleVersion(string jobName) => Problem(
        409,
        "schedule-conflict",
        "Schedule version required",
        $"A schedule for '{Echo(jobName)}' is already stored, so the write must carry the version " +
        "it was loaded at. Send that version, or send 0 to overwrite whatever is there.");

    /// <summary>Describes a token id that matches nothing revocable.</summary>
    /// <param name="id">The id that matched nothing.</param>
    public static ProblemDetails TokenNotFound(Guid id) => Problem(
        404,
        "token-not-found",
        "Token not found",
        $"No token is recorded under the id '{id}'.");

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

    /// <summary>
    /// A value the caller wrote, as a detail repeats it back: capped, so a refusal cannot be turned
    /// into a response of the caller's own length and content.
    /// </summary>
    /// <param name="value">The value as it arrived.</param>
    private static string Echo(string? value) => value switch
    {
        null => "(none)",
        { Length: > MaxEchoLength } => string.Concat(value.AsSpan(0, MaxEchoLength), "…"),
        _ => value,
    };

    private static ProblemDetails Problem(int status, string slug, string title, string detail) => new()
    {
        Status = status,
        Type = Base + slug,
        Title = title,
        Detail = detail,
    };
}
