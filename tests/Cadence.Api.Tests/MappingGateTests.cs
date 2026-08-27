using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// §13.3: the API refuses to mount when nothing would authenticate it, and refuses at map time so
/// the failure lands on a deploy rather than on whoever finds the open endpoint first.
/// </summary>
public sealed class MappingGateTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    /// <summary>Stands in for a policy the host owns, as an app with its own OIDC setup would write.</summary>
    private const string HostPolicy = "cadence-ops";

    /// <summary>A documentation-range address (RFC 5737), so nothing here can be mistaken for real.</summary>
    private static readonly IPAddress Remote = IPAddress.Parse("203.0.113.7");

    [Fact]
    public void MappingOutsideDevelopmentWithNothingConfiguredThrows()
    {
        var app = BuildApp(Environments.Production);

        Assert.Throws<CadenceStartupException>(() => app.MapCadenceApi());
    }

    [Fact]
    public void MappingInDevelopmentWithNothingConfiguredIsAllowed()
    {
        var app = BuildApp(Environments.Development);

        app.MapCadenceApi();
    }

    [Fact]
    public void AConfiguredTokenSatisfiesTheGateInProduction()
    {
        var app = BuildApp(Environments.Production, api => api.Tokens.Add("s3cret-token-value-32-chars-long"));

        app.MapCadenceApi();
    }

    [Fact]
    public void AllowUnauthenticatedSatisfiesTheGateInProduction()
    {
        var app = BuildApp(Environments.Production, api => api.AllowUnauthenticated = true);

        app.MapCadenceApi();
    }

    [Fact]
    public void ANamedPolicySatisfiesTheGateInProduction()
    {
        var app = BuildApp(Environments.Production, api => api.RequireAuthorization(HostPolicy));

        app.MapCadenceApi();
    }

    [Fact]
    public void MappingInDevelopmentWithNothingConfiguredWarns()
    {
        var logs = new LogCapture();
        var app = BuildApp(Environments.Development, logs: logs);

        app.MapCadenceApi();

        Assert.True(logs.HasWarning(3000));
    }

    [Fact]
    public void AllowUnauthenticatedWarnsOnEveryStart()
    {
        var logs = new LogCapture();
        var app = BuildApp(Environments.Production, api => api.AllowUnauthenticated = true, logs);

        app.MapCadenceApi();

        Assert.True(logs.HasWarning(3001));
    }

    [Fact]
    public async Task ANamedPolicyGovernsRequestsAndTheTokenSchemeAuthenticatesIntoIt()
    {
        // The composition §13.3 spends the most words on, proven at request time rather than at map
        // time: a host's own policy naming CadenceApiDefaults.AuthenticationScheme, alongside a
        // configured token, so the token scheme authenticates into that policy instead of bypassing
        // it. Without a token the scheme is unregistered and evaluating the policy is a 500, which is
        // why this configuration needs both halves.
        await using var host = await ApiTestHost.StartAsync(
            api =>
            {
                api.Tokens.Add(Token);
                api.RequireAuthorization(HostPolicy);
            },
            services => services.AddAuthorizationBuilder().AddPolicy(
                HostPolicy,
                policy => policy
                    .AddAuthenticationSchemes(CadenceApiDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()),
            remoteIp: Remote);

        var authorized = Request("GET", "/cadence/api/jobs");
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var authenticated = await host.Client.SendAsync(authorized);
        var anonymous = await host.Client.GetAsync("/cadence/api/jobs");

        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/cadence/api/jobs")]
    [InlineData("POST", "/cadence/api/jobs/" + ApiTestJobs.NightlyName + "/trigger")]
    [InlineData("PUT", "/cadence/api/pause")]
    public async Task ANonLoopbackCallerIsRefusedOnTheDevelopmentGate(string method, string path)
    {
        // A container that shipped with ASPNETCORE_ENVIRONMENT=Development is on the one branch of
        // the gate that authenticates nobody, and it is reachable. The writes are the reason this
        // matters: POST /trigger runs any registered job, PUT /pause halts scheduling cluster-wide.
        await using var host = await StartOnTheDevelopmentGate(Remote);

        var response = await host.Client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheRefusalNamesTheRemedy()
    {
        await using var host = await StartOnTheDevelopmentGate(Remote);

        var response = await host.Client.SendAsync(Request("PUT", "/cadence/api/pause"));

        // Through ProblemMapper, like every other refusal, and carrying all three ways out: whoever
        // meets this is likelier to be scanning the port than holding the deployment's runbook.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(403, problem.Status);
        Assert.Contains("CADENCE_API_TOKEN", problem.Detail!, StringComparison.Ordinal);
        Assert.Contains("RequireAuthorization", problem.Detail!, StringComparison.Ordinal);
        Assert.Contains("AllowUnauthenticated", problem.Detail!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task ALoopbackCallerIsAnsweredOnTheDevelopmentGate(string address)
    {
        // The developer's own machine, including the IPv4-mapped form a dual-stack listener reports.
        await using var host = await StartOnTheDevelopmentGate(IPAddress.Parse(address));

        var response = await host.Client.GetAsync("/cadence/api/pause");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnAbsentRemoteAddressIsTreatedAsLoopback()
    {
        // Deliberate: Kestrel over TCP always fills RemoteIpAddress in, so nothing arriving over the
        // network is null. Null means a transport with no IP peer -- this in-memory host, a Unix
        // socket, a named pipe -- and refusing those would close nothing while breaking every one.
        await using var host = await StartOnTheDevelopmentGate(remoteIp: null);

        var response = await host.Client.GetAsync("/cadence/api/pause");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AllowUnauthenticatedAnswersANonLoopbackCaller()
    {
        // Not filtered: an operator who set this has an authenticating proxy or an mTLS mesh in
        // front, where every legitimate caller is non-loopback. The 3001 warning is that path's gate.
        await using var host = await ApiTestHost.StartAsync(
            api => api.AllowUnauthenticated = true,
            remoteIp: Remote);

        var response = await host.Client.GetAsync("/cadence/api/pause");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheTokenPathAnswersANonLoopbackCallerHoldingTheToken()
    {
        // Not filtered either: these requests are authenticated, and a filter here would break every
        // real deployment. The refusal for a caller without the token stays 401, not 403.
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token), remoteIp: Remote);

        var authorized = Request("GET", "/cadence/api/pause");
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var authenticated = await host.Client.SendAsync(authorized);
        var anonymous = await host.Client.GetAsync("/cadence/api/pause");

        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static Task<ApiTestHost> StartOnTheDevelopmentGate(IPAddress? remoteIp) =>
        ApiTestHost.StartAsync(environment: Environments.Development, remoteIp: remoteIp);

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        // PUT /pause binds a body. Endpoint filters run after argument binding, so without one the
        // framework answers 400 and the filter under test never runs.
        if (request.Method == HttpMethod.Put)
        {
            request.Content = JsonContent.Create(new PauseRequest(nameof(PauseScope.All), "probing"));
        }

        return request;
    }

    private static WebApplication BuildApp(
        string environment,
        Action<CadenceApiOptions>? configure = null,
        LogCapture? logs = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });

        // CreateSlimBuilder reads the machine environment, so without this an ambient
        // CADENCE_API_TOKEN on a developer's workstation would decide the gate's answer.
        foreach (var source in builder.Configuration.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .ToList())
        {
            builder.Configuration.Sources.Remove(source);
        }

        if (logs is not null)
        {
            builder.Services.AddSingleton<ILoggerProvider>(logs);
        }

        builder.Services.AddCadence(cadence => cadence.AddApi(configure ?? (_ => { })));

        return builder.Build();
    }
}
