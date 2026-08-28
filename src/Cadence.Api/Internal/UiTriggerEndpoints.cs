using Cadence.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Cadence.Api.Internal;

/// <summary>
/// The dashboard's trigger. A route of its own rather than the machine tree's remapped, because
/// §13.2 wants history to separate someone clicking from something calling us — and only the kind
/// differs, so the dispatch and every refusal it can answer come from <see cref="JobEndpoints"/>.
/// </summary>
internal static class UiTriggerEndpoints
{
    /// <summary>Maps the operator tree's trigger onto an already-policied group.</summary>
    /// <param name="group">The group the operator tree mounts under.</param>
    /// <param name="requireOperate">Whether the route requires Cadence's Operate policy.</param>
    public static void Map(IEndpointRouteBuilder group, bool requireOperate)
        => JobEndpoints.DeclareTrigger(group.MapPost(JobEndpoints.TriggerRoute, TriggerAsync), requireOperate);

    private static Task<Results<JsonHttpResult<TriggerResponse>, JsonHttpResult<ProblemDetails>>> TriggerAsync(
        string name,
        IJobTrigger trigger,
        IOptions<CadenceOptions> cadence,
        CancellationToken cancellationToken)
        => JobEndpoints.DispatchAsync(name, TriggerKind.Manual, trigger, cadence, cancellationToken);
}
