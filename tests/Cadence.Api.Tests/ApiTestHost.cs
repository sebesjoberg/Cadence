using System.Globalization;
using System.Net;
using System.Security.Claims;
using Cadence.Api;
using Cadence.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Cadence.Api.Tests;

/// <summary>Builds a running control surface over the in-memory defaults, so tests exercise real routing and auth.</summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    /// <summary>A placeholder issuer. Reserved for testing by RFC 6761, and never contacted.</summary>
    public const string OidcAuthority = "https://idp.test";

    private const string SignInPath = "/test-signin";

    private readonly IHost _host;

    private ApiTestHost(IHost host) => _host = host;

    public HttpClient Client { get; private init; } = null!;

    public IServiceProvider Services => _host.Services;

    public static async Task<ApiTestHost> StartAsync(
        Action<CadenceApiOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        string? environment = null,
        LogCapture? logs = null,
        IDictionary<string, string?>? configuration = null,
        Action<IEndpointRouteBuilder>? endpoints = null,
        IPAddress? remoteIp = null,
        bool testUserScheme = false)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseEnvironment(environment ?? Environments.Production);

            web.ConfigureAppConfiguration(config =>
            {
                if (configuration is not null)
                {
                    config.AddInMemoryCollection(configuration);
                }
            });

            web.ConfigureServices(collection =>
            {
                if (logs is not null)
                {
                    collection.AddSingleton<ILoggerProvider>(logs);
                }

                collection.AddRouting();
                collection.AddCadence(cadence => cadence.AddApi(configure ?? (_ => { })));

                if (testUserScheme)
                {
                    collection.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestUserHandler>(TestUserHandler.SchemeName, _ => { });
                }

                services?.Invoke(collection);
            });

            web.Configure(app =>
            {
                // TestServer leaves RemoteIpAddress null; a real transport would have set it here.
                if (remoteIp is not null)
                {
                    app.Use((context, next) =>
                    {
                        context.Connection.RemoteIpAddress = remoteIp;
                        return next(context);
                    });
                }

                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(routes =>
                {
                    routes.MapCadenceApi();
                    routes.MapCadenceHealth();
                    endpoints?.Invoke(routes);
                });
            });
        });

        var host = await builder.StartAsync();

        return new ApiTestHost(host) { Client = host.GetTestClient() };
    }

    /// <summary>
    /// Starts a host with OIDC configured, so both the cookie and the OIDC scheme register.
    /// </summary>
    /// <remarks>
    /// The handshake itself is Microsoft's code, and standing up a provider for every assertion
    /// would test theirs rather than ours. <see cref="SignInAsync"/> therefore signs straight into
    /// the cookie scheme, which is what exercises Cadence's cookie configuration, the CSRF filter,
    /// the policies and the endpoints. One real handshake against a mock provider is a docker-gated
    /// test of its own. <see cref="OidcAuthority"/> is never contacted.
    /// </remarks>
    /// <param name="configure">Adjusts the options, after the OIDC placeholders are set.</param>
    /// <param name="services">Adds to the container.</param>
    /// <param name="store">Registered as the token store, readable and writable.</param>
    /// <param name="discovery">
    /// Served to the OIDC handler as a static discovery document, so nothing is fetched.
    /// </param>
    /// <param name="recordChallenge">
    /// Replaces the OIDC handler with <see cref="RecordingChallengeHandler"/>, which reports the
    /// properties a challenge carried instead of redirecting to a provider.
    /// </param>
    /// <param name="logs">Collects what the host logged.</param>
    /// <param name="endpoints">Maps further routes, alongside the sign-in one.</param>
    public static Task<ApiTestHost> StartWithOidcAsync(
        Action<CadenceApiOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        FakeApiTokenStore? store = null,
        OpenIdConnectConfiguration? discovery = null,
        bool recordChallenge = false,
        LogCapture? logs = null,
        Action<IEndpointRouteBuilder>? endpoints = null)
        => StartAsync(
            logs: logs,
            configure: options =>
            {
                options.Oidc.Authority = OidcAuthority;
                options.Oidc.ClientId = "cadence-tests";
                configure?.Invoke(options);
            },
            services: collection =>
            {
                if (store is not null)
                {
                    collection.AddSingleton<IApiTokenStore>(store);
                    collection.AddSingleton<IWritableApiTokenStore>(store);
                }

                if (discovery is not null)
                {
                    // Set on the manager rather than on Configuration: the framework's own
                    // post-configure has already built a network-backed manager from the authority
                    // by the time this runs, and only replacing it keeps every path offline.
                    collection.PostConfigure<OpenIdConnectOptions>(
                        CadenceApiDefaults.OidcScheme,
                        options => options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery));
                }

                if (recordChallenge)
                {
                    collection.AddTransient<RecordingChallengeHandler>();
                    collection.PostConfigure<AuthenticationOptions>(options =>
                    {
                        if (options.SchemeMap.TryGetValue(CadenceApiDefaults.OidcScheme, out var scheme))
                        {
                            scheme.HandlerType = typeof(RecordingChallengeHandler);
                        }
                    });
                }

                services?.Invoke(collection);
            },
            endpoints: routes =>
            {
                routes.MapGet(
                    SignInPath,
                    async (HttpContext context, string subject, string name, long? authTime, string? sid) =>
                    {
                        var identity = TestUserHandler.UserIdentity(
                            subject, name, CadenceApiDefaults.CookieScheme);

                        if (authTime is { } seconds)
                        {
                            identity.AddClaim(new Claim(
                                "auth_time", seconds.ToString(CultureInfo.InvariantCulture)));
                        }

                        if (sid is not null)
                        {
                            identity.AddClaim(new Claim("sid", sid));
                        }

                        await context.SignInAsync(
                            CadenceApiDefaults.CookieScheme, new ClaimsPrincipal(identity));

                        return Results.NoContent();
                    });

                endpoints?.Invoke(routes);
            });

    /// <summary>Signs a user into the cookie scheme and carries the ticket on every later request.</summary>
    /// <param name="subject">The user's subject.</param>
    /// <param name="name">The user's display name.</param>
    /// <param name="authTime">When the provider says the user authenticated, if it said.</param>
    /// <param name="sid">The provider's session identifier, if it sent one.</param>
    public async Task<HttpResponseMessage> SignInAsync(
        string subject, string name, DateTimeOffset? authTime = null, string? sid = null)
    {
        var query = $"{SignInPath}?subject={Uri.EscapeDataString(subject)}&name={Uri.EscapeDataString(name)}";

        if (authTime is { } instant)
        {
            query += $"&authTime={instant.ToUnixTimeSeconds()}";
        }

        if (sid is not null)
        {
            query += $"&sid={Uri.EscapeDataString(sid)}";
        }

        var response = await Client.GetAsync(query);
        response.EnsureSuccessStatusCode();

        // TestServer's client keeps no cookie container, so the ticket is carried forward by hand.
        var ticket = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        Client.DefaultRequestHeaders.Add("Cookie", ticket);

        return response;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
