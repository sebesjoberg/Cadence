using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
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
        // Asserted on the body, and not only on the status. HealthCheckOptions maps Degraded to 200
        // just as it maps Healthy, so a status-only assertion here holds even with the tag predicates
        // deleted -- it would pass while proving nothing. The minimal plaintext writer emits the
        // report's own status, which is precisely what a leaked storage check changes.
        await using var host = await StartAsync(WithFailingStore);

        var live = await host.Client.GetAsync("/health/live");
        var ready = await host.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(nameof(HealthStatus.Healthy), await live.Content.ReadAsStringAsync());
        Assert.Equal(nameof(HealthStatus.Healthy), await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheProbesStayAnonymousUnderAHostFallbackPolicy()
    {
        // The one thing AllowAnonymous() defends against, and the only configuration in which it is
        // observable: a fallback policy applies to every endpoint carrying no authorization metadata
        // of its own, so without those two calls the kubelet gets 401 from a host that sets one.
        await using var host = await StartAsync(services => services.Configure<AuthorizationOptions>(
            authorization => authorization.FallbackPolicy =
                new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));

        var live = await host.Client.GetAsync("/health/live");
        var ready = await host.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public void TheTwoProbePathsCannotBeTheSame()
    {
        // Mapping both on one route is two GET endpoints on one pattern, which matches neither: an
        // AmbiguousMatchException on the probe path at request time. Refused at map time instead.
        var builder = WebApplication.CreateSlimBuilder();

        // Registered so the health-check services exist: without them MapHealthChecks throws on its
        // own, and the assertion would pass on an exception the guard never raised.
        builder.Services.AddCadence();

        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapCadenceHealth("/health", "/health"));
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

    [Theory]
    [InlineData("/health/live", "cadence.live", "cadence-live")]
    [InlineData("/health/ready", "cadence.ready", "cadence-ready")]
    public async Task EachProbeRouteSelectsTheTagCoreRegistered(string path, string tag, string check)
    {
        // Cadence.Core writes these tags and Cadence.Api's predicates read them, from two private
        // constants in two packages that no compiler links -- and MapCadenceHealth's whole guarantee
        // rests on the two agreeing. One literal here stands for both ends: a failing check under it
        // proves Api's predicate selects that tag, and Core's registration is read back off the
        // running host to prove Core writes it. Renaming either side alone turns one of the two red.
        await using var host = await StartAsync(services => services.AddHealthChecks()
            .AddCheck<AlwaysDown>("drift-sentinel", tags: [tag]));

        var response = await host.Client.GetAsync(path);
        var registrations = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(registrations, r => r.Name == check && r.Tags.Contains(tag));
    }

    [Fact]
    public async Task AHostCheckOnTheBareTagsJoinsNeitherProbe()
    {
        // The composition the ASP.NET Core documentation encourages: a host's own database check
        // tagged "ready". Namespacing the probe tags is what keeps it off them, so a store blip
        // cannot answer 503 on every replica at once.
        await using var host = await StartAsync(services => services.AddHealthChecks()
            .AddCheck<AlwaysDown>("host-database", tags: ["live", "ready"]));

        var live = await host.Client.GetAsync("/health/live");
        var ready = await host.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
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

    /// <summary>Fails hard, so a route that adopted it answers 503 rather than 200.</summary>
    private sealed class AlwaysDown : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Unhealthy("Down."));
    }

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
