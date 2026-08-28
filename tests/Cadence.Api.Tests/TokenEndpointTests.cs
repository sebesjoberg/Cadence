using System.Net;
using System.Net.Http.Json;
using Cadence.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Api.Tests;

public sealed class TokenEndpointTests
{
    private const string UserPolicy = "cadence-tests-user";

    private const string HostPolicy = "cadence-tests-host";

    /// <summary>The header value <see cref="TestUserHandler"/> mints a principal from: subject|name.</summary>
    private const string UserHeader = "u1|Ada Lovelace";

    private static Task<ApiTestHost> StartAsync(FakeApiTokenStore? store)
        => ApiTestHost.StartAsync(
            configure: options => options.Tokens.Add("operate-token"),
            services: services =>
            {
                if (store is null)
                {
                    return;
                }

                services.AddSingleton<IApiTokenStore>(store);
                services.AddSingleton<IWritableApiTokenStore>(store);
            });

    // A host policy naming the test-only scheme, exactly the way AHostPolicyLeavesScopesToItsOwner
    // names its own probe policy -- Cadence's built-in policies stay token-only. The opt-in is what
    // mounts the tree behind a host-named policy at all.
    private static Task<ApiTestHost> StartWithUserAsync(FakeApiTokenStore store)
        => ApiTestHost.StartAsync(
            configure: options =>
            {
                options.RequireAuthorization(UserPolicy);
                options.AllowTokenAdministrationUnderHostPolicy = true;
            },
            services: services =>
            {
                services.AddSingleton<IApiTokenStore>(store);
                services.AddSingleton<IWritableApiTokenStore>(store);
                services.AddAuthorizationBuilder().AddPolicy(
                    UserPolicy,
                    policy => policy.AddAuthenticationSchemes(TestUserHandler.SchemeName).RequireAuthenticatedUser());
            },
            testUserScheme: true);

