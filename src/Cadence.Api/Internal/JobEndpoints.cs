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
    /// <summary>Maps the job routes onto an already-policied group.</summary>
    /// <param name="group">The group the control surface mounts under.</param>
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapGet("/jobs", ListAsync);
        group.MapGet("/jobs/{name}", GetAsync);
        group.MapPost("/jobs/{name}/trigger", TriggerAsync);
    }

    private static async Task<Results<JsonHttpResult<TriggerResponse>, JsonHttpResult<ProblemDetails>>> TriggerAsync(
        string name,
        IJobTrigger trigger,
        IOptions<CadenceOptions> cadence,
        CancellationToken cancellationToken)
    {
        DispatchResult result;

        try
        {
            // Api, not Manual: history has to separate someone clicking from something calling us.
            result = await trigger.TriggerAsync(name, TriggerKind.Api, payload: null, cancellationToken);
        }
        catch (Exception ex) when (ProblemMapper.Describe(ex) is { } problem)
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
            return ProblemMapper.AsResult(ProblemMapper.JobNotFound(name));
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
