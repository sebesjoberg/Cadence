using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cadence.Api.Internal;

/// <summary>The run reads.</summary>
internal static class RunEndpoints
{
    /// <summary>
    /// The most rows one request can ask for. <see cref="RunQuery.Limit"/> has no ceiling of its
    /// own, which makes an unbounded limit a one-request denial against the history store.
    /// </summary>
    private const int MaxLimit = 500;

    /// <summary>Maps the run routes onto an already-policied group.</summary>
    /// <param name="group">The group the control surface mounts under.</param>
    public static void Map(IEndpointRouteBuilder group)
    {
        // The 400 carries no body: an unparseable status, from, to, limit or offset fails parameter
        // binding before any filter of ours could render a problem document (§13.2).
        group.MapGet("/runs", QueryAsync)
            .Produces<RunPageResponse>()
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/runs/{id:guid}", GetAsync)
            .Produces<RunDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Under the read policy with everything else on this group. Collecting what a run produced
        // is a read of history, not an operation -- nothing about downloading a report starts work.
        group.MapGet("/runs/{id:guid}/result", GetResultAsync)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<JsonHttpResult<RunPageResponse>> QueryAsync(
        IRunHistoryStore history,
        CancellationToken cancellationToken,
        string? job = null,
        RunStatus? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? instance = null,
        int limit = 100,
        int offset = 0)
    {
        var applied = Math.Clamp(limit, 0, MaxLimit);
        var skip = Math.Max(0, offset);

        var runs = await history.QueryAsync(
            new RunQuery
            {
                JobName = job,
                Statuses = status is { } value ? [value] : null,
                From = from,
                To = to,
                InstanceId = instance,
                Limit = applied,
                Offset = skip,

                // A list view renders no progress entries, so fetching them would be a second
                // query per row for output nobody reads.
                IncludeLog = false,
            },
            cancellationToken);

        return TypedResults.Json(
            new RunPageResponse([.. runs.Select(Responses.ToSummary)], applied, skip),
            CadenceApiJsonContext.Default.RunPageResponse);
    }

    private static async Task<Results<JsonHttpResult<RunDetailResponse>, JsonHttpResult<ProblemDetails>>> GetAsync(
        Guid id,
        IRunHistoryStore history,
        IJobResultStore results,
        CancellationToken cancellationToken)
    {
        var run = await history.GetAsync(id, cancellationToken);

        if (run is null)
        {
            return ProblemMapper.AsResult(ProblemMapper.RunNotFound(id));
        }

        // Described, never read. A run detail that streamed the bytes would make opening a page as
        // expensive as downloading everything on it.
        var result = await results.DescribeAsync(id, cancellationToken);

        return TypedResults.Json(
            Responses.ToDetail(run, result), CadenceApiJsonContext.Default.RunDetailResponse);
    }

    private static async Task<IResult> GetResultAsync(
        Guid id,
        IRunHistoryStore history,
        IJobResultStore results,
        CancellationToken cancellationToken)
    {
        // The run is checked first so a mistyped id and an expired result are told apart: one is
        // "no such run", the other is "that run's bytes have been swept".
        if (await history.GetAsync(id, cancellationToken) is null)
        {
            return ProblemMapper.AsResult(ProblemMapper.RunNotFound(id));
        }

        var stored = await results.OpenAsync(id, cancellationToken);

        return stored is null
            ? ProblemMapper.AsResult(ProblemMapper.ResultNotFound(id))
            : new JobResultDownload(stored);
    }
}