    [Fact]
    public async Task TheCreationRoutesDoNotExistWithoutAWritableStore()
    {
        await using var host = await StartAsync(store: null);

        var response = await host.Client.GetAsync("/cadence/api/tokens");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheOtherTokenRoutesAlsoDoNotExistWithoutAWritableStore()
    {
        await using var host = await StartAsync(store: null);

        var created = await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("escalation", "Operate", null));
        var revoked = await host.Client.DeleteAsync($"/cadence/api/tokens/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, created.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact]
    public async Task ATokenPrincipalCannotCreateAToken()
    {
        await using var host = await StartAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add("Authorization", "Bearer operate-token");

        var response = await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new { name = "escalation", scope = "Operate" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // A leaked read-only token able to revoke every operational credential is worse than the
    // failure scopes exist to prevent, and listing is reconnaissance even when no secret leaks.
    [Fact]
    public async Task AReadScopedTokenCannotAdministerTokensAtAll()
    {
        var store = new FakeApiTokenStore();
        var (secret, digest) = ApiTokenSecret.Create();
        await store.CreateAsync(new ApiTokenCreation("monitor", ApiTokenScope.Read, null, null, null), digest, default);

        await using var host = await StartAsync(store);
        host.Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {secret}");

        var create = await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("escalation", "Read", null));
        var list = await host.Client.GetAsync("/cadence/api/tokens");
        var revoke = await host.Client.DeleteAsync($"/cadence/api/tokens/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    // The refusal is about the principal's kind, not its scope -- an Operate token is refused too.
    [Fact]
    public async Task AnOperateScopedTokenCannotListOrRevokeTokensEither()
    {
        await using var host = await StartAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add("Authorization", "Bearer operate-token");

        var list = await host.Client.GetAsync("/cadence/api/tokens");
        var revoke = await host.Client.DeleteAsync($"/cadence/api/tokens/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    // Revoke sits behind the same filter as create, so reaching an unknown id needs a user principal.
    [Fact]
    public async Task AnUnknownIdIsNotFoundOnRevoke()
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        var response = await host.Client.DeleteAsync($"/cadence/api/tokens/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Mounting depends on the store, not on authentication, so the tree still mounts here -- but
    // with no principal at all, every route refuses.
    [Fact]
    public async Task AllowUnauthenticatedRefusesAllThreeTokenRoutesEvenThoughTheyStillMount()
    {
        var store = new FakeApiTokenStore();

        await using var host = await ApiTestHost.StartAsync(
            configure: options => options.AllowUnauthenticated = true,
            services: services =>
            {
                services.AddSingleton<IApiTokenStore>(store);
                services.AddSingleton<IWritableApiTokenStore>(store);
            });

        var list = await host.Client.GetAsync("/cadence/api/tokens");
        var create = await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("escalation", "Operate", null));
        var revoke = await host.Client.DeleteAsync($"/cadence/api/tokens/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    // Mounting and governing are independent: the store decides whether the routes can exist, the
    // host's policy decides who reaches them. An operator who named a policy for reads and triggers
    // did not ask for credential administration behind it, so the tree is absent until they say so.
    [Fact]
    public async Task AHostNamedPolicyDoesNotMountTheTokenTreeOnItsOwn()
    {
        var logs = new LogCapture();

        await using var host = await StartUnderHostPolicyAsync(new FakeApiTokenStore(), logs: logs);
        host.Client.DefaultRequestHeaders.Add("Authorization", "Bearer operate-token");

        var list = await host.Client.GetAsync("/cadence/api/tokens");
        var create = await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("escalation", "Operate", null));
        var revoke = await host.Client.DeleteAsync($"/cadence/api/tokens/{Guid.NewGuid()}");

        // 404 from routing, which is honest: the routes are not there.
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        // And the operator is told, naming the option that would mount them.
        Assert.True(logs.HasWarning(3005));
    }

    // With the opt-in, the named policy governs alone: the host has already vouched for this caller,
    // so Cadence's own kind check does not run, and a token principal reaches every route.
    [Fact]
    public async Task AHostNamedPolicyLetsItsAdmittedCallerAdministerTokensWithNoUserKind()
    {
        var store = new FakeApiTokenStore();

        await using var host = await ApiTestHost.StartAsync(
            configure: options =>
            {
                options.Tokens.Add("operate-token");
                options.RequireAuthorization(HostPolicy);
                options.AllowTokenAdministrationUnderHostPolicy = true;
            },
            services: services =>
            {
                services.AddSingleton<IApiTokenStore>(store);
                services.AddSingleton<IWritableApiTokenStore>(store);
                services.AddAuthorizationBuilder().AddPolicy(
                    HostPolicy,
                    policy => policy
                        .AddAuthenticationSchemes(CadenceApiDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser());
            });

        host.Client.DefaultRequestHeaders.Add("Authorization", "Bearer operate-token");

        var response = await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("escalation", "Operate", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ACreatedTokenReturnsTheSecretExactlyOnce()
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        var response = await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("nightly-report", "Operate", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ApiTokenCreatedResponse>();
        Assert.NotNull(created);
        Assert.Equal("nightly-report", created.Name);
        Assert.Equal(nameof(ApiTokenScope.Operate), created.Scope);
        Assert.False(string.IsNullOrEmpty(created.Token));
        Assert.Equal(
            ApiTokenSecret.Fingerprint(ApiTokenSecret.Digest(created.Token)),
            created.Fingerprint);

        var raw = await host.Client.GetStringAsync("/cadence/api/tokens");
        Assert.DoesNotContain(created.Token, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreationTakesProvenanceFromThePrincipalNotTheBody()
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens",
            new { name = "nightly-report", scope = "Read", createdByName = "someone else" });

        var list = await host.Client.GetFromJsonAsync<List<ApiTokenResponse>>("/cadence/api/tokens");
        var listed = Assert.Single(list!);
        Assert.Equal("Ada Lovelace", listed.CreatedBy);
    }

    [Fact]
    public async Task TheListNeverCarriesASecretOrADigest()
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        var created = await (await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("nightly-report", "Read", null)))
            .Content.ReadFromJsonAsync<ApiTokenCreatedResponse>();

        var list = await host.Client.GetFromJsonAsync<List<ApiTokenResponse>>("/cadence/api/tokens");

        var listed = Assert.Single(list!);
        Assert.Equal(created!.Id, listed.Id);
        Assert.Equal(created.Fingerprint, listed.Fingerprint);
        Assert.Equal(nameof(ApiTokenScope.Read), listed.Scope);
    }

    [Fact]
    public async Task ARevokedTokenIsGoneFromTheListAndRevokingItAgainIsNotFound()
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        var created = await (await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("nightly-report", "Read", null)))
            .Content.ReadFromJsonAsync<ApiTokenCreatedResponse>();

        var revoked = await host.Client.DeleteAsync($"/cadence/api/tokens/{created!.Id}");
        var revokedAgain = await host.Client.DeleteAsync($"/cadence/api/tokens/{created.Id}");
        var list = await host.Client.GetFromJsonAsync<List<ApiTokenResponse>>("/cadence/api/tokens");

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokedAgain.StatusCode);
        Assert.Empty(list!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankNameIsRejected(string? name)
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        var problem = await CreateExpectingProblemAsync(
            host, new ApiTokenRequest(name, "Operate", null), "invalid-token-name");

        Assert.NotNull(problem);
    }

    [Fact]
    public async Task AnOverlongNameIsRejected()
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        await CreateExpectingProblemAsync(
            host, new ApiTokenRequest(new string('a', 201), "Operate", null), "invalid-token-name");
    }

    [Theory]
    [InlineData("Sideways")]
    [InlineData("Read,Bogus")]
    [InlineData("2")]
    public async Task AMalformedScopeIsAnRfc9457Problem(string scope)
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        await CreateExpectingProblemAsync(
            host, new ApiTokenRequest("nightly-report", scope, null), "invalid-token-scope");
    }

    [Fact]
    public async Task AnExpiryThatIsNotInTheFutureIsRejected()
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        await CreateExpectingProblemAsync(
            host,
            new ApiTokenRequest("nightly-report", "Operate", DateTimeOffset.UtcNow.AddMinutes(-1)),
            "invalid-token-expiry");
    }

    [Fact]
    public async Task AFutureExpiryRoundTripsThroughCreationAndTheList()
    {
        await using var host = await StartWithUserAsync(new FakeApiTokenStore());
        host.Client.DefaultRequestHeaders.Add(TestUserHandler.HeaderName, UserHeader);

        var expiresAt = DateTimeOffset.UtcNow.AddDays(1);

        var created = await (await host.Client.PostAsJsonAsync(
            "/cadence/api/tokens", new ApiTokenRequest("nightly-report", "Operate", expiresAt)))
            .Content.ReadFromJsonAsync<ApiTokenCreatedResponse>();

        var list = await host.Client.GetFromJsonAsync<List<ApiTokenResponse>>("/cadence/api/tokens");
        var listed = Assert.Single(list!);

        Assert.Equal(expiresAt, created!.ExpiresAtUtc);
        Assert.Equal(expiresAt, listed.ExpiresAtUtc);
    }

    private static Task<ApiTestHost> StartUnderHostPolicyAsync(
        FakeApiTokenStore store, LogCapture? logs = null)
        => ApiTestHost.StartAsync(
            configure: options =>
            {
                options.Tokens.Add("operate-token");
                options.RequireAuthorization(HostPolicy);
            },
            services: services =>
            {
                services.AddSingleton<IApiTokenStore>(store);
                services.AddSingleton<IWritableApiTokenStore>(store);
                services.AddAuthorizationBuilder().AddPolicy(
                    HostPolicy,
                    policy => policy
                        .AddAuthenticationSchemes(CadenceApiDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser());
            },
            logs: logs);

    private static async Task<ProblemDetails?> CreateExpectingProblemAsync(
        ApiTestHost host, ApiTokenRequest request, string slug)
    {
        var response = await host.Client.PostAsJsonAsync("/cadence/api/tokens", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal($"urn:cadence:problem:{slug}", problem?.Type);

        return problem;
    }
}
