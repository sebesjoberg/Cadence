using System.Collections.Immutable;
using Cadence.Execution;
using Cadence.Scheduling;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Cadence.Api.Internal;

/// <summary>The job reads, and the one write a token may make.</summary>
internal static class JobEndpoints
{
    /// <summary>The trigger's pattern, mapped once per tree under its own kind.</summary>
    internal const string TriggerRoute = "/jobs/{name}/trigger";

    /// <summary>Maps the job routes onto an already-policied group.</summary>
    /// <param name="group">The group the control surface mounts under.</param>
    /// <param name="requireOperate">Whether the trigger route requires Cadence's Operate policy.</param>
    public static void Map(IEndpointRouteBuilder group, bool requireOperate)
    {
        MapReads(group);
        MapTrigger(group, requireOperate);
    }

    /// <summary>Maps the reads, which every tree shares.</summary>
    /// <param name="group">The group the tree mounts under.</param>
    public static void MapReads(IEndpointRouteBuilder group)
    {
        group.MapGet("/jobs", ListAsync)
            .Produces<IReadOnlyList<JobSummaryResponse>>();

        group.MapGet("/jobs/{name}", GetAsync)
            .Produces<JobDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Maps the machine-callable trigger, which the reads are deliberately split from: this route
    /// records <see cref="TriggerKind.Api"/>, and the dashboard's own has to record
    /// <see cref="TriggerKind.Manual"/>.
    /// </summary>
    /// <param name="group">The group the tree mounts under.</param>
    /// <param name="requireOperate">Whether the route requires Cadence's Operate policy.</param>
    public static void MapTrigger(IEndpointRouteBuilder group, bool requireOperate)
        => DeclareTrigger(group.MapPost(TriggerRoute, TriggerAsync), requireOperate, requireUserPrincipal: false);

    /// <summary>
    /// Declares a trigger route's statuses and its policy, whichever tree mapped it. Shared with
    /// <see cref="UiTriggerEndpoints"/>, so the two routes cannot promise different answers.
    /// </summary>
    /// <param name="trigger">The mapped route.</param>
    /// <param name="requireOperate">Whether the route requires Cadence's Operate policy.</param>
    /// <param name="requireUserPrincipal">
    /// Whether the route requires a user principal. Always false on this tree, which is the one a
    /// machine calls and which records <see cref="TriggerKind.Api"/> for it.
    /// </param>
    internal static void DeclareTrigger(
        RouteHandlerBuilder trigger, bool requireOperate, bool requireUserPrincipal)
    {
        trigger.Produces<TriggerResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        if (requireOperate)
        {
            trigger.RequireAuthorization(CadenceTokenDefaults.OperatePolicy);
        }

        if (requireUserPrincipal)
        {
            trigger.AddEndpointFilter<UserPrincipalFilter>();
        }

        // Declared once however many of the two are on, so the document carries no duplicate row.
        if (requireOperate || requireUserPrincipal)
        {
            trigger.WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(void)));
        }
    }

    /// <summary>
    /// Starts a run and maps whatever came back. The only difference that matters between the two
    /// trees is that history has to separate someone clicking from something calling us, so the
    /// kind is the parameter and everything else — the refusals above all — is shared.
    /// </summary>
    /// <param name="name">The job to start.</param>
    /// <param name="kind">What the run is recorded as having been started by.</param>
    /// <param name="trigger">Dispatches the run.</param>
    /// <param name="registry">
    /// Counts what this replica registered, for the 404's detail. §13.6: the trigger runs in the
    /// process that received the request, so a dashboard-only replica 404s every name, and the
    /// count is what says so from the response body.
    /// </param>
    /// <param name="cadence">Supplies this instance's id, for the response.</param>
    /// <param name="cancellationToken">Cancels the dispatch.</param>
    internal static async Task<Results<JsonHttpResult<TriggerResponse>, JsonHttpResult<ProblemDetails>>> DispatchAsync(
        string name,
        TriggerKind kind,
        IJobTrigger trigger,
        IJobRegistry registry,
        IOptions<CadenceOptions> cadence,
        CancellationToken cancellationToken)
    {
        DispatchResult result;

        try
        {
            // No payload, on either tree. §13.2: accepting caller JSON would widen the route from
            // "start the job as configured" to "start the job with arbitrary input".
            result = await trigger.TriggerAsync(name, kind, payload: null, cancellationToken);
        }
        catch (Exception ex) when (ProblemMapper.Describe(ex, registry.All.Count) is { } problem)
        {
            // Filtered rather than caught wholesale: an exception the mapper does not recognise
            // propagates as a 500 instead of being flattened into a misleading problem document.
            return ProblemMapper.AsResult(problem);
        }

        return result.RunId is { } runId
            ? TypedResults.Json(
                new TriggerResponse(runId, name, cadence.Value.InstanceId),
                CadenceApiJsonContext.Default.TriggerResponse,
                statusCode: StatusCodes.Status202Accepted)
            : ProblemMapper.AsResult(ProblemMapper.Skipped(name, result));
    }

    private static Task<Results<JsonHttpResult<TriggerResponse>, JsonHttpResult<ProblemDetails>>> TriggerAsync(
        string name,
        IJobTrigger trigger,
        IJobRegistry registry,
        IOptions<CadenceOptions> cadence,
        CancellationToken cancellationToken)
        => DispatchAsync(name, TriggerKind.Api, trigger, registry, cadence, cancellationToken);

    // Resolving every schedule per request is deliberate: this is a dashboard read, not the tick
    // loop, and resolving means the answer matches what the ticker would do right now.
    private static async Task<JsonHttpResult<IReadOnlyList<JobSummaryResponse>>> ListAsync(
        IJobRegistry registry,
        ScheduleResolver resolver,
        IRunHistoryStore history,
        ISystemClock clock,
        CancellationToken cancellationToken)
    {
        var resolution = await resolver.ResolveAsync(cancellationToken);
        var now = clock.UtcNow;
        var summaries = new List<JobSummaryResponse>(registry.All.Count);

        foreach (var descriptor in registry.All)
        {
            var schedule = Scheduled(resolution, descriptor.Name);
            var lastRun = await history.GetLastRunAsync(descriptor.Name, cancellationToken);

            summaries.Add(Summarise(descriptor, schedule, lastRun, now));
        }

        return TypedResults.Json<IReadOnlyList<JobSummaryResponse>>(
            summaries,
            CadenceApiJsonContext.Default.IReadOnlyListJobSummaryResponse);
    }

    private static async Task<Results<JsonHttpResult<JobDetailResponse>, JsonHttpResult<ProblemDetails>>> GetAsync(
        string name,
        IJobRegistry registry,
        ScheduleResolver resolver,
        IRunHistoryStore history,
        ISystemClock clock,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGet(name, out var descriptor) || descriptor is null)
        {
            return ProblemMapper.AsResult(ProblemMapper.JobNotFound(name, registry.All.Count));
        }

        var resolution = await resolver.ResolveAsync(cancellationToken);
        var schedule = Scheduled(resolution, name);

        var runs = await history.QueryAsync(
            new RunQuery { JobName = name, Limit = 20, IncludeLog = false },
            cancellationToken);

        var summary = Summarise(descriptor, schedule, runs.Count > 0 ? runs[0] : null, clock.UtcNow);

        return TypedResults.Json(
            new JobDetailResponse(
                summary,
                (schedule?.Overlap ?? descriptor.Overlap).ToString(),
                schedule is null ? descriptor.MaxDuration : schedule.MaxDuration,
                schedule?.Settings ?? ImmutableDictionary<string, string>.Empty,
                [.. runs.Select(Responses.ToSummary)]),
            CadenceApiJsonContext.Default.JobDetailResponse);
    }

    // Null for a trigger-only job, and for a scheduled job whose configuration the resolver
    // reported as a problem. Both fall back to the code-declared defaults below.
    private static EffectiveSchedule? Scheduled(ScheduleResolution resolution, string name)
        => resolution.Schedules.TryGetValue(name, out var schedule) ? schedule : null;

    private static JobSummaryResponse Summarise(
        JobDescriptor descriptor,
        EffectiveSchedule? schedule,
        JobRun? lastRun,
        DateTimeOffset now) => new(
            descriptor.Name,
            schedule?.CronText ?? descriptor.DefaultCron,
            (schedule?.TimeZone ?? descriptor.DefaultTimeZone).Id,
            schedule?.Enabled ?? descriptor.DefaultEnabled,
            descriptor.AllowedTriggers.ToString(),
            Responses.Utc(schedule?.NextOccurrenceAfter(now)),
            lastRun is null ? null : Responses.ToSummary(lastRun));
}
