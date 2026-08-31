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
/// Read and edit one job's schedule. The machine-callable tree carries neither: §13.2 draws the
/// line at a token being able to start work and stop work, and only a person being able to change
/// when work happens.
/// </summary>
internal static class ScheduleEndpoints
{
    private const string Route = "/jobs/{name}/schedule";

    /// <summary>Recorded when nothing authenticated the caller, so the audit line is never blank.</summary>
    private const string AnonymousCaller = "api";

    /// <summary>Recorded when the source held no row, so the audit line reads as prose either way.</summary>
    private const string NoPreviousSchedule = "(none)";

    /// <summary>Maps the schedule pair onto an already-policied group.</summary>
    /// <param name="group">The group the operator tree mounts under.</param>
    /// <param name="requireUserPrincipal">
    /// Whether the pair requires a user principal on top of the group's own policy. False under a
    /// host-named policy, which governs alone -- the rule <c>TokenEndpoints</c> already follows.
    /// </param>
    public static void Map(IEndpointRouteBuilder group, bool requireUserPrincipal)
    {
        var read = group.MapGet(Route, GetAsync)
            .Produces<ScheduleResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        var write = group.MapPut(Route, PutAsync)
            .Produces<ScheduleResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        if (!requireUserPrincipal)
        {
            return;
        }

        // The read is gated with the write: the version it hands out is only useful to whoever may
        // spend it.
        foreach (var route in (RouteHandlerBuilder[])[read, write])
        {
            route.AddEndpointFilter<UserPrincipalFilter>()
                .WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(void)));
        }
    }

    private static async Task<Results<JsonHttpResult<ScheduleResponse>, JsonHttpResult<ProblemDetails>>> GetAsync(
        string name,
        IJobRegistry registry,
        IWritableScheduleSource schedules,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGet(name, out var descriptor) || descriptor is null)
        {
            return ProblemMapper.AsResult(ProblemMapper.JobNotFound(name, registry.All.Count));
        }

        var stored = await schedules.GetAsync(name, cancellationToken);

        return TypedResults.Json(
            Responses.ToSchedule(stored ?? Declared(descriptor)),
            CadenceApiJsonContext.Default.ScheduleResponse);
    }

    private static async Task<Results<JsonHttpResult<ScheduleResponse>, JsonHttpResult<ProblemDetails>>> PutAsync(
        string name,
        ScheduleWriteRequest request,
        ClaimsPrincipal user,
        IJobRegistry registry,
        IWritableScheduleSource schedules,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGet(name, out _))
        {
            return ProblemMapper.AsResult(ProblemMapper.JobNotFound(name, registry.All.Count));
        }

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
            // defines out of the row.
            if (!Enum.TryParse<OverlapPolicy>(declared, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
            {
                return ProblemMapper.AsResult(ProblemMapper.InvalidOverlapPolicy(declared));
            }

            overlap = parsed;
        }

        if (request.MaxDuration is { } maxDuration && maxDuration <= TimeSpan.Zero)
        {
            return ProblemMapper.AsResult(ProblemMapper.InvalidMaxDuration(maxDuration));
        }

        var previous = await schedules.GetAsync(name, cancellationToken);

        if (previous is not null && request.Version is null)
        {
            return ProblemMapper.AsResult(ProblemMapper.MissingScheduleVersion(name));
        }

        var schedule = new JobSchedule
        {
            JobName = name,
            CronExpression = request.CronExpression,

            // The resolved zone's own id, so a blank one is stored as UTC rather than as blank.
            TimeZoneId = zone!.Id,
            Enabled = request.Enabled,
            Overlap = overlap,
            MaxDuration = request.MaxDuration,
            // Absent means "I did not supply this", not "make it empty" -- the rule Version
            // follows on the same request. An empty object still clears them.
            Settings = request.Settings ?? previous?.Settings ?? ImmutableDictionary<string, string>.Empty,
            Version = request.Version ?? 0,
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

        // Read back, so the version the editor holds is the store's and not the one it sent.
        var stored = await schedules.GetAsync(name, cancellationToken) ?? schedule;

        return TypedResults.Json(
            Responses.ToSchedule(stored),
            CadenceApiJsonContext.Default.ScheduleResponse);
    }

    /// <summary>What the job declares in code, for a job the source holds no row for yet.</summary>
    /// <param name="descriptor">The registered job.</param>
    private static JobSchedule Declared(JobDescriptor descriptor) => new()
    {
        JobName = descriptor.Name,
        CronExpression = descriptor.DefaultCron ?? string.Empty,
        TimeZoneId = descriptor.DefaultTimeZone.Id,
        Enabled = descriptor.DefaultEnabled,
        Overlap = descriptor.Overlap,
        MaxDuration = descriptor.MaxDuration,
    };
}
