using System.Net;
using System.Net.Http.Json;
using Cadence.Api.Routing;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// The seam <c>Cadence.Dashboard</c> mounts. The operator tree is the machine tree's own read
/// handlers under a different policy, so the two cannot drift apart.
/// </summary>
public sealed class UiRoutesTests
{
    private const string JobsPath = CadenceApiDefaults.UiPath + "/jobs";

    private const string PausePath = CadenceApiDefaults.UiPath + "/pause";

    private const string TokensPath = CadenceApiDefaults.UiPath + "/tokens";

    private const string LoginPattern = CadenceApiDefaults.ApiPath + "/auth/login";

    /// <summary>The header value <see cref="TestUserHandler"/> mints a principal from: subject|name.</summary>
    private const string UserHeader = "u1|Ada Lovelace";

    /// <summary>Stands in for a policy the host owns, as an app with its own OIDC setup would write.</summary>
    private const string HostPolicy = "cadence-ops";

    /// <summary>A documentation-range address (RFC 5737), so nothing here can be mistaken for real.</summary>
    private static readonly IPAddress Remote = IPAddress.Parse("203.0.113.7");

    private static readonly CadenceUiMapOptions Open =
        new() { CookiePolicy = false, LoopbackOnly = false };

    private static readonly CadenceUiMapOptions Cookie =
        new() { CookiePolicy = true, LoopbackOnly = false };

    private static readonly CadenceUiMapOptions UnderHostPolicy =
        new() { CookiePolicy = false, LoopbackOnly = false, PolicyName = HostPolicy };

