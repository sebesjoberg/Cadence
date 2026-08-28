using Cadence.Api;
using Cadence.Api.Routing;
using Cadence.Dashboard.Internal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Dashboard;

/// <summary>Mounts the operator dashboard.</summary>
public static class CadenceDashboardEndpointExtensions
{
    private const string GateFailureMessage =
        "MapCadenceDashboard() refuses to map because nothing could sign a person in. A dashboard " +
        "needs a user principal, and a bearer token is not one -- no browser presents one. " +
        "Configure CadenceApiOptions.Oidc, or name an authorization policy with " +
        "CadenceApiOptions.RequireAuthorization. If something in front of this application already " +
        "authenticates operators, set CadenceApiOptions.AllowUnauthenticated.";

    /// <summary>
    /// Maps the operator tree the dashboard calls — the shared reads, pause and token
    /// administration — under whichever of the gate's branches this deployment is on.
    /// </summary>
    /// <remarks>
    /// The gate is the machine tree's, one row narrower: a configured token satisfies
    /// <c>MapCadenceApi()</c> and satisfies nothing here. Tokens are presented by callers with an
    /// <c>Authorization</c> header, browsers are not among them, and mapping a UI on that signal
    /// would ship an interface nobody could ever sign into.
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>
    /// The group the endpoints were mapped into, so a host can attach conventions of its own — rate
    /// limiting, CORS, response caching — to the tree it has just mounted.
    /// </returns>
    /// <exception cref="CadenceStartupException">
    /// Nothing that could authenticate a person is configured, and the environment is not
    /// Development.
    /// </exception>
    public static RouteGroupBuilder MapCadenceDashboard(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var options = services.GetRequiredService<IOptions<CadenceApiOptions>>().Value;
        var environment = services.GetRequiredService<IHostEnvironment>();

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Cadence.Dashboard");

        // The two signals that can sign a person in: the ticket cookie Cadence mints from a
        // configured provider, and a policy of the host's, which governs alone where it is named.
        //
        // The cookie half is read off the scheme AddApi registers for exactly that condition, and
        // not re-derived from the provider's settings here: CadenceOidcOptions.IsConfigured is
        // internal to Cadence.Api, and a copy of its rule that drifted would leave this tree
        // applying a cookie policy over a scheme that is not registered -- a 500 on every request.
        var cookiePolicy = services.GetRequiredService<IOptions<AuthenticationOptions>>()
            .Value.SchemeMap.ContainsKey(CadenceApiDefaults.CookieScheme);

        var authenticated = cookiePolicy || options.PolicyName is not null;

        var loopbackOnly = false;

        if (!authenticated)
        {
            if (options.AllowUnauthenticated)
            {
                logger.MappedWithAuthenticationDisabled(CadenceApiDefaults.BasePath);
            }
            else if (!environment.IsDevelopment())
            {
                // Logged as well as thrown: a startup exception reaches whoever ran the deploy,
                // and the event id reaches whoever alerts on the logs. They are rarely the same
                // person, and this is the failure worth waking both.
                logger.GateRefused(CadenceApiDefaults.BasePath);

                throw new CadenceStartupException(GateFailureMessage);
            }
            else
            {
                logger.MappedUnauthenticatedInDevelopment(CadenceApiDefaults.BasePath);
                loopbackOnly = true;
            }
        }

        return CadenceUiRoutes.Map(
            endpoints,
            new CadenceUiMapOptions
            {
                CookiePolicy = cookiePolicy,
                LoopbackOnly = loopbackOnly,
                PolicyName = options.PolicyName,
            });
    }
}
