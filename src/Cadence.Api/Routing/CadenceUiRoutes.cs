using System.ComponentModel;
using Cadence.Api.Internal;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        var api = services.GetRequiredService<IOptions<CadenceApiOptions>>().Value;
        var group = endpoints.MapGroup(CadenceApiDefaults.UiPath);

        // A host-named policy governs alone, which is what leaves scopes to Cadence's own policies
        // and to the token tree's user-principal check -- the machine tree's rule, applied here so
        // the two trees cannot disagree about what a named policy means.
        var cadencePolicies = options.PolicyName is null && options.CookiePolicy;

        // §4.5's CSRF rule, on the tree a ticket exists to reach.
        if (options.CookiePolicy)
        {
            group.AddEndpointFilter<SessionHeaderFilter>();
        }

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
            // Nothing authenticates this tree, so the network is the only boundary left.
            group.AddEndpointFilter<LoopbackOnlyFilter>();
            group.ProducesProblem(StatusCodes.Status403Forbidden);
        }

        // Exactly the two branches above that apply a policy: the third authenticates nobody, so a
        // 401 in the document would promise a response the deployment cannot send. typeof(void),
        // because the challenge carries no body -- the machine tree declares it the same way.
        if (options.PolicyName is not null || options.CookiePolicy)
        {
            group.WithMetadata(new ProducesResponseTypeMetadata(
                StatusCodes.Status401Unauthorized,
                typeof(void)));
        }

        // The reads are the same handlers the machine tree maps: two trees, one implementation. The
        // trigger is not among them, because the dashboard's records TriggerKind.Manual.
        JobEndpoints.MapReads(group);
        RunEndpoints.Map(group);
        PauseEndpoints.Map(group, requireOperate: cadencePolicies);
        HealthEndpoints.Map(group);

        MapTokenAdministration(group, services, api, options);

        // Mounted on capability, the way /tokens is: no writable source means the route is absent
        // and routing answers 404, rather than a handler that mounted and then apologised. No
        // opt-in gate on top -- editing a schedule is what this tree exists for, and unlike
        // credential administration it grants no reach beyond the jobs the tree already shows.
        if (services.GetService<IWritableScheduleSource>() is not null)
        {
            ScheduleEndpoints.Map(group, requireOperate: cadencePolicies);
        }

        AuthMapMarker.MapOnce(endpoints);

        return group;
    }

    /// <summary>
    /// Mounts create, list and revoke -- on the container, and under a host-named policy only where
    /// that host asked for it.
    /// </summary>
    /// <remarks>
    /// The machine tree's rule, and for its reason (§13.5): mounting depends on the store and
    /// governing depends on the policy, and a deployment that named a policy for reads and pause
    /// never consented to credential administration behind it. The two trees read one operator
    /// statement, so the opt-in cannot be given to one tree and withheld from the other.
    /// </remarks>
    /// <param name="group">The already-policied operator group.</param>
    /// <param name="services">The application's container.</param>
    /// <param name="api">The control surface's options, which carry the opt-in.</param>
    /// <param name="options">How the tree was policied.</param>
    private static void MapTokenAdministration(
        RouteGroupBuilder group,
        IServiceProvider services,
        CadenceApiOptions api,
        CadenceUiMapOptions options)
    {
        // No storage package means no route rather than a handler that mounted and then apologised.
        if (services.GetService<IWritableApiTokenStore>() is null)
        {
            return;
        }

        if (options.PolicyName is null)
        {
            TokenEndpoints.Map(group, requireUserPrincipal: true);
        }
        else if (api.AllowTokenAdministrationUnderHostPolicy)
        {
            TokenEndpoints.Map(group, requireUserPrincipal: false);
        }
        else
        {
            // The same 3005 line the machine tree writes, naming this tree: silence would leave an
            // operator with three routes that 404 and nothing to read about why.
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Cadence.Api")
                .TokenAdministrationNotMounted(CadenceApiDefaults.UiPath, options.PolicyName);
        }
    }
}
