using System.Net;
using Cadence.Storage.Conformance;
using DotNet.Testcontainers.Builders;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// One authorization-code exchange against a real provider, end to end.
/// </summary>
/// <remarks>
/// <para>
/// Every other cookie test signs straight into the cookie scheme, which leaves the handshake itself
/// — the challenge, the code exchange, the token validation and <c>OnTokenValidated</c> — untested
/// against anything but a stub. This test drives all of it.
/// </para>
/// <para>
/// The provider is <c>mock-oauth2-server</c> rather than Keycloak, which the samples use: it starts
/// in a second, needs no realm, and still serves a real discovery document, a real JWKS and signed
/// tokens.
/// </para>
/// </remarks>
public sealed class OidcHandshakeTests
{
    private const string ProviderImage = "ghcr.io/navikt/mock-oauth2-server:2.1.10";
    private const int ProviderPort = 8080;

    /// <summary>The provider's built-in issuer, one of however many a caller names.</summary>
    private const string Realm = "default";

    private const string RoleClaim = "cadence_role";
    private const string OperatorRole = "cadence-operator";

    private const string LoginPath = "/cadence/api/auth/login";
    private const string JobsPath = "/cadence/api/jobs";

    [SkippableFact]
    public async Task SigningInThroughAProviderLandsATicketTheApiAccepts()
    {
        Skip.If(DockerDaemon.SkipReason is not null, DockerDaemon.SkipReason ?? string.Empty);

        await using var provider = new ContainerBuilder(ProviderImage)
            .WithPortBinding(ProviderPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPath($"/{Realm}/.well-known/openid-configuration")
                .ForPort(ProviderPort)))
            .Build();

        await provider.StartAsync();

        var authority =
            $"http://{provider.Hostname}:{provider.GetMappedPublicPort(ProviderPort)}/{Realm}";

        await using var host = await ApiTestHost.StartAsync(
            configure: options =>
            {
                options.Oidc.Authority = authority;
                options.Oidc.ClientId = "cadence-dashboard";
                options.Oidc.ClientSecret = "handshake-secret";
                options.Oidc.RequiredClaimType = RoleClaim;
                options.Oidc.RequiredClaimValue = OperatorRole;

                // The container speaks plain HTTP, which the handler refuses to read metadata over
                // by default.
                options.Oidc.RequireHttpsMetadata = false;
            });

        // Leg one: the dashboard's own route challenges, and the redirect it writes is the
        // authorization request — built from the discovery document the container just served.
        using var challenge = await host.Client.GetAsync(LoginPath);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);

        var authorize = challenge.Headers.Location;

        Assert.NotNull(authorize);
        Assert.StartsWith(authority, authorize.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("code_challenge=", authorize.Query, StringComparison.Ordinal);

        // The correlation and nonce cookies. Both are checked at the callback, and TestServer's
        // client keeps no cookie container.
        var handshakeCookies = Cookies(challenge);

        // Leg two: the provider authenticates the user. Its login form posts back to the same
        // address, and the claims box is what puts the realm role in the token.
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        using var login = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("username", "ada"),
                new KeyValuePair<string, string>(
                    "claims",
                    $$"""{"{{RoleClaim}}": "{{OperatorRole}}", "name": "Ada Lovelace"}"""),
            ]);

        using var authorized = await browser.PostAsync(authorize, login);

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        // The handler asks for response_mode=form_post, so the provider answers with a page that
        // posts itself to the callback rather than with a redirect.
        var page = await authorized.Content.ReadAsStringAsync();

        Assert.Contains("/cadence/signin-oidc", Attribute(page, "action"), StringComparison.Ordinal);

        using var callbackForm = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("code", Hidden(page, "code")),
                new KeyValuePair<string, string>("state", Hidden(page, "state")),
            ]);

        // Leg three: the callback. The handler exchanges the code over the back channel, checks
        // the id_token against the provider's JWKS, and TicketIdentity decides who is admitted.
        using var callbackRequest = new HttpRequestMessage(HttpMethod.Post, "/cadence/signin-oidc")
        {
            Content = callbackForm,
        };

        callbackRequest.Headers.Add("Cookie", string.Join("; ", handshakeCookies));

        using var completed = await host.Client.SendAsync(callbackRequest);

        Assert.Equal(HttpStatusCode.Redirect, completed.StatusCode);
        Assert.Equal("/cadence", completed.Headers.Location?.OriginalString);

        var ticket = Cookies(completed)
            .FirstOrDefault(cookie => cookie.StartsWith("cadence.session=", StringComparison.Ordinal));

        Assert.NotNull(ticket);

        // And the ticket is one the control surface accepts, header and all.
        using var read = new HttpRequestMessage(HttpMethod.Get, JobsPath);
        read.Headers.Add("Cookie", ticket);
        read.Headers.Add(CadenceApiDefaults.SessionHeader, "1");

        using var jobs = await host.Client.SendAsync(read);

        Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
    }

    /// <summary>The name=value pair of every cookie a response set, ready to be sent back.</summary>
    private static string[] Cookies(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Select(value => value.Split(';')[0])]
            : [];

    /// <summary>The value of an attribute in the provider's callback page.</summary>
    private static string Attribute(string html, string name) => Quoted(html, $"{name}=\"");

    /// <summary>The value of one hidden input in the provider's callback page.</summary>
    private static string Hidden(string html, string name) => Quoted(html, $"name=\"{name}\" value=\"");

    private static string Quoted(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, $"The provider's callback page carries no '{marker}'.");

        start += marker.Length;

        return html[start..html.IndexOf('"', start)];
    }
}
