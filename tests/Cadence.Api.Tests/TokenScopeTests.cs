using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// A token's scope, enforced at the endpoints rather than inside handlers.
/// </summary>
/// <remarks>
/// The case that matters is a read-only token and pause: §13.2 puts pause on the token surface, so
/// without this a leaked monitoring credential halts scheduled work across the cluster.
/// </remarks>
public sealed class TokenScopeTests
{
    private const string ConfiguredToken = "configured-operate-token";

    /// <summary>The name every stored token here is issued under, and its audit identity.</summary>
    private const string TokenName = "nightly-report";

    /// <summary>Stands in for a policy the host owns, naming Cadence's public scheme constant.</summary>
    private const string ProbePolicy = "cadence-probe";

    private const string JobsPath = "/cadence/api/jobs";

    private const string PausePath = "/cadence/api/pause";

    [Fact]
    public async Task AReadTokenReachesTheReadEndpoints()
    {
        var (host, secret) = await StartWithTokenAsync(ApiTokenScope.Read);
        await using var lifetime = host;
        Authorize(host.Client, secret);

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AReadTokenCannotPause()
    {
        var (host, secret) = await StartWithTokenAsync(ApiTokenScope.Read);
        await using var lifetime = host;
        Authorize(host.Client, secret);

        var response = await host.Client.PutAsJsonAsync(
            PausePath, new PauseRequest(nameof(PauseScope.All), "nope"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AReadTokenCannotTrigger()
    {
        var (host, secret) = await StartWithTokenAsync(ApiTokenScope.Read);
        await using var lifetime = host;
        Authorize(host.Client, secret);

        var response = await host.Client.PostAsync($"{JobsPath}/anything/trigger", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnOperateTokenReachesTheTrigger()
    {
        // 404 from the handler, not 403 from the policy: the pair with AReadTokenCannotTrigger is
        // what shows the refusal is the scope and not a policy nothing can satisfy.
        var (host, secret) = await StartWithTokenAsync(ApiTokenScope.Operate);
        await using var lifetime = host;
        Authorize(host.Client, secret);

        var response = await host.Client.PostAsync($"{JobsPath}/anything/trigger", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnOperateTokenCanPause()
    {
        var (host, secret) = await StartWithTokenAsync(ApiTokenScope.Operate);
        await using var lifetime = host;
        Authorize(host.Client, secret);

        var response = await host.Client.PutAsJsonAsync(
            PausePath, new PauseRequest(nameof(PauseScope.Schedule), "maintenance"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AStoredTokenIsAuditedByItsName()
    {
        // The stored kind's audit identity; PauseEndpointTests pins the configuration kind's.
        var (host, secret) = await StartWithTokenAsync(ApiTokenScope.Operate);
        await using var lifetime = host;
        Authorize(host.Client, secret);

        await host.Client.PutAsJsonAsync(PausePath, new PauseRequest(nameof(PauseScope.All), "incident"));

        var state = await host.Client.GetFromJsonAsync<PauseResponse>(PausePath);

        Assert.Equal($"token:{TokenName}", state?.SetBy);
    }

    [Fact]
    public async Task AStoredTokenKeepsItsFingerprintOnTheClaim()
    {
        // Names are not unique, and the audit field records the name alone -- so the claim is the
        // only place a host policy or handler can tell two tokens called deploy apart.
        var store = new FakeApiTokenStore();
        var secret = await IssueAsync(store, ApiTokenScope.Read);

        await using var host = await ApiTestHost.StartAsync(
            configure: options => options.Tokens.Add(ConfiguredToken),
            services: services =>
            {
                services.AddSingleton<IApiTokenStore>(store);
                AddHostPolicy(services);
            },
            endpoints: routes => routes
                .MapGet("/probe", (HttpContext context) => context.User.FindFirst("cadence:token")?.Value)
                .RequireAuthorization(ProbePolicy));

        Authorize(host.Client, secret);

        Assert.Equal(
            ApiTokenSecret.Fingerprint(ApiTokenSecret.Digest(secret)),
            await host.Client.GetStringAsync("/probe"));
    }

    [Fact]
    public async Task AHostPolicyLeavesScopesToItsOwner()
    {
        // A named policy governs alone, so Cadence requires no scope under one: this read token
        // pauses, and refusing it is that policy's business rather than ours.
        var store = new FakeApiTokenStore();
        var secret = await IssueAsync(store, ApiTokenScope.Read);

        await using var host = await ApiTestHost.StartAsync(
            configure: options =>
            {
                options.Tokens.Add(ConfiguredToken);
                options.RequireAuthorization(ProbePolicy);
            },
            services: services =>
            {
                services.AddSingleton<IApiTokenStore>(store);
                AddHostPolicy(services);
            });

        Authorize(host.Client, secret);

        var response = await host.Client.PutAsJsonAsync(
            PausePath, new PauseRequest(nameof(PauseScope.All), "the host's call"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AllowUnauthenticatedBeatsAWritableStore()
    {
        // §13.3's AllowUnauthenticated row, which the writable store must not close: anonymous is
        // answered, and the warning that says so is still logged.
        var logs = new LogCapture();
        var store = new FakeApiTokenStore();

        await using var host = await ApiTestHost.StartAsync(
            configure: options => options.AllowUnauthenticated = true,
            services: services => services.AddSingleton<IApiTokenStore>(store),
            logs: logs);

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(logs.HasWarning(3001));
    }

    [Fact]
    public async Task AConfigurationTokenStillOperates()
    {
        var (host, _) = await StartWithTokenAsync(ApiTokenScope.Read);
        await using var lifetime = host;
        Authorize(host.Client, ConfiguredToken);

        var response = await host.Client.PutAsJsonAsync(
            PausePath, new PauseRequest(nameof(PauseScope.None), null));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownTokenIsUnauthorized()
    {
        var (host, _) = await StartWithTokenAsync(ApiTokenScope.Operate);
        await using var lifetime = host;
        Authorize(host.Client, ApiTokenSecret.Create().Secret);

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AWritableStoreCarriesTheSchemeWithNoConfigurationToken()
    {
        // The row this task adds: a store that can issue tokens satisfies the gate on its own.
        var store = new FakeApiTokenStore();
        var secret = await IssueAsync(store, ApiTokenScope.Operate);

        await using var host = await ApiTestHost.StartAsync(
            services: services => services.AddSingleton<IApiTokenStore>(store));

        var anonymous = await host.Client.GetAsync(JobsPath);
        Authorize(host.Client, secret);
        var authenticated = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    private static async Task<(ApiTestHost Host, string Secret)> StartWithTokenAsync(ApiTokenScope scope)
    {
        var store = new FakeApiTokenStore();
        var secret = await IssueAsync(store, scope);

        var host = await ApiTestHost.StartAsync(
            configure: options => options.Tokens.Add(ConfiguredToken),
            services: services => services.AddSingleton<IApiTokenStore>(store));

        return (host, secret);
    }

    private static async Task<string> IssueAsync(FakeApiTokenStore store, ApiTokenScope scope)
    {
        var (secret, digest) = ApiTokenSecret.Create();

        await store.CreateAsync(
            new ApiTokenCreation(TokenName, scope, null, null, null), digest, default);

        return secret;
    }

    private static void AddHostPolicy(IServiceCollection services)
        => services.AddAuthorizationBuilder().AddPolicy(
            ProbePolicy,
            policy => policy
                .AddAuthenticationSchemes(CadenceApiDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());

    private static void Authorize(HttpClient client, string secret)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
}
