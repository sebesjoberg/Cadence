using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§13.4: the kubelet cannot present a token, and the storage answer is not for the kubelet.</summary>
public sealed class HealthEndpointTests
{
    private const string Token = "s3cret-token-value-32-chars-long";
    private const string StorageTag = "cadence.storage";
    private const string StoreError = "The schedule database refused the connection.";

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task TheKubeletProbesAreAnonymous(string path)
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StorageHealthIsBehindTheGate()
    {
        await using var host = await StartAsync();

        var anonymous = await host.Client.GetAsync("/cadence/api/health/storage");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task StorageHealthAnswersAnAuthenticatedCaller()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get("/cadence/api/health/storage"));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ADownStoreLeavesTheKubeletProbesGreen()
    {
        // The whole point of the split, exercised end to end rather than argued in a comment: a
        // storage check that is failing must change nothing the kubelet reads.
        await using var host = await StartAsync(WithFailingStore);

        var live = await host.Client.GetAsync("/health/live");
        var ready = await host.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task StorageHealthReportsDegradedAndTheStoreError()
    {
        await using var host = await StartAsync(WithFailingStore);

        var response = await host.Client.SendAsync(Get("/cadence/api/health/storage"));

        // 200 with a Degraded body, not 503: a route that fails during the incident it exists to
        // explain is a route nobody can use.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<StorageHealthResponse>();
        Assert.NotNull(body);
        Assert.Equal(nameof(HealthStatus.Degraded), body.Status);

        var check = Assert.Single(body.Checks);
        Assert.Equal("fake-store", check.Name);
        Assert.Equal(nameof(HealthStatus.Degraded), check.Status);
        Assert.Equal(StoreError, check.Error);
    }

    [Fact]
    public async Task StorageHealthCarriesOnlyTheStorageChecks()
    {
        // Filtered by tag rather than reporting everything, so the liveness and readiness entries do
        // not leak onto a route whose contract is the store.
        await using var host = await StartAsync(WithFailingStore);

        var response = await host.Client.SendAsync(Get("/cadence/api/health/storage"));

        var body = await response.Content.ReadFromJsonAsync<StorageHealthResponse>();
        Assert.NotNull(body);
        Assert.DoesNotContain(body.Checks, check => check.Name is "cadence-live" or "cadence-ready");
    }

    [Fact]
    public async Task TheProbePathsAreConfigurable()
    {
        await using var host = await StartAsync(endpoints: routes => routes.MapCadenceHealth("/up", "/in"));

        var live = await host.Client.GetAsync("/up");
        var ready = await host.Client.GetAsync("/in");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    private static void WithFailingStore(IServiceCollection services) => services.AddHealthChecks()
        .AddCheck<UnreachableStore>("fake-store", tags: [StorageTag]);

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static Task<ApiTestHost> StartAsync(
        Action<IServiceCollection>? services = null,
        Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>? endpoints = null) =>
        ApiTestHost.StartAsync(api => api.Tokens.Add(Token), services, endpoints: endpoints);

    /// <summary>Stands in for a storage tier that is down, so the split can be tested without one.</summary>
    private sealed class UnreachableStore : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Degraded(
                "The schedule database did not answer.", new InvalidOperationException(StoreError)));
    }
}
