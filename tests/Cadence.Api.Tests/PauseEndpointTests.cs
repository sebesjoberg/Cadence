using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§12 and §13.2: the write that earns its place, and who gets blamed for it.</summary>
public sealed class PauseEndpointTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    /// <summary>The first eight lowercase hex of SHA-256(Token), as the token handler names it.</summary>
    private const string Fingerprint = "bb60af61";

    /// <summary>What the substituted clock stamps a pause with. Not UTC, which is the whole point.</summary>
    private static readonly DateTimeOffset Stamp =
        new(2026, 8, 26, 13, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task PausingReturnsNoContentAndTakesEffect()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(
            Put(new PauseRequest(nameof(PauseScope.Schedule), "incident 4021")));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var state = await StateOf(host);
        Assert.True(state.IsSchedulePaused);
        Assert.Equal("incident 4021", state.Reason);
    }

    [Fact]
    public async Task SetByComesFromTheTokenNotTheBody()
    {
        await using var host = await StartAsync();

        await host.Client.SendAsync(Put(new PauseRequest(nameof(PauseScope.All), "because")));

        var state = await StateOf(host);
        Assert.Equal($"token:{Fingerprint}", state.SetBy);
        Assert.DoesNotContain(Token, state.SetBy);
    }

    [Fact]
    public async Task ASetByInTheRequestBodyChangesNothing()
    {
        await using var host = await StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Put, "/cadence/api/pause")
        {
            Content = new StringContent(
                """{"scope":"All","reason":"because","setBy":"root"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal($"token:{Fingerprint}", (await StateOf(host)).SetBy);
    }

    [Fact]
    public async Task WithoutAPrincipalSetByIsTheApi()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.AllowUnauthenticated = true);

        var request = new HttpRequestMessage(HttpMethod.Put, "/cadence/api/pause")
        {
            Content = JsonContent.Create(new PauseRequest(nameof(PauseScope.Triggers), null)),
        };

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("api", (await StateOf(host)).SetBy);
    }

    [Fact]
    public async Task ThePauseStateIsReadable()
    {
        // The clock is substituted so the store stamps an offset the response has to normalize away.
        // Against the real clock UtcNow is always zero-offset, and the assertion below would hold
        // whether or not anything normalized anything.
        await using var host = await StartAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<ISystemClock>(new OffsetClock(Stamp))));
        await host.Services.GetRequiredService<IPauseStore>()
            .SetAsync(PauseScope.Triggers, "maintenance", "someone", default);

        var response = await host.Client.SendAsync(Get());

        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<PauseResponse>();
        Assert.NotNull(state);
        Assert.Equal(nameof(PauseScope.Triggers), state.Scope);
        Assert.Equal("maintenance", state.Reason);
        Assert.Equal("someone", state.SetBy);
        Assert.Equal(TimeSpan.Zero, state.SetAtUtc!.Value.Offset);
        Assert.Equal(Stamp.UtcDateTime, state.SetAtUtc.Value.DateTime);
    }

    // The write that halts scheduling across a cluster, probed directly rather than by way of the
    // group it shares with the reads. The store assertion is what separates "refused before the
    // handler" from "refused after the pause had already been written".
    [Fact]
    public async Task ThePauseWriteIsBehindThePolicy()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PutAsJsonAsync(
            "/cadence/api/pause",
            new PauseRequest(nameof(PauseScope.All), "no token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PauseScope.None, (await StateOf(host)).Scope);
    }

    [Fact]
    public async Task AnUnparseableScopeIsABadRequest()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Put(new PauseRequest("Sideways", null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PauseScope.None, (await StateOf(host)).Scope);
    }

    // Enum.TryParse takes bare numbers too, so a scope carrying a bit no member defines would
    // otherwise be stored and read back as something no operator can interpret.
    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task AScopeOutsideTheDefinedFlagsIsABadRequest(string scope)
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Put(new PauseRequest(scope, null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PauseScope.None, (await StateOf(host)).Scope);
    }

    [Fact]
    public async Task ACommaSeparatedScopeIsAccepted()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(
            Put(new PauseRequest($"{nameof(PauseScope.Schedule)},{nameof(PauseScope.Triggers)}", null)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(PauseScope.All, (await StateOf(host)).Scope);
    }

    [Fact]
    public async Task ResumingClearsEverything()
    {
        await using var host = await StartAsync();
        await host.Client.SendAsync(Put(new PauseRequest(nameof(PauseScope.All), "stop")));
        Assert.Equal(PauseScope.All, (await StateOf(host)).Scope);

        await host.Client.SendAsync(Put(new PauseRequest(nameof(PauseScope.None), "resume")));

        Assert.Equal(PauseScope.None, (await StateOf(host)).Scope);
    }

    private static Task<PauseState> StateOf(ApiTestHost host) =>
        host.Services.GetRequiredService<IPauseStore>().GetAsync(default);

    private static HttpRequestMessage Get()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/cadence/api/pause");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static HttpRequestMessage Put(PauseRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/cadence/api/pause")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static Task<ApiTestHost> StartAsync(Action<IServiceCollection>? services = null) =>
        ApiTestHost.StartAsync(api => api.Tokens.Add(Token), services);

    /// <summary>A clock whose instant carries an offset, so normalization has something to do.</summary>
    private sealed class OffsetClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
