using Cadence.Api.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
    /// <returns>
    /// The group the endpoints were mapped into, so a host can attach conventions of its own — rate
    /// limiting, CORS, OpenAPI metadata — to the tree it has just mounted.
    /// </returns>
    public static RouteGroupBuilder MapCadenceApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var options = services.GetRequiredService<IOptions<CadenceApiOptions>>().Value;
        var environment = services.GetRequiredService<IHostEnvironment>();

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Cadence.Api");
        var authenticated = options.PolicyName is not null || options.Tokens.Count > 0;
        var loopbackOnly = false;

        // Nested on !authenticated so that AllowUnauthenticated set alongside a token or a policy
        // says nothing: the policy below is still applied, and warning that no authentication is
        // performed would contradict what the request path actually does. The 3002 token line is
        // what tells that operator their tokens are enforced.
        if (!authenticated)
        {
            if (options.AllowUnauthenticated)
            {
                logger.MappedWithAuthenticationDisabled(options.BasePath);
            }
            else if (!environment.IsDevelopment())
            {
                throw new CadenceStartupException(GateFailureMessage);
            }
            else
            {
                logger.MappedUnauthenticatedInDevelopment(options.BasePath);
                loopbackOnly = true;
            }
        }

        // Only when tokens exist: "0 tokens" on every start of an AllowUnauthenticated deployment is
        // noise, not diagnostics. Gated on the tokens rather than on the per-source counts, because
        // a host that configures Tokens after AddApi is counted by no source and would otherwise
        // boot with a real token unannounced.
        if (options.Tokens.Count > 0)
        {
            var sources = options.TokenSources;

            logger.TokenSourcesBound(
                options.Tokens.Count,
                sources.FromCode,
                sources.FromConfiguration,
                sources.FromEnvironment);
        }

        var group = endpoints.MapGroup($"{options.BasePath.TrimEnd('/')}/api");

        // A host policy governs alone when it names one. Otherwise the built-in policy is applied
        // only when a token exists, because AddApi registers that policy on the same condition —
        // applying it without one would authenticate against a scheme that is not there, which is a
        // 500 on every request in exactly the deployments that expect none. AllowUnauthenticated
        // and the Development case apply no policy at all: that is what those flags mean.
        if (options.PolicyName is { } policyName)
        {
            group.RequireAuthorization(policyName);
        }
        else if (options.Tokens.Count > 0)
        {
            group.RequireAuthorization(CadenceTokenDefaults.Policy);
        }
        else if (loopbackOnly)
        {
            // Nothing authenticates this tree, so the network is the only boundary left. Not applied
            // to AllowUnauthenticated, where a proxy or mesh in front makes every caller legitimately
            // non-loopback -- that flag is an operator's decision and carries its own warning.
            group.AddEndpointFilter<LoopbackOnlyFilter>();
            group.ProducesProblem(StatusCodes.Status403Forbidden);
        }

        // Exactly the two branches above that apply a policy, and not the group: under
        // AllowUnauthenticated nothing authenticates, so a 401 in the document would promise a
        // response the deployment cannot send. typeof(void), not null -- the challenge carries no
        // body, and the API explorer drops a null-typed entry, which is why Produces() substitutes
        // typeof(void) itself.
        if (authenticated)
        {
            group.WithMetadata(new ProducesResponseTypeMetadata(
                StatusCodes.Status401Unauthorized,
                typeof(void)));
        }

        // Every handler with a body returns JsonHttpResult<T>, so the responses go out through the
        // package's own source-generated context — and that type contributes no metadata of its own.
        // Each route below therefore declares its statuses and shapes with .Produces<T>(), which is
        // what a host's AddOpenApi() reads; without it the document would list empty schemas.
        JobEndpoints.Map(group);
        RunEndpoints.Map(group);
        PauseEndpoints.Map(group);
        HealthEndpoints.Map(group);

        return group;
    }
}
