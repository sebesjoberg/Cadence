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
    /// <summary>
    /// Where the sign-in routes sit whichever tree mapped them: under the machine tree's prefix,
    /// never the caller's, so the path does not depend on which tree asked first.
    /// </summary>
    private const string AuthPath = CadenceApiDefaults.ApiPath + "/auth";

    private bool _mapped;

    /// <summary>Maps the sign-in routes, if OIDC is configured and nothing has mapped them yet.</summary>
    /// <param name="endpoints">
    /// The route builder, which also carries the marker's container. It must be the application's
    /// root builder: "mapped already" binds to the container, while the routes bind to whichever
    /// builder called first, so mapping through a nested group would put them under its prefix and
    /// silence every later caller.
    /// </param>
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
