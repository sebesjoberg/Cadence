using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cadence.Api.Internal;

/// <summary>
/// Maps the sign-in routes once, whichever tree asks first. Both trees need them and both may be
/// mounted; a second mapping would be an ambiguous-match failure at request time.
/// </summary>
internal sealed class AuthMapMarker
{
    /// <summary>Where the routes sit whichever tree mapped them, so a bundle can bake the path.</summary>
    private const string AuthPath = CadenceApiDefaults.ApiPath + "/auth";

    private bool _mapped;

    /// <summary>Maps the sign-in routes, if OIDC is configured and nothing has mapped them yet.</summary>
    /// <param name="endpoints">The route builder, which also carries the marker's container.</param>
    public static void MapOnce(IEndpointRouteBuilder endpoints)
    {
        var services = endpoints.ServiceProvider;
        var options = services.GetRequiredService<IOptions<CadenceApiOptions>>().Value;

        if (!options.Oidc.IsConfigured)
        {
            return;
        }

        var marker = services.GetRequiredService<AuthMapMarker>();

        if (marker._mapped)
        {
            return;
        }

        marker._mapped = true;

        // A sibling group, because login has to answer a caller who has no ticket yet and /me
        // authenticates its own caller: neither can sit behind either tree's policy.
        AuthEndpoints.Map(endpoints.MapGroup(AuthPath), CadenceApiDefaults.BasePath);
    }
}
