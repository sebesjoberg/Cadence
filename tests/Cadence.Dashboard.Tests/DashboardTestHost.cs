using System.Net;
using System.Security.Claims;
using Cadence.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cadence.Dashboard.Tests;

/// <summary>
/// Builds a running dashboard over the in-memory defaults, so tests exercise the real gate and the
/// real operator tree. Mirrors <c>ApiTestHost</c>, mapping the dashboard alone: the two trees are
/// independently mountable, and everything asserted here has to hold without the machine tree.
/// </summary>
internal sealed class DashboardTestHost : IAsyncDisposable
{
    /// <summary>A placeholder issuer. Reserved for testing by RFC 6761, and never contacted.</summary>
    public const string OidcAuthority = "https://idp.test";

    private const string SignInPath = "/test-signin";

    private readonly IHost _host;

    private DashboardTestHost(IHost host) => _host = host;

    public HttpClient Client { get; private init; } = null!;

    public IServiceProvider Services => _host.Services;

    public static async Task<DashboardTestHost> StartAsync(
        Action<CadenceApiOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        string? environment = null,
        LogCapture? logs = null,
        IPAddress? remoteIp = null,
        Action<IEndpointRouteBuilder>? endpoints = null,
        bool testUserScheme = false)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseEnvironment(environment ?? Environments.Production);

            web.ConfigureServices(collection =>
            {
                if (logs is not null)
                {
                    collection.AddSingleton<ILoggerProvider>(logs);
                }

                collection.AddRouting();
                collection.AddCadence(cadence => cadence.AddDashboard(configure ?? (_ => { })));

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
                    routes.MapCadenceDashboard();
                    endpoints?.Invoke(routes);
                });
            });
        });

        var host = await builder.StartAsync();

        return new DashboardTestHost(host) { Client = host.GetTestClient() };
    }

    /// <summary>Starts a host with OIDC configured, so the cookie scheme registers and a user can sign in.</summary>
    /// <remarks>
    /// The handshake itself is Microsoft's code and <c>Cadence.Api.Tests</c> already covers it, so
    /// <see cref="SignInAsync"/> signs straight into the cookie scheme — which is what exercises the
    /// gate's cookie branch, the CSRF filter and the policies. <see cref="OidcAuthority"/> is never
    /// contacted.
    /// </remarks>
    /// <param name="configure">Adjusts the options, after the OIDC placeholders are set.</param>
    /// <param name="services">Adds to the container.</param>
    /// <param name="environment">The host environment.</param>
    /// <param name="logs">Collects what the host logged.</param>
    public static Task<DashboardTestHost> StartWithOidcAsync(
        Action<CadenceApiOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        string? environment = null,
        LogCapture? logs = null)
        => StartAsync(
            configure: options =>
            {
                options.Oidc.Authority = OidcAuthority;
                options.Oidc.ClientId = "cadence-tests";
                configure?.Invoke(options);
            },
            services: services,
            environment: environment,
            logs: logs,
            endpoints: routes => routes.MapGet(
                SignInPath,
                async (HttpContext context, string subject, string name) =>
                {
                    var identity = TestUserHandler.UserIdentity(
                        subject, name, CadenceApiDefaults.CookieScheme);

                    await context.SignInAsync(
                        CadenceApiDefaults.CookieScheme, new ClaimsPrincipal(identity));

                    return Results.NoContent();
                }));

    /// <summary>Signs a user into the cookie scheme and carries the ticket on every later request.</summary>
    /// <param name="subject">The user's subject.</param>
    /// <param name="name">The user's display name.</param>
    public async Task<HttpResponseMessage> SignInAsync(string subject, string name)
    {
        var query = $"{SignInPath}?subject={Uri.EscapeDataString(subject)}&name={Uri.EscapeDataString(name)}";

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
