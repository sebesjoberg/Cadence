using System.Net;
using Cadence.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cadence.Dashboard.Tests;

/// <summary>
/// §13.3's gate, one row narrower than the machine tree's: a bearer token satisfies that gate and
/// not this one, because no browser presents one and a dashboard nobody can sign into is a UI
/// shipped open or shipped useless.
/// </summary>
public sealed class MappingGateTests
{
    private const string JobsPath = CadenceApiDefaults.UiPath + "/jobs";

    private const string Token = "s3cret-token-value-32-chars-long";

    /// <summary>Stands in for a policy the host owns, as an app with its own OIDC setup would write.</summary>
    private const string HostPolicy = "cadence-ops";

    /// <summary>The header value <see cref="TestUserHandler"/> mints a principal from: subject|name.</summary>
    private const string UserHeader = "u1|Ada Lovelace";

    /// <summary>A documentation-range address (RFC 5737), so nothing here can be mistaken for real.</summary>
    private static readonly IPAddress Remote = IPAddress.Parse("203.0.113.7");

    [Fact]
    public async Task OidcSatisfiesTheGate()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        // Mapping is the assertion: the gate throws from MapCadenceDashboard, so an answer of any
        // kind means it mapped. A 401 rather than a 200 says the cookie policy governs the tree.
        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AHostPolicySatisfiesTheGate()
    {
        await using var host = await StartUnderAHostPolicyAsync();

        var anonymous = await host.Client.GetAsync(JobsPath);

        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);
        var admitted = await host.Client.GetAsync(JobsPath);

        // The host's policy governs alone: it refuses, and it admits.
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
    }

    [Fact]
    public async Task AllowUnauthenticatedMapsAndWarnsOnEveryStart()
    {
        var logs = new LogCapture();

        await using (var first = await StartAllowingUnauthenticatedAsync(logs))
        {
            var response = await first.Client.GetAsync(JobsPath);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using var second = await StartAllowingUnauthenticatedAsync(logs);

        // Every start, not the first: an operator who scrolled past it once still has to meet it on
        // the next deploy, because nothing about the deployment has become safer in between.
        Assert.Equal(2, logs.Count(LogLevel.Warning, 3201));
    }

    [Fact]
    public async Task DevelopmentMapsAndRefusesANonLoopbackCaller()
    {
        var logs = new LogCapture();

        await using var host = await DashboardTestHost.StartAsync(
            environment: Environments.Development, logs: logs, remoteIp: Remote);

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(logs.HasWarning(3202));
    }

    [Fact]
    public async Task DevelopmentAnswersALoopbackCaller()
    {
        // The other half of that branch: the developer on localhost sees the dashboard. What the
        // filter closes is the container that shipped with ASPNETCORE_ENVIRONMENT=Development.
        await using var host = await DashboardTestHost.StartAsync(
            environment: Environments.Development, remoteIp: IPAddress.Loopback);

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TokensAloneDoNotSatisfyTheDashboardGate()
    {
        var logs = new LogCapture();

        var thrown = await Assert.ThrowsAsync<CadenceStartupException>(
            () => DashboardTestHost.StartAsync(api => api.Tokens.Add(Token), logs: logs));

        // Both remedies, because the operator holding a token has the one credential that will
        // never work here and needs to be told what does.
        Assert.Contains("Oidc", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("RequireAuthorization", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(1, logs.Count(LogLevel.Error, 3200));
    }

    [Fact]
    public async Task TokensDoNotCloseTheDevelopmentBranchEither()
    {
        // A configured token is no signal here at all, so this deployment is on the loopback branch
        // rather than on a token-authenticated one.
        var logs = new LogCapture();

        await using var host = await DashboardTestHost.StartAsync(
            api => api.Tokens.Add(Token),
            environment: Environments.Development,
            logs: logs,
            remoteIp: Remote);

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(logs.HasWarning(3202));
    }

    [Fact]
    public async Task NothingConfiguredThrows()
    {
        var thrown = await Assert.ThrowsAsync<CadenceStartupException>(
            () => DashboardTestHost.StartAsync());

        // Every remedy the message names, so the refusal carries its own way out.
        Assert.Contains("CadenceApiOptions.Oidc", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("CadenceApiOptions.RequireAuthorization", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("CadenceApiOptions.AllowUnauthenticated", thrown.Message, StringComparison.Ordinal);
    }

    private static Task<DashboardTestHost> StartAllowingUnauthenticatedAsync(LogCapture logs)
        => DashboardTestHost.StartAsync(api => api.AllowUnauthenticated = true, logs: logs);

    private static Task<DashboardTestHost> StartUnderAHostPolicyAsync()
        => DashboardTestHost.StartAsync(
            configure: api => api.RequireAuthorization(HostPolicy),
            services: collection => collection.AddAuthorizationBuilder().AddPolicy(
                HostPolicy,
                policy => policy
                    .AddAuthenticationSchemes(TestUserHandler.SchemeName)
                    .RequireAuthenticatedUser()),
            testUserScheme: true);
}
