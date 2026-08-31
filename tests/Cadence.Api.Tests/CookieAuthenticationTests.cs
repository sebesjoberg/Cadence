using System.Net;
using System.Net.Http.Json;
using Cadence.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// The ticket cookie and the one header that makes it count. §4.5: a cookie-authenticated request
/// is accepted only when it also carries <c>X-Cadence-Session</c>, which a cross-site form cannot
/// set and a cross-origin fetch cannot get past a preflight nothing answers.
/// </summary>
public sealed class CookieAuthenticationTests
{
    private const string JobsPath = "/cadence/api/jobs";

    [Fact]
    public async Task TheTicketCookieIsHttpOnlySecureAndLax()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();

        var response = await host.SignInAsync("u1", "Ada");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        Assert.Contains("cadence.session=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/cadence", cookie, StringComparison.OrdinalIgnoreCase);

        // No Domain, so the ticket never rides on a sibling host; and no __Host- prefix, which
        // would have mandated Path=/ and put the cookie on the host's own routes.
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__Host-", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACookieWithoutTheSessionHeaderAuthenticatesNobody()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada");

        // Cookie present, custom header absent -- the shape a cross-site request can produce.
        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ACookieWithTheSessionHeaderIsAccepted()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The milestone's headline capability: a person signs in and operates. A signed-in user always
    // carries Operate, so the write endpoints -- not just the reads -- have to answer them.
    [Fact]
    public async Task ASignedInUserSatisfiesTheOperatePolicy()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var paused = await host.Client.PutAsJsonAsync(
            "/cadence/api/pause", new PauseRequest(nameof(PauseScope.All), "by hand"));

        Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);

        // And the pause records the person, from the principal rather than the body.
        var state = await host.Client.GetFromJsonAsync<PauseResponse>("/cadence/api/pause");

        Assert.Equal(nameof(PauseScope.All), state!.Scope);
        Assert.Equal("Ada", state.SetBy);
    }

    [Fact]
    public async Task ABearerRequestNeedsNoSessionHeader()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            configure: options => options.Tokens.Add("operate-token"));
        host.Client.DefaultRequestHeaders.Add("Authorization", "Bearer operate-token");

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // OIDC alone satisfies the gate, so this host maps in Production with no token and no store.
    [Fact]
    public async Task NoCredentialAtAllIsRefusedRatherThanRedirectedToALoginPage()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();

        var response = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The framework's default paths would collide with a host's own OIDC registration.
    [Fact]
    public async Task TheHandshakeAndTheTicketBothSitUnderTheFixedPath()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();

        var cookie = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CadenceApiDefaults.CookieScheme);

        var oidc = host.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CadenceApiDefaults.OidcScheme);

        Assert.Equal("/cadence", cookie.Cookie.Path);
        Assert.Equal("/cadence/signin-oidc", oidc.CallbackPath);
        Assert.Equal("/cadence/signout-oidc", oidc.RemoteSignOutPath);
        Assert.Equal("/cadence/signout-callback-oidc", oidc.SignedOutCallbackPath);
        Assert.Equal("/cadence", oidc.SignedOutRedirectUri);
    }

    // Without openid the provider need not treat the request as an OIDC one at all.
    // A provider running in a container in development serves plain HTTP and nothing else, and the
    // handler refuses to read metadata over it. This has to be Cadence's own option rather than the
    // handler's: the framework's post-configure is what throws, and it runs before a host's.
    [Fact]
    public async Task HttpsMetadataIsRequiredUnlessTheOptionsSayOtherwise()
    {
        await using var secure = await ApiTestHost.StartWithOidcAsync();

        Assert.True(OidcOptions(secure).RequireHttpsMetadata);

        await using var relaxed = await ApiTestHost.StartWithOidcAsync(
            configure: options => options.Oidc.RequireHttpsMetadata = false);

        Assert.False(OidcOptions(relaxed).RequireHttpsMetadata);
    }

    // A compose file or an AppHost sets the authority; the same place has to be able to say it is
    // plain HTTP.
    [Fact]
    public async Task HttpsMetadataCanBeRelaxedFromConfiguration()
    {
        await using var host = await ApiTestHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Cadence:Api:Oidc:Authority"] = ApiTestHost.OidcAuthority,
            ["Cadence:Api:Oidc:ClientId"] = "cadence-tests",
            ["Cadence:Api:Oidc:RequireHttpsMetadata"] = "false",
        });

        Assert.False(OidcOptions(host).RequireHttpsMetadata);
    }

    [Theory]
    [InlineData(new[] { "profile", "email" }, new[] { "openid", "profile", "email" })]
    [InlineData(new[] { "openid", "profile" }, new[] { "openid", "profile" })]
    public async Task OpenidIsRequestedFirstWhateverWasConfigured(string[] configured, string[] expected)
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(configure: options =>
        {
            options.Oidc.Scopes.Clear();

            foreach (var scope in configured)
            {
                options.Oidc.Scopes.Add(scope);
            }
        });

        Assert.Equal(expected, OidcOptions(host).Scope);
    }

    [Fact]
    public async Task NeitherSchemeIsRegisteredWithoutAnAuthority()
    {
        await using var host = await ApiTestHost.StartAsync(
            configure: options => options.Tokens.Add("operate-token"));

        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(await schemes.GetSchemeAsync(CadenceApiDefaults.CookieScheme));
        Assert.Null(await schemes.GetSchemeAsync(CadenceApiDefaults.OidcScheme));
    }

    [Fact]
    public async Task NeitherSchemeIsRegisteredWithoutAClientId()
    {
        await using var host = await ApiTestHost.StartAsync(configure: options =>
        {
            options.Tokens.Add("operate-token");
            options.Oidc.Authority = ApiTestHost.OidcAuthority;
        });

        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(await schemes.GetSchemeAsync(CadenceApiDefaults.CookieScheme));
        Assert.Null(await schemes.GetSchemeAsync(CadenceApiDefaults.OidcScheme));
    }

    [Fact]
    public async Task BothSchemesAreRegisteredOnceTheAuthorityAndClientIdAreSet()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();

        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync(CadenceApiDefaults.CookieScheme));
        Assert.NotNull(await schemes.GetSchemeAsync(CadenceApiDefaults.OidcScheme));
    }

    [Fact]
    public async Task TheSchemesRegisterFromTheEnvironmentVariablesAlone()
    {
        await using var host = await ApiTestHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["CADENCE_OIDC_AUTHORITY"] = ApiTestHost.OidcAuthority,
            ["CADENCE_OIDC_CLIENT_ID"] = "cadence-tests",
        });

        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync(CadenceApiDefaults.CookieScheme));
        Assert.NotNull(await schemes.GetSchemeAsync(CadenceApiDefaults.OidcScheme));
    }

    [Fact]
    public async Task TheSchemesRegisterFromConfigurationAlone()
    {
        await using var host = await ApiTestHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Cadence:Api:Oidc:Authority"] = ApiTestHost.OidcAuthority,
            ["Cadence:Api:Oidc:ClientId"] = "cadence-tests",
        });

        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync(CadenceApiDefaults.CookieScheme));
        Assert.NotNull(await schemes.GetSchemeAsync(CadenceApiDefaults.OidcScheme));
    }

    [Fact]
    public async Task CallingAddApiTwiceWithOidcConfiguredStillProducesAWorkingProvider()
    {
        // Each AddApi call appends another callback that adds these two schemes, and AddScheme
        // throws on a duplicate name -- which would take the host's own authentication down too.
        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => collection.AddCadence(cadence => cadence.AddApi()));

        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var signedIn = await host.Client.GetAsync(JobsPath);

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    private static OpenIdConnectOptions OidcOptions(ApiTestHost host)
        => host.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CadenceApiDefaults.OidcScheme);
}
