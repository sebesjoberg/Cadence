using System.Collections.Immutable;
using System.Security.Claims;
using Cadence.Scheduling;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Cadence.Api.Internal;

/// <summary>
/// The schedule write, which the machine-callable tree deliberately does not carry: a triggered run
/// is loud and over, a changed cron expression is silent and permanent, so only a person edits one.
/// </summary>
internal static class ScheduleEndpoints
{
    /// <summary>Recorded when nothing authenticated the caller, so the audit line is never blank.</summary>
    private const string AnonymousCaller = "api";

    /// <summary>Recorded when the source held no row, so the audit line reads as prose either way.</summary>
    private const string NoPreviousSchedule = "(none)";

    /// <summary>Maps the schedule write onto an already-policied group.</summary>
    /// <param name="group">The group the operator tree mounts under.</param>
    /// <param name="requireOperate">Whether the write requires Cadence's Operate policy.</param>
    public static void Map(IEndpointRouteBuilder group, bool requireOperate)
    {
        var write = group.MapPut("/jobs/{name}/schedule", PutAsync)
            .Produces<ScheduleResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // The pause write's rule, for a heavier write: the group's own policy admits a read-scoped
        // token, and a leaked monitoring credential must not be able to move when work happens.
        if (requireOperate)
        {
            write.RequireAuthorization(CadenceTokenDefaults.OperatePolicy)
                .WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(void)));
        }
    }

    private static async Task<Results<JsonHttpResult<ScheduleResponse>, JsonHttpResult<ProblemDetails>>> PutAsync(
        string name,
        ScheduleWriteRequest request,
        ClaimsPrincipal user,
        IWritableScheduleSource schedules,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        // Parsed here rather than at the next tick: an expression that reaches the loop unparseable
        // would throw once a second forever, and nobody would be left to tell about it.
        if (!CronParser.TryParse(request.CronExpression, out _, out _))
        {
            return ProblemMapper.AsResult(ProblemMapper.InvalidCron(request.CronExpression));
        }

        if (!CronParser.TryResolveTimeZone(request.TimeZoneId, out var zone, out _))
        {
            return ProblemMapper.AsResult(ProblemMapper.UnknownTimeZone(request.TimeZoneId));
        }

        OverlapPolicy? overlap = null;

        if (request.Overlap is { } declared)
        {
            // Enum.TryParse also accepts bare numbers, so IsDefined is what keeps a policy no member
            // defines out of the row rather than storing it and dropping it at read time.
            if (!Enum.TryParse<OverlapPolicy>(declared, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
            {
                return ProblemMapper.AsResult(ProblemMapper.InvalidOverlapPolicy(declared));
            }

            overlap = parsed;
        }

        var previous = await schedules.GetAsync(name, cancellationToken);

        var schedule = new JobSchedule
        {
            JobName = name,
            CronExpression = request.CronExpression,

            // The resolved zone's own id, so a blank one is stored as UTC rather than as blank.
            TimeZoneId = zone!.Id,
            Enabled = request.Enabled,
            Overlap = overlap,
            MaxDuration = request.MaxDuration,
            Settings = request.Settings ?? ImmutableDictionary<string, string>.Empty,
            Version = request.Version,
        };

        try
        {
            await schedules.UpsertAsync(schedule, cancellationToken);
        }
        catch (Exception ex) when (ProblemMapper.Describe(ex) is { } problem)
        {
            return ProblemMapper.AsResult(problem);
        }

        // Taken from the principal, never the body: an audit field a caller can write is an audit
        // field a caller can forge.
        loggers.CreateLogger("Cadence.Api").ScheduleChanged(
            name,
            user.Identity?.Name is { Length: > 0 } changedBy ? changedBy : AnonymousCaller,
            previous?.CronExpression ?? NoPreviousSchedule,
            schedule.CronExpression);

        // Read back, so the version the editor holds is the store's and not the one it sent -- that
        // is what makes its next write safe. A source that cannot read its own write back has
        // nothing better to offer than what we sent it.
        var stored = await schedules.GetAsync(name, cancellationToken) ?? schedule;

        return TypedResults.Json(
            Responses.ToSchedule(stored),
            CadenceApiJsonContext.Default.ScheduleResponse);
    }
}
