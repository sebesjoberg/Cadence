using System.Net;
using System.Net.Http.Headers;
using Cadence.Api.Internal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§13.3: the token scheme, and what it refuses.</summary>
public sealed class TokenAuthenticationTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    /// <summary>The first eight lowercase hex of SHA-256(Token), computed outside this codebase.</summary>
    private const string Fingerprint = "bb60af61";

    private const string ProbePath = "/probe";

    private const string PausePath = "/cadence/api/pause";

    [Fact]
    public void TheCorrectTokenMatchesAndReturnsItsFingerprint()
    {
        var tokens = new TokenSet([Token]);

        Assert.Equal(Fingerprint, tokens.Match(Token));
    }

    [Fact]
    public void AWrongTokenMatchesNothing()
    {
        var tokens = new TokenSet([Token]);

        Assert.Null(tokens.Match("not-the-token-but-the-same-length"));
    }

    [Fact]
    public async Task ACorrectTokenIsAuthenticated()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token), endpoints: MapProbe);

        var response = await host.Client.SendAsync(Request(Token, ProbePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheAuthenticatedPrincipalIsNamedForTheTokenFingerprint()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token), endpoints: MapProbe);

        var response = await host.Client.SendAsync(Request(Token, ProbePath));

        Assert.Equal($"token:{Fingerprint}|{Fingerprint}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AWrongTokenIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var response = await host.Client.SendAsync(Request("not-the-token-but-the-same-length", PausePath));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AMissingHeaderIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var response = await host.Client.GetAsync(PausePath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Basic", "dXNlcjpwYXNz")]
    [InlineData("Bearer", "")]
    public async Task AMalformedHeaderIsRefused(string scheme, string parameter)
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var request = new HttpRequestMessage(HttpMethod.Get, PausePath);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, parameter);

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokensBindFromConfiguration()
    {
        await using var host = await ApiTestHost.StartAsync(
            configuration: new Dictionary<string, string?> { ["Cadence:Api:Tokens:0"] = Token },
            endpoints: MapProbe);

        var response = await host.Client.SendAsync(Request(Token, ProbePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TokensBindFromTheEnvironmentVariableSplitOnCommas()
    {
        await using var host = await ApiTestHost.StartAsync(
            configuration: new Dictionary<string, string?>
            {
                ["CADENCE_API_TOKEN"] = $" first-token-value-32-chars-long , {Token} ,",
            },
            endpoints: MapProbe);

        var response = await host.Client.SendAsync(Request(Token, ProbePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnEmptyTokenSetInCodeIsNotAToken()
    {
        await using var host = await ApiTestHost.StartAsync(
            api => api.Tokens.Add("   "),
            environment: Environments.Development);

        Assert.Empty(host.Services.GetRequiredService<IOptions<CadenceApiOptions>>().Value.Tokens);
    }

    [Fact]
    public async Task TheSchemeIsAbsentWithNoTokenConfigured()
    {
        await using var host = await ApiTestHost.StartAsync(environment: Environments.Development);

        var schemes = await host.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();

        Assert.DoesNotContain(schemes, scheme => scheme.Name == CadenceApiDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task TheSchemeIsRegisteredOnceATokenIsConfigured()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var schemes = await host.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();

        Assert.Contains(schemes, scheme => scheme.Name == CadenceApiDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task TheBuiltInPolicyIsAbsentWithNoTokenConfigured()
    {
        await using var host = await ApiTestHost.StartAsync(environment: Environments.Development);

        var policies = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        Assert.Null(await policies.GetPolicyAsync(CadenceTokenDefaults.Policy));
    }

    [Fact]
    public async Task TheBuiltInPolicyNamesTheTokenSchemeOnceATokenIsConfigured()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var policy = await host.Services.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(CadenceTokenDefaults.Policy);

        Assert.NotNull(policy);
        Assert.Contains(CadenceApiDefaults.AuthenticationScheme, policy.AuthenticationSchemes);
    }

    // The symptom half of the finding above: a policy naming a scheme that is not registered throws
    // when it is evaluated, which would be a 500 on every request in exactly the deployments that
    // configure no token. Only a mapped route can show that, and now there is one.
    [Fact]
    public async Task WithNoTokenConfiguredAMappedRouteStillAnswers()
    {
        await using var host = await ApiTestHost.StartAsync(environment: Environments.Development);

        var response = await host.Client.GetAsync(PausePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BootLogsTheSourceAndCountButNeverTheToken()
    {
        var logs = new LogCapture();

        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token), logs: logs);

        Assert.DoesNotContain(logs.Records, record => record.Message.Contains(Token, StringComparison.Ordinal));
        Assert.Contains(logs.Records, record => record.EventId == 3002);
    }

    // The probe lives here rather than in Cadence.Api because the routes this scheme guards are
    // mapped by a later task, and a production endpoint added to satisfy a test is worse to own.
    private static void MapProbe(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(
                ProbePath,
                (HttpContext context) =>
                    $"{context.User.Identity!.Name}|{context.User.FindFirst(CadenceTokenDefaults.TokenClaim)?.Value}")
            .RequireAuthorization(CadenceTokenDefaults.Policy);

    private static HttpRequestMessage Request(string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
