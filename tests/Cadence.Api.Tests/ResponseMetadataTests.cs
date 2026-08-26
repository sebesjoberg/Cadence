using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// The refusals as a host's OpenAPI document sees them. Each is declared in the gate branch that
/// can return it, so the document stays true in every configuration rather than promising a status
/// the deployment cannot produce.
/// </summary>
public sealed class ResponseMetadataTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    private const string HostPolicy = "cadence-ops";

    [Fact]
    public async Task TheBuiltInTokenPolicyDeclaresItsChallenge()
    {
        var declared = await DeclaredOnJobsAsync(api => api.Tokens.Add(Token));

        var unauthorized = declared.Single(response => response.StatusCode == StatusCodes.Status401Unauthorized);

        // typeof(void) rather than null: the API explorer drops a null-typed entry, so a null here
        // would leave the document silent about the commonest response the surface sends.
        Assert.Equal(typeof(void), unauthorized.Type);
        Assert.DoesNotContain(declared, response => response.StatusCode == StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task AHostNamedPolicyDeclaresTheSameChallenge()
    {
        var declared = await DeclaredOnJobsAsync(api => api.RequireAuthorization(HostPolicy));

        Assert.Contains(declared, response => response.StatusCode == StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task TheDevelopmentGateDeclaresTheLoopbackRefusalAsAProblemDocument()
    {
        var declared = await DeclaredOnJobsAsync(environment: Environments.Development);

        var forbidden = declared.Single(response => response.StatusCode == StatusCodes.Status403Forbidden);

        Assert.Equal(typeof(ProblemDetails), forbidden.Type);
        Assert.Contains("application/problem+json", forbidden.ContentTypes);
        Assert.DoesNotContain(declared, response => response.StatusCode == StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task AllowUnauthenticatedDeclaresNeitherRefusal()
    {
        // Nothing authenticates and nothing filters, so both statuses would be unreachable here.
        var declared = await DeclaredOnJobsAsync(api => api.AllowUnauthenticated = true);

        Assert.DoesNotContain(declared, response => response.StatusCode == StatusCodes.Status401Unauthorized);
        Assert.DoesNotContain(declared, response => response.StatusCode == StatusCodes.Status403Forbidden);
    }

    private static async Task<IReadOnlyList<IProducesResponseTypeMetadata>> DeclaredOnJobsAsync(
        Action<CadenceApiOptions>? configure = null,
        string? environment = null)
    {
        IReadOnlyList<Endpoint> built = [];

        // Read from the route builder the surface was mapped into: group conventions land on the
        // endpoints only once they are materialised, which is what reading DataSources does.
        await using var host = await ApiTestHost.StartAsync(
            configure,
            environment: environment,
            endpoints: routes => built = [.. routes.DataSources.SelectMany(source => source.Endpoints)]);

        var jobs = built
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/cadence/api/jobs");

        return [.. jobs.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()];
    }
}
