using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§13.3: the token scheme, and what it refuses.</summary>
public sealed class TokenAuthenticationTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    /// <summary>The first eight lowercase hex of SHA-256(Token), computed outside this codebase.</summary>
    private const string Fingerprint = "bb60af61";

    /// <summary>The claim a host's own policy or handler would read the token off, as a string.</summary>
    private const string TokenClaim = "cadence:token";

    private const string ProbePath = "/probe";

    private const string PausePath = "/cadence/api/pause";

    /// <summary>Stands in for a policy the host owns, naming Cadence's public scheme constant.</summary>
    private const string ProbePolicy = "cadence-probe";

    /// <summary>A host default scheme that authenticates nobody, so the token's route matters.</summary>
    private const string HostDefaultScheme = "HostDefault";

    [Fact]
    public async Task ACorrectTokenIsAuthenticated()
    {
        await using var host = await StartWithProbeAsync(api => api.Tokens.Add(Token));

        var response = await host.Client.SendAsync(Request(Token, ProbePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheAuthenticatedPrincipalIsNamedForTheTokenFingerprint()
    {
        await using var host = await StartWithProbeAsync(api => api.Tokens.Add(Token));

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
        await using var host = await StartWithProbeAsync(
            configuration: new Dictionary<string, string?> { ["Cadence:Api:Tokens:0"] = Token });

        var response = await host.Client.SendAsync(Request(Token, ProbePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TokensBindFromTheEnvironmentVariableSplitOnCommas()
    {
        await using var host = await StartWithProbeAsync(
            configuration: new Dictionary<string, string?>
            {
                ["CADENCE_API_TOKEN"] = $" first-token-value-32-chars-long , {Token} ,",
            });

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
    public async Task CallingAddApiTwiceStillProducesAWorkingProvider()
    {
        // Every AddApi call appends another IConfigureOptions<AuthenticationOptions> that calls
        // AddScheme, and AddScheme throws on a duplicate name. Unguarded, a second call fails host
        // startup and takes the host's own app-wide authentication down with it.
        await using var host = await StartWithProbeAsync(
            api => api.Tokens.Add(Token),
            services => services.AddCadence(cadence => cadence.AddApi()));

        var authenticated = await host.Client.SendAsync(Request(Token, ProbePath));
        var anonymous = await host.Client.GetAsync(PausePath);

        // The token path still works, and still refuses: a guard that dropped the scheme instead of
        // the duplicate would boot cleanly and authenticate nobody.
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // The built-in policy has to name the token scheme rather than lean on the default one, or a
    // host with its own default scheme authenticates that scheme on Cadence's routes and every token
    // holder gets a 401. The host default here deliberately authenticates nobody, so a 200 can only
    // come from the token scheme being named.
    [Fact]
    public async Task TheBuiltInPolicyNamesTheTokenSchemeEvenWhenTheHostHasAnotherDefault()
    {
        await using var host = await ApiTestHost.StartAsync(
            api => api.Tokens.Add(Token),
            services => services
                .AddAuthentication(HostDefaultScheme)
                .AddScheme<AuthenticationSchemeOptions, NeverAuthenticates>(HostDefaultScheme, _ => { }));

        var authenticated = await host.Client.SendAsync(Request(Token, PausePath));
        var anonymous = await host.Client.GetAsync(PausePath);

        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // A policy naming a scheme that is not registered throws when it is evaluated, which would be a
    // 500 on every request in exactly the deployments that configure no token.
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

    // The probe lives here rather than in Cadence.Api because a production endpoint added to satisfy
    // a test is worse to own. Its policy is the host's, not Cadence's built-in one.
    private static void MapProbe(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(
                ProbePath,
                (HttpContext context) =>
                    $"{context.User.Identity!.Name}|{context.User.FindFirst(TokenClaim)?.Value}")
            .RequireAuthorization(ProbePolicy);

    private static Task<ApiTestHost> StartWithProbeAsync(
        Action<CadenceApiOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        IDictionary<string, string?>? configuration = null) =>
        ApiTestHost.StartAsync(
            configure,
            collection =>
            {
                collection.AddAuthorizationBuilder().AddPolicy(
                    ProbePolicy,
                    policy => policy
                        .AddAuthenticationSchemes(CadenceApiDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser());
                services?.Invoke(collection);
            },
            configuration: configuration,
            endpoints: MapProbe);

    private static HttpRequestMessage Request(string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private sealed class NeverAuthenticates(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
