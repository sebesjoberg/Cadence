using System.ComponentModel;
using Cadence.Api.Internal;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cadence.Api.Routing;

/// <summary>
/// The operator tree, mounted by <c>Cadence.Dashboard</c>. Public only because that is a separate
/// assembly and this repo does not use <c>InternalsVisibleTo</c>; it is not a supported API.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class CadenceUiRoutes
{
    /// <summary>Mounts the operator endpoints at <see cref="CadenceApiDefaults.UiPath"/>.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="options">How the tree should be policied.</param>
    /// <returns>The group, so the caller can attach conventions of its own.</returns>
    public static RouteGroupBuilder Map(IEndpointRouteBuilder endpoints, CadenceUiMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        var services = endpoints.ServiceProvider;
        var group = endpoints.MapGroup(CadenceApiDefaults.UiPath);

        // §4.5's CSRF rule, on the tree a ticket exists to reach.
        if (options.CookiePolicy)
        {
            group.AddEndpointFilter<SessionHeaderFilter>();
        }

        // The same precedence the machine tree applies: a host-named policy governs alone, Cadence's
        // own policy stands where Cadence authenticates, and the network is the last boundary left
        // where neither does.
        if (options.PolicyName is { } policy)
        {
            group.RequireAuthorization(policy);
        }
        else if (options.CookiePolicy)
        {
            group.RequireAuthorization(CadenceTokenDefaults.ReadPolicy);
        }
        else if (options.LoopbackOnly)
        {
            group.AddEndpointFilter<LoopbackOnlyFilter>();
        }

        // The reads are the same handlers the machine tree maps: two trees, one implementation. The
        // trigger is not among them, because the dashboard's records TriggerKind.Manual.
        JobEndpoints.MapReads(group);
        RunEndpoints.Map(group);
        PauseEndpoints.Map(group, requireOperate: options.CookiePolicy);
        HealthEndpoints.Map(group);

        // Mounted on the container, as on the machine tree: no storage package means no route rather
        // than a handler that mounted and then apologised.
        if (services.GetService<IWritableApiTokenStore>() is not null)
        {
            TokenEndpoints.Map(group, requireUserPrincipal: options.PolicyName is null);
        }

        AuthMapMarker.MapOnce(endpoints);

        return group;
    }
}