    [Fact]
    public async Task TheSharedReadsAnswerOnTheOperatorTree()
    {
        await using var host = await StartAsync(Open);

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheOperatorTreeMountsTheReadsAndNotTheTrigger()
    {
        IReadOnlyList<Endpoint> built = [];

        await using var host = await StartAsync(Open, built: endpoints => built = endpoints);

        Assert.Equal(
            [
                "GET /cadence/ui/health/storage",
                "GET /cadence/ui/jobs",
                "GET /cadence/ui/jobs/{name}",
                "GET /cadence/ui/pause",
                "GET /cadence/ui/runs",
                "GET /cadence/ui/runs/{id:guid}",
                "PUT /cadence/ui/pause",
            ],
            Routes(built, CadenceApiDefaults.UiPath));

        // Splitting the job routes left the machine tree's trigger where it was. The dashboard's
        // own trigger is a separate route, because history has to separate a person clicking from
        // something calling us.
        Assert.Contains(
            "POST /cadence/api/jobs/{name}/trigger", Routes(built, CadenceApiDefaults.ApiPath));
    }

    [Fact]
    public async Task TheTokenRoutesMountWhereAStoreCanPersistThem()
    {
        IReadOnlyList<Endpoint> built = [];
        var store = new FakeApiTokenStore();

        await using var host = await StartAsync(
            Open,
            services: collection =>
            {
                collection.AddSingleton<IApiTokenStore>(store);
                collection.AddSingleton<IWritableApiTokenStore>(store);
            },
            built: endpoints => built = endpoints);

        var routes = Routes(built, CadenceApiDefaults.UiPath);

        // The trailing slash is what MapPost("") on the /tokens group produces; routing matches the
        // path with and without it.
        Assert.Contains("POST /cadence/ui/tokens/", routes);
        Assert.Contains("GET /cadence/ui/tokens/", routes);
        Assert.Contains("DELETE /cadence/ui/tokens/{id:guid}", routes);
    }

    [Fact]
    public async Task TheSignInRoutesAreMappedOnceWhenBothTreesMount()
    {
        IReadOnlyList<Endpoint> built = [];

        await using var host = await ApiTestHost.StartWithOidcAsync(
            endpoints: routes =>
            {
                CadenceUiRoutes.Map(routes, Cookie);
                built = [.. routes.DataSources.SelectMany(source => source.Endpoints)];
            });

        // A second mapping is an ambiguous match at request time, not a startup failure.
        Assert.Single(
            built.OfType<RouteEndpoint>(), endpoint => endpoint.RoutePattern.RawText == LoginPattern);
    }

    [Fact]
    public void TheOperatorTreeMapsTheSignInRoutesWhenItMountsAlone()
    {
        IEndpointRouteBuilder app = BuildApp();

        CadenceUiRoutes.Map(app, Cookie);

        var built = app.DataSources.SelectMany(source => source.Endpoints).ToList();

        Assert.Single(
            built.OfType<RouteEndpoint>(), endpoint => endpoint.RoutePattern.RawText == LoginPattern);
    }

    [Fact]
    public async Task TheOperatorTreeRefusesANonLoopbackCallerWhenNothingAuthenticatesIt()
    {
        await using var host = await StartAsync(
            new CadenceUiMapOptions { CookiePolicy = false, LoopbackOnly = true },
            remoteIp: Remote);

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ACookieWithoutTheSessionHeaderIsRefusedOnTheOperatorTree()
    {
        await using var host = await StartWithOidcAsync(Cookie);
        await host.SignInAsync("u1", "Ada");

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ASignedInUserReadsTheOperatorTree()
    {
        await using var host = await StartWithOidcAsync(Cookie);
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The cookie tree's one write carries Operate, the same pair the machine tree applies.
    [Fact]
    public async Task ASignedInUserPausesFromTheOperatorTree()
    {
        await using var host = await StartWithOidcAsync(Cookie);
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PutAsJsonAsync(
            PausePath, new PauseRequest(nameof(PauseScope.All), "by hand"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ANamedPolicyGovernsTheOperatorTreeAlone()
    {
        await using var host = await StartAsync(
            new CadenceUiMapOptions { CookiePolicy = false, LoopbackOnly = false, PolicyName = HostPolicy },
            configure: api => api.RequireAuthorization(HostPolicy),
            services: collection =>
            {
                collection.AddSingleton<IApiTokenStore>(new FakeApiTokenStore());
                collection.AddAuthorizationBuilder().AddPolicy(
                    HostPolicy,
                    policy => policy
                        .AddAuthenticationSchemes(CadenceApiDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser());
            });

        // A 401 and not a 200: the tree took the host's policy rather than mounting open.
        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // §13.5: mounting depends on the store, governing depends on the policy, and a deployment that
    // named a policy for reads and pause never consented to credential administration behind it.
    [Fact]
    public async Task TokenAdministrationIsNotMountedUnderAHostPolicyWithoutTheOptIn()
    {
        var logs = new LogCapture();

        await using var host = await StartUnderAHostPolicyAsync(
            UnderHostPolicy, new FakeApiTokenStore(), logs: logs);

        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        var list = await host.Client.GetAsync(TokensPath);
        var create = await host.Client.PostAsJsonAsync(
            TokensPath, new ApiTokenRequest("escalation", "Operate", null));

        // 404 from routing, to a caller the host's own policy admits: the routes are not there.
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);

        // And the operator is told, naming the option that would mount them.
        Assert.True(logs.HasWarning(3005));
    }

    [Fact]
    public async Task TokenAdministrationMountsUnderAHostPolicyWithTheOptIn()
    {
        await using var host = await StartUnderAHostPolicyAsync(
            UnderHostPolicy, new FakeApiTokenStore(), allowTokenAdministration: true);

        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        var response = await host.Client.GetAsync(TokensPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The dashboard's own shape: a host policy for who may look, and the CSRF filter still wanted.
    // Cadence's Operate policy must not be added on top -- the named policy governs alone, and a
    // principal it admits carries no scope claim of Cadence's.
    [Fact]
    public async Task PausingIsReachableUnderAHostPolicyThatAlsoKeepsTheCookieRule()
    {
        await using var host = await StartUnderAHostPolicyAsync(
            new CadenceUiMapOptions { CookiePolicy = true, LoopbackOnly = false, PolicyName = HostPolicy },
            new FakeApiTokenStore());

        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PutAsJsonAsync(
            PausePath, new PauseRequest(nameof(PauseScope.All), "by hand"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static Task<ApiTestHost> StartAsync(
        CadenceUiMapOptions options,
        Action<CadenceApiOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        IPAddress? remoteIp = null,
        Action<IReadOnlyList<Endpoint>>? built = null,
        LogCapture? logs = null,
        bool testUserScheme = false)
        => ApiTestHost.StartAsync(
            configure ?? (api => api.AllowUnauthenticated = true),
            services,
            logs: logs,
            remoteIp: remoteIp,
            testUserScheme: testUserScheme,
            endpoints: routes =>
            {
                CadenceUiRoutes.Map(routes, options);
                built?.Invoke([.. routes.DataSources.SelectMany(source => source.Endpoints)]);
            });

    /// <summary>
    /// A host-named policy over the test-only user scheme, the way <c>TokenEndpointTests</c> writes
    /// one. The machine tree is left on <c>AllowUnauthenticated</c> so that it mounts its own token
    /// routes without a host policy, and the 3005 line under test can only have come from this tree.
    /// </summary>
    private static Task<ApiTestHost> StartUnderAHostPolicyAsync(
        CadenceUiMapOptions options,
        FakeApiTokenStore store,
        bool allowTokenAdministration = false,
        LogCapture? logs = null)
        => StartAsync(
            options,
            configure: api =>
            {
                api.AllowUnauthenticated = true;
                api.AllowTokenAdministrationUnderHostPolicy = allowTokenAdministration;
            },
            services: collection =>
            {
                collection.AddSingleton<IApiTokenStore>(store);
                collection.AddSingleton<IWritableApiTokenStore>(store);
                collection.AddAuthorizationBuilder().AddPolicy(
                    HostPolicy,
                    policy => policy
                        .AddAuthenticationSchemes(TestUserHandler.SchemeName)
                        .RequireAuthenticatedUser());
            },
            logs: logs,
            testUserScheme: true);

    private static Task<ApiTestHost> StartWithOidcAsync(CadenceUiMapOptions options)
        => ApiTestHost.StartWithOidcAsync(endpoints: routes => CadenceUiRoutes.Map(routes, options));

    /// <summary>Every method-and-pattern pair under a prefix, ordered so it can be compared whole.</summary>
    private static string[] Routes(IReadOnlyList<Endpoint> built, string prefix) =>
        [.. built
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(prefix, StringComparison.Ordinal) == true)
            .SelectMany(
                endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                (endpoint, method) => $"{method} {endpoint.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)];

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Services.AddCadence(cadence => cadence.AddApi(api =>
        {
            api.Oidc.Authority = ApiTestHost.OidcAuthority;
            api.Oidc.ClientId = "cadence-tests";
        }));

        return builder.Build();
    }
}
