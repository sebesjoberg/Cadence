using Cadence.Api.Internal;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Api;

/// <summary>Mounts the machine-callable tree.</summary>
public static class CadenceApiEndpointExtensions
{
    private const string GateFailureMessage =
        "MapCadenceApi() refuses to map outside Development because nothing would authenticate it. " +
        "Supply a token (CADENCE_API_TOKEN, or Cadence:Api:Tokens), or name an authorization policy " +
        "with CadenceApiOptions.RequireAuthorization, or — if something in front of this application " +
        "already authenticates callers — set CadenceApiOptions.AllowUnauthenticated.";

    /// <summary>
    /// Maps the machine-callable endpoints — trigger, reads and pause. Schedule writes are
    /// deliberately not on this tree; see §13.2.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>A group containing the mapped endpoints.</returns>
    public static IEndpointRouteBuilder MapCadenceApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var options = services.GetRequiredService<IOptions<CadenceApiOptions>>().Value;
        var environment = services.GetRequiredService<IHostEnvironment>();

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Cadence.Api");
        var authenticated = options.PolicyName is not null || options.Tokens.Count > 0;

        if (options.AllowUnauthenticated)
        {
            logger.MappedWithAuthenticationDisabled(options.BasePath);
        }
        else if (!authenticated)
        {
            if (!environment.IsDevelopment())
            {
                throw new CadenceStartupException(GateFailureMessage);
            }

            logger.MappedUnauthenticatedInDevelopment();
        }

        // Only when tokens exist: "0 tokens" on every start of an AllowUnauthenticated deployment is
        // noise, not diagnostics.
        var sources = options.TokenSources;

        if (sources.Total > 0)
        {
            logger.TokenSourcesBound(
                sources.Total,
                sources.FromCode,
                sources.FromConfiguration,
                sources.FromEnvironment);
        }

        return endpoints;
    }
}
