using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cadence.Api;
using Cadence.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Cadence.Dashboard.Tests;

/// <summary>
/// The document the browser loads, and the files it then asks for. Both are served anonymously and
/// by routing rather than by middleware: an operator has to be able to load the application before
/// anything can sign them in, and a host should not have to order a static-file call correctly for
/// a package's routes to work.
/// </summary>
public sealed class ShellTests
{
    private const string DeepRoute = CadenceApiDefaults.BasePath + "/jobs/x";

    /// <summary>Matches the bootstrap the shell substitutes into its inline script.</summary>
    private static readonly Regex Bootstrap =
        new(@"window\.__cadence = (?<json>\{.*\});", RegexOptions.Compiled);

    /// <summary>Matches the hashed entry module Vite names in the emitted document.</summary>
    private static readonly Regex AssetReference =
        new(@"src=""(?<url>/cadence/assets/[^""]+)""", RegexOptions.Compiled);

    [Theory]
    [InlineData(CadenceApiDefaults.BasePath)]
    [InlineData(DeepRoute)]
    public async Task TheShellIsServedAnonymouslyAsHtml(string path)
    {
        // OIDC, so the operator tree is behind a cookie policy: whatever answers here answers
        // without one.
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        var response = await host.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains("<div id=\"root\">", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheShellCarriesTheConfiguredTitle()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync(
            options => options.Dashboard.Title = "Cadence staging");

        var boot = await ReadBootstrapAsync(host);

        Assert.Equal("Cadence staging", boot.GetProperty("title").GetString());
    }

    [Fact]
    public async Task CapabilitiesAreFalseWhereNothingWritableIsRegistered()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        var capabilities = (await ReadBootstrapAsync(host)).GetProperty("capabilities");

        Assert.False(capabilities.GetProperty("scheduleWrite").GetBoolean());
        Assert.False(capabilities.GetProperty("tokens").GetBoolean());
    }

    [Fact]
    public async Task CapabilitiesFollowTheRegisteredStores()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync(
            services: collection =>
            {
                collection.AddSingleton<IWritableScheduleSource>(new FakeWritableScheduleSource());
                collection.AddSingleton<IWritableApiTokenStore>(new FakeWritableApiTokenStore());
            });

        var capabilities = (await ReadBootstrapAsync(host)).GetProperty("capabilities");

        Assert.True(capabilities.GetProperty("scheduleWrite").GetBoolean());
        Assert.True(capabilities.GetProperty("tokens").GetBoolean());
    }

    [Fact]
    public async Task ATitleCannotCloseTheScriptElementItIsWrittenInto()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync(
            options => options.Dashboard.Title = "</script><script>alert(1)</script>");

        var response = await host.Client.GetAsync(CadenceApiDefaults.BasePath);
        var body = await response.Content.ReadAsStringAsync();

        // One script element opens the head and one loads the bundle; the title contributes none.
        Assert.DoesNotContain("<script>alert(1)", body, StringComparison.Ordinal);
        Assert.Equal(
            "</script><script>alert(1)</script>",
            (await ReadBootstrapAsync(host)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheShellIsFixedAtMapTimeAndDoesNotVaryByCaller()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        var anonymous = await host.Client.GetStringAsync(CadenceApiDefaults.BasePath);
        await host.SignInAsync("u1", "Ada");
        var signedIn = await host.Client.GetStringAsync(DeepRoute);

        // Nothing per-request may reach the document: what the caller is allowed to see is decided
        // by the operator tree, on the fetches the application makes afterwards.
        Assert.Equal(anonymous, signedIn);
    }

    [Fact]
    public async Task TheShellAndItsAssetsSurviveAHostFallbackPolicy()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync(
            services: collection => collection.AddAuthorizationBuilder().SetFallbackPolicy(
                new AuthorizationPolicyBuilder(CadenceApiDefaults.CookieScheme)
                    .RequireAuthenticatedUser()
                    .Build()));

        var shell = await host.Client.GetAsync(DeepRoute);
        var asset = await host.Client.GetAsync(await ReadAssetUrlAsync(host));

        // The host's fallback governs everything it was not told to leave alone. It has to leave
        // these alone: a browser cannot sign in through an application it was never served.
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
    }

    [Fact]
    public async Task TheShellIsNotCached()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        var response = await host.Client.GetAsync(CadenceApiDefaults.BasePath);

        // It names hashed assets, so a cached copy outlives the files it points at.
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task TheHashedAssetIsServedImmutably()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        var url = await ReadAssetUrlAsync(host);
        var response = await host.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMissingAssetIsAFourOhFourRatherThanTheShell()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        var response = await host.Client.GetAsync(CadenceApiDefaults.AssetsPath + "/missing.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheMachineTreeIsAFourOhFourWhereItWasNeverMapped()
    {
        // The dashboard alone: MapCadenceApi() is not called, so nothing under /cadence/api exists
        // apart from the sign-in routes, and the catch-all must not answer for it.
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        var response = await host.Client.GetAsync(CadenceApiDefaults.ApiPath + "/jobs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUnmappedOperatorRouteIsAFourOhFourRatherThanTheShell()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();

        var response = await host.Client.GetAsync(CadenceApiDefaults.UiPath + "/nothing-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> ReadBootstrapAsync(DashboardTestHost host)
    {
        var body = await host.Client.GetStringAsync(CadenceApiDefaults.BasePath);
        var match = Bootstrap.Match(body);

        Assert.True(match.Success, $"The shell carries no bootstrap object:{Environment.NewLine}{body}");

        return JsonDocument.Parse(match.Groups["json"].Value).RootElement.Clone();
    }

    private static async Task<string> ReadAssetUrlAsync(DashboardTestHost host)
    {
        var body = await host.Client.GetStringAsync(CadenceApiDefaults.BasePath);
        var match = AssetReference.Match(body);

        Assert.True(match.Success, $"The shell references no bundle asset:{Environment.NewLine}{body}");

        return match.Groups["url"].Value;
    }

    private sealed class FakeWritableScheduleSource : IWritableScheduleSource
    {
        public Task<IReadOnlyList<JobSchedule>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<JobSchedule>>([]);

        public Task<JobSchedule?> GetAsync(string jobName, CancellationToken cancellationToken)
            => Task.FromResult<JobSchedule?>(null);

        public IChangeToken GetChangeToken() => NullChangeToken.Singleton;

        public Task UpsertAsync(JobSchedule schedule, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeWritableApiTokenStore : IWritableApiTokenStore
    {
        public Task<ApiTokenPrincipal?> FindAsync(byte[] digest, CancellationToken cancellationToken)
            => Task.FromResult<ApiTokenPrincipal?>(null);

        public Task<ApiTokenInfo> CreateAsync(
            ApiTokenCreation creation, byte[] digest, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ApiTokenInfo>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiTokenInfo>>([]);

        public Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
