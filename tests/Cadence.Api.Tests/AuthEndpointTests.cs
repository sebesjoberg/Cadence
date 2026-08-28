using System.Net;
using System.Net.Http.Json;
using Cadence.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>The three sign-in routes from §6, and §4.6's freshness rule on token creation.</summary>
public sealed class AuthEndpointTests
{
    private const string LoginPath = "/cadence/api/auth/login";

    private const string LogoutPath = "/cadence/api/auth/logout";

    private const string MePath = "/cadence/api/auth/me";

    private const string TokensPath = "/cadence/api/tokens";

    /// <summary>Where the freshness refusal sends a caller, which has to re-authenticate for real.</summary>
    private const string FreshLoginPath = LoginPath + "?prompt=login";

    /// <summary>The provider's back-channel sign-out path, which routing never sees.</summary>
    private const string RemoteSignOutPath = "/cadence/signout-oidc";

    [Fact]
    public async Task LoginChallengesTheProviderWithAReturnUrlUnderTheBasePath()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(recordChallenge: true);

        var response = await host.Client.GetAsync($"{LoginPath}?returnUrl=/cadence/jobs/nightly");

        // Anonymous: the one route that must answer a caller who has no ticket yet.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "/cadence/jobs/nightly",
            response.Headers.GetValues(RecordingChallengeHandler.RedirectUriHeader).Single());
    }

    [Fact]
    public async Task LoginIgnoresANonLocalReturnUrl()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(recordChallenge: true);

        var response = await host.Client.GetAsync($"{LoginPath}?returnUrl=https://evil.example/steal");

        var redirect = response.Headers.GetValues(RecordingChallengeHandler.RedirectUriHeader).Single();
        Assert.DoesNotContain("evil.example", redirect, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/cadence", redirect);
    }

    // A network-path reference is a relative URI as far as Uri.IsWellFormedUriString is concerned,
    // and a browser reads it as a host: the base-path check is what refuses it.
    [Theory]
    [InlineData("//evil.example/steal")]
    [InlineData("/somewhere/else")]
    [InlineData("https://evil.example/steal")]
    [InlineData("/cadenceevil")]
    [InlineData("/cadence/../../evil")]
    public async Task LoginIgnoresAReturnUrlOutsideTheBasePath(string returnUrl)
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(recordChallenge: true);

        var response = await host.Client.GetAsync(
            $"{LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(
            "/cadence",
            response.Headers.GetValues(RecordingChallengeHandler.RedirectUriHeader).Single());
    }

    // Login is the one route the session-header rule is not applied to.
    [Fact]
    public async Task LoginAnswersATicketHolderWithAChallenge()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(recordChallenge: true);
        await host.SignInAsync("u1", "Ada");

        var response = await host.Client.GetAsync(LoginPath);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    // The route the freshness refusal sends people to, walked end to end.
    [Fact]
    public async Task TheStaleTicketRefusalLeadsBackToAChallenge()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            store: new FakeApiTokenStore(),
            configure: options => options.Oidc.TokenCreationMaxAge = TimeSpan.Zero,
            recordChallenge: true);

        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var refused = await host.Client.PostAsJsonAsync(TokensPath, new { name = "late", scope = "Read" });
        var challenge = await host.Client.GetAsync(LoginPath);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains(
            CadenceApiDefaults.CookieScheme,
            refused.Headers.WwwAuthenticate.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
    }

    // The whole remedy, walked: the 401 names a route, that route asks the provider to authenticate
    // the user again rather than re-entering its live session, and the ticket it lands mints.
    [Fact]
    public async Task TheStaleTicketRefusalLeadsToAReAuthenticationThatCanMint()
    {
        var store = new FakeApiTokenStore();

        await using var host = await ApiTestHost.StartWithOidcAsync(
            store: store,
            discovery: new OpenIdConnectConfiguration
            {
                AuthorizationEndpoint = "https://idp.test/authorize",
            });

        await host.SignInAsync("u1", "Ada", authTime: DateTimeOffset.UtcNow.AddHours(-3));
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var refused = await host.Client.PostAsJsonAsync(TokensPath, new { name = "late", scope = "Read" });
        var problem = await refused.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains(FreshLoginPath, problem!.Detail!, StringComparison.Ordinal);

        // prompt=login on the authorization request is the part that makes it one redirect: without
        // it the provider's session answers with the same auth_time and the same refusal.
        var challenge = await host.Client.GetAsync(FreshLoginPath);

        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        Assert.Contains("prompt=login", challenge.Headers.Location?.Query, StringComparison.Ordinal);

        // What the provider sends back from that: the same user, authenticated just now.
        host.Client.DefaultRequestHeaders.Remove("Cookie");
        await host.SignInAsync("u1", "Ada", authTime: DateTimeOffset.UtcNow);

        var created = await host.Client.PostAsJsonAsync(TokensPath, new { name = "late", scope = "Read" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Single(await store.ListAsync(default));
    }

    // Asked for, never assumed: every other sign-in accepts the provider's live session, which is
    // the whole point of single sign-on.
    [Fact]
    public async Task APlainLoginAsksForNoFreshAuthentication()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            discovery: new OpenIdConnectConfiguration
            {
                AuthorizationEndpoint = "https://idp.test/authorize",
            });

        var challenge = await host.Client.GetAsync(LoginPath);

        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        Assert.DoesNotContain("prompt=", challenge.Headers.Location?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MeDescribesAUserPrincipal()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var body = await host.Client.GetFromJsonAsync<Dictionary<string, object?>>(MePath);

        Assert.Equal("user", body!["kind"]?.ToString());
        Assert.Equal("Ada", body["name"]?.ToString());
        Assert.Equal("u1", body["subject"]?.ToString());
        Assert.Equal(nameof(ApiTokenScope.Operate), body["scope"]?.ToString());
    }

    [Fact]
    public async Task MeDescribesAConfiguredToken()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            configure: options => options.Tokens.Add("operate-token"));
        host.Client.DefaultRequestHeaders.Add("Authorization", "Bearer operate-token");

        var body = await host.Client.GetFromJsonAsync<Dictionary<string, object?>>(MePath);

        Assert.Equal("token", body!["kind"]?.ToString());
        Assert.StartsWith("token:", body["name"]?.ToString(), StringComparison.Ordinal);
        Assert.Equal(nameof(ApiTokenScope.Operate), body["scope"]?.ToString());
    }

    [Fact]
    public async Task MeDescribesAStoredToken()
    {
        var store = new FakeApiTokenStore();
        var (secret, digest) = ApiTokenSecret.Create();
        await store.CreateAsync(
            new ApiTokenCreation("monitor", ApiTokenScope.Read, null, null, null), digest, default);

        await using var host = await ApiTestHost.StartWithOidcAsync(store: store);
        host.Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {secret}");

        var body = await host.Client.GetFromJsonAsync<Dictionary<string, object?>>(MePath);

        Assert.Equal("token", body!["kind"]?.ToString());
        Assert.Equal("token:monitor", body["name"]?.ToString());
        Assert.Equal(nameof(ApiTokenScope.Read), body["scope"]?.ToString());
    }

    [Fact]
    public async Task MeNamesTheCookieHolderWhereTheHostHasItsOwnDefaultScheme()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => collection
                .AddAuthentication(TestUserHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestUserHandler>(
                    TestUserHandler.SchemeName, _ => { }));

        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, "h1|Host User");

        var body = await host.Client.GetFromJsonAsync<Dictionary<string, object?>>(MePath);

        Assert.Equal("Ada", body!["name"]?.ToString());
        Assert.Equal("u1", body["subject"]?.ToString());
    }

    [Fact]
    public async Task MeRefusesACallerItCannotName()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();

        var response = await host.Client.GetAsync(MePath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LogoutClearsTheTicketCookie()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            discovery: new OpenIdConnectConfiguration());
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PostAsync(LogoutPath, content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("cadence.session=;", cookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }

    // Without this the next /auth/login is answered by the provider's still-live session and signs
    // the same user straight back in.
    [Fact]
    public async Task LogoutAlsoSignsOutOfTheProviderWhereItAdvertisesTheEndpoint()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            discovery: new OpenIdConnectConfiguration { EndSessionEndpoint = "https://idp.test/logout" });
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PostAsync(LogoutPath, content: null);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(
            "https://idp.test/logout",
            response.Headers.Location?.ToString(),
            StringComparison.Ordinal);
    }

    // Keycloak — and anything else enforcing RP-Initiated Logout strictly — refuses an end-session
    // request that names a post-logout redirect without identifying the caller, and answers the user
    // an error page instead of signing them out. SaveTokens is false, so there is no id_token_hint to
    // send and client_id is the specification's other accepted form.
    [Fact]
    public async Task LogoutIdentifiesTheClientOnTheProvidersEndSessionRequest()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            discovery: new OpenIdConnectConfiguration { EndSessionEndpoint = "https://idp.test/logout" });
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PostAsync(LogoutPath, content: null);

        var location = response.Headers.Location?.ToString();

        Assert.Contains("client_id=cadence-tests", location, StringComparison.Ordinal);
        Assert.DoesNotContain("id_token_hint", location, StringComparison.Ordinal);
    }

    // The cookie is the state that matters here: a provider that cannot be reached must not leave
    // the caller signed in with no way to sign out.
    [Fact]
    public async Task LogoutStillClearsTheCookieWhenTheProviderCannotBeReached()
    {
        var logs = new LogCapture();

        await using var host = await ApiTestHost.StartWithOidcAsync(
            logs: logs,
            services: collection => collection.PostConfigure<OpenIdConnectOptions>(
                CadenceApiDefaults.OidcScheme,
                options => options.ConfigurationManager = new UnreachableProvider()));

        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PostAsync(LogoutPath, content: null);

        // The 204 must carry the clearing cookie: reporting success while leaving the caller signed
        // in would be worse than failing the request.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("cadence.session=;", cookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(logs.HasWarning(3100));
    }

    // RemoteSignOutPath is handled inside the authentication middleware, before routing, so no
    // endpoint filter reaches it: an <img src=".../signout-oidc"> on any page would otherwise sign
    // the operator out. The handler's own sid check does not run when the request carries none.
    [Fact]
    public async Task ARemoteSignOutNamingNoSessionIsRefused()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada", sid: "provider-session-1");

        var forged = await host.Client.GetAsync(RemoteSignOutPath);

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.False(forged.Headers.Contains("Set-Cookie"));

        // And the ticket is still good, which is the part the operator would have lost.
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        Assert.Equal(
            HttpStatusCode.OK,
            (await host.Client.GetAsync("/cadence/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task ARemoteSignOutNamingSomebodyElsesSessionIsRefused()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada", sid: "provider-session-1");

        var forged = await host.Client.GetAsync($"{RemoteSignOutPath}?sid=provider-session-2");

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.False(forged.Headers.Contains("Set-Cookie"));
    }

    // The provider's own back-channel request, which names the session it is ending. sid rides in
    // the ticket's allow-list for exactly this comparison.
    [Fact]
    public async Task ARemoteSignOutNamingThisTicketsSessionClearsIt()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada", sid: "provider-session-1");

        var response = await host.Client.GetAsync($"{RemoteSignOutPath}?sid=provider-session-1");

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("cadence.session=;", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserCanMintATokenAndSeesTheSecretOnce()
    {
        var store = new FakeApiTokenStore();
        await using var host = await ApiTestHost.StartWithOidcAsync(store: store);
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var created = await host.Client.PostAsJsonAsync(
            TokensPath, new { name = "nightly", scope = "Read" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
        var secret = body!["token"]?.ToString();
        Assert.Equal(43, secret?.Length);

        var listed = await host.Client.GetFromJsonAsync<List<Dictionary<string, object?>>>(TokensPath);

        Assert.Single(listed!);
        Assert.False(listed![0].ContainsKey("token"));
        Assert.Equal("Ada", listed[0]["createdBy"]?.ToString());
    }

    [Fact]
    public async Task AStaleTicketCannotMintAToken()
    {
        var store = new FakeApiTokenStore();
        await using var host = await ApiTestHost.StartWithOidcAsync(
            store: store,
            configure: options => options.Oidc.TokenCreationMaxAge = TimeSpan.Zero);

        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PostAsJsonAsync(TokensPath, new { name = "late", scope = "Read" });

        // 401 and not 403: the fix is one redirect, and the header is what says so.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            CadenceApiDefaults.CookieScheme,
            response.Headers.WwwAuthenticate.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(default));
    }

    // The ticket is minutes old, so only the provider's own auth_time can refuse this.
    [Fact]
    public async Task AnOldAuthTimeCannotMintATokenEvenOnAFreshTicket()
    {
        var store = new FakeApiTokenStore();
        await using var host = await ApiTestHost.StartWithOidcAsync(store: store);

        await host.SignInAsync("u1", "Ada", authTime: DateTimeOffset.UtcNow.AddHours(-3));
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PostAsJsonAsync(TokensPath, new { name = "late", scope = "Read" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A provider whose discovery document cannot be read.</summary>
    private sealed class UnreachableProvider : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
            => throw new IOException("The discovery document could not be read.");

        public void RequestRefresh()
        {
        }
    }
}
