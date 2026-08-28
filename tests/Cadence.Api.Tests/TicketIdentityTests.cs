using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// What the handshake turns into a ticket: §4.2's required claim, and §4.3's allow-list.
/// </summary>
/// <remarks>
/// The handshake itself is Microsoft's code and a docker-gated test performs one for real. These
/// hand the events Cadence installed the context the handler would have handed them, which is what
/// exercises our own admission and reduction with no provider in the loop.
/// </remarks>
public sealed class TicketIdentityTests
{
    private const string Groups = "groups";

    [Fact]
    public async Task AnyUserIsAdmittedWhenNoClaimIsRequired()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();

        var context = await ValidateAsync(host, Token(("sub", "u1"), ("name", "Ada")));

        Assert.Null(context.Result);
        Assert.Equal("user", context.Principal?.FindFirst("cadence:kind")?.Value);
        Assert.Equal("Ada", context.Principal?.Identity?.Name);
    }

    [Fact]
    public async Task AConfiguredClaimTypeMustBePresentWithAnyValue()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            configure: options => options.Oidc.RequiredClaimType = Groups);

        var admitted = await ValidateAsync(
            host, Token(("sub", "u1"), (Groups, "anything"), (Groups, "and-more")));
        var refused = await ValidateAsync(host, Token(("sub", "u2")));

        Assert.Null(admitted.Result);

        // One value, not every match: a user in 200 groups would otherwise put 200 claims in the cookie.
        Assert.Equal("anything", Assert.Single(admitted.Principal!.FindAll(Groups)).Value);
        AssertRefused(refused);
    }

    [Fact]
    public async Task AConfiguredValueMustMatch()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(configure: options =>
        {
            options.Oidc.RequiredClaimType = Groups;
            options.Oidc.RequiredClaimValue = "cadence-operators";
        });

        // Two values on one claim type is the shape a group membership arrives in.
        var admitted = await ValidateAsync(
            host, Token(("sub", "u1"), (Groups, "everyone"), (Groups, "cadence-operators")));
        var refused = await ValidateAsync(host, Token(("sub", "u2"), (Groups, "everyone")));

        Assert.Null(admitted.Result);
        Assert.Equal("cadence-operators", Assert.Single(admitted.Principal!.FindAll(Groups)).Value);
        AssertRefused(refused);
    }

    // A role name is not culture-sensitive, and a near-miss must not be admitted.
    [Fact]
    public async Task AConfiguredValueIsComparedOrdinally()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(configure: options =>
        {
            options.Oidc.RequiredClaimType = Groups;
            options.Oidc.RequiredClaimValue = "cadence-operators";
        });

        var context = await ValidateAsync(host, Token(("sub", "u1"), (Groups, "Cadence-Operators")));

        AssertRefused(context);
    }

    [Fact]
    public async Task ATokenWithNoSubjectIsRefused()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();

        var context = await ValidateAsync(host, Token(("name", "Ada")));

        AssertRefused(context);
    }

    [Fact]
    public async Task TheTicketCarriesTheAllowListedClaimsAndNothingElse()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            configure: options => options.Oidc.RequiredClaimType = Groups);

        var context = await ValidateAsync(host, Token(
            ("sub", "u1"),
            ("name", "Ada"),
            ("auth_time", "1700000000"),
            (Groups, "cadence-operators"),
            ("email", "ada@example.test"),
            ("roles", "everything"),
            ("sid", "abc")));

        // sid is on the list for one reason: matching a remote sign-out against the session it names.
        Assert.Equal(
            [
                "auth_time",
                "cadence:kind",
                "cadence:scope",
                Groups,
                ClaimTypes.Name,
                ClaimTypes.NameIdentifier,
                "sid",
            ],
            context.Principal!.Claims.Select(claim => claim.Type).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ThePreferredUsernameNamesAUserWhenTheTokenCarriesNoName()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync();

        var context = await ValidateAsync(host, Token(("sub", "u1"), ("preferred_username", "ada")));

        Assert.Equal("ada", context.Principal?.Identity?.Name);
    }

    // The default is an unhandled exception, which reaches the refused person as a bare 500.
    [Fact]
    public async Task ARefusedHandshakeAnswers403AndIsLogged()
    {
        var logs = new LogCapture();
        await using var host = await ApiTestHost.StartWithOidcAsync(logs: logs);

        var options = Options(host);
        var http = new DefaultHttpContext { RequestServices = host.Services };

        await options.Events.OnRemoteFailure(new RemoteFailureContext(
            http, Scheme(), options, new InvalidOperationException("no groups claim")));

        Assert.Equal((int)HttpStatusCode.Forbidden, http.Response.StatusCode);
        Assert.True(logs.HasWarning(3101));
    }

    /// <summary>Asserts the sign-in was failed, so no cookie is ever minted for this token.</summary>
    private static void AssertRefused(TokenValidatedContext context)
    {
        Assert.NotNull(context.Result);
        Assert.False(context.Result.Succeeded);
    }

    private static async Task<TokenValidatedContext> ValidateAsync(
        ApiTestHost host, ClaimsPrincipal provider)
    {
        var options = Options(host);

        var context = new TokenValidatedContext(
            new DefaultHttpContext { RequestServices = host.Services },
            Scheme(),
            options,
            provider,
            new AuthenticationProperties());

        await options.Events.OnTokenValidated(context);

        return context;
    }

    private static OpenIdConnectOptions Options(ApiTestHost host)
        => host.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CadenceApiDefaults.OidcScheme);

    private static AuthenticationScheme Scheme()
        => new(CadenceApiDefaults.OidcScheme, displayName: null, typeof(OpenIdConnectHandler));

    /// <summary>The principal the handler builds from a validated id token, claims unmapped.</summary>
    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(
            [.. claims.Select(claim => new Claim(claim.Type, claim.Value))],
            "Bearer",
            "name",
            roleType: null));
}
