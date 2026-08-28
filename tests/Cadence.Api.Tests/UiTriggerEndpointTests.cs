using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cadence.Api.Routing;
using Cadence.Execution;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// The operator tree's own trigger. It exists rather than reusing the machine tree's route because
/// §13.2 wants history to separate someone clicking from something calling us, and it shares that
/// route's dispatch so the two cannot answer a refusal differently.
/// </summary>
public sealed class UiTriggerEndpointTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    /// <summary>The signed-in person every call on the operator tree here is made by.</summary>
    private const string Operator = "Ada Lovelace";

    private const string MachinePath =
        CadenceApiDefaults.ApiPath + "/jobs/" + ApiTestJobs.OnDemandName + "/trigger";

    private const string UiPath =
        CadenceApiDefaults.UiPath + "/jobs/" + ApiTestJobs.OnDemandName + "/trigger";

    private static readonly CadenceUiMapOptions Cookie =
        new() { CookiePolicy = true, LoopbackOnly = false };

    // The distinction the second route exists for, asserted where it is observable: on the runs the
    // two calls recorded, for one job, in one host. A person on the operator tree and a token on the
    // machine one, which is the split the kinds are named for.
    [Fact]
    public async Task TheOperatorTreeRecordsManualAndTheMachineTreeRecordsApi()
    {
        await using var host = await StartAsync();

        var machine = await host.Client.SendAsync(WithToken(MachinePath));
        var dashboard = await host.Client.PostAsync(UiPath, content: null);

        Assert.Equal(HttpStatusCode.Accepted, machine.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, dashboard.StatusCode);

        var runs = await host.Services.GetRequiredService<IRunHistoryStore>().QueryAsync(
            new RunQuery { JobName = ApiTestJobs.OnDemandName, IncludeLog = false }, default);

        // Newest first, so the dashboard's call is the first row.
        Assert.Equal<TriggerKind[]>([TriggerKind.Manual, TriggerKind.Api], [.. runs.Select(run => run.Trigger)]);
    }

    [Fact]
    public async Task AStartedRunIsAccepted()
    {
        var runId = Guid.NewGuid();
        var trigger = new FakeTrigger { Result = DispatchResult.Started(runId) };
        await using var host = await StartAsync(trigger);

        var response = await host.Client.PostAsync(UiPath, content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(TriggerKind.Manual, trigger.LastTrigger);

        var body = await response.Content.ReadFromJsonAsync<TriggerResponse>();
        Assert.NotNull(body);
        Assert.Equal(runId, body.RunId);
        Assert.Equal(ApiTestJobs.OnDemandName, body.JobName);
        Assert.False(string.IsNullOrWhiteSpace(body.InstanceId));
    }

    // §13.2: the route starts the job as configured. Accepting caller JSON would widen it to
    // starting the job with arbitrary input, so a body is not read even when one is sent.
    [Fact]
    public async Task TheTriggerTakesNoPayload()
    {
        var trigger = new FakeTrigger();
        await using var host = await StartAsync(trigger);

        var response = await host.Client.PostAsync(
            UiPath, new StringContent("""{"drop":"everything"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(trigger.LastPayload);
    }

    [Theory]
    [InlineData(MachinePath)]
    [InlineData(UiPath)]
    public async Task ARefusalGetsTheSameStatusOnEitherTree(string path)
    {
        var trigger = new FakeTrigger { Throws = new JobNotFoundException(ApiTestJobs.OnDemandName) };
        await using var host = await StartAsync(trigger);

        var response = await host.Client.PostAsync(path, content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(MachinePath)]
    [InlineData(UiPath)]
    public async Task ASkippedRunIsAConflictOnEitherTree(string path)
    {
        var trigger = new FakeTrigger { Result = DispatchResult.Skipped("already running here") };
        await using var host = await StartAsync(trigger);

        var response = await host.Client.PostAsync(path, content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already running here", await response.Content.ReadAsStringAsync());
    }

    // The reason the two routes are not interchangeable. Operate is a scope a token holds, so
    // without a user-principal check on this route a CI job could record itself as Manual -- and
    // then history no longer separates someone clicking from something calling us. The token is not
    // shut out of anything: the machine route takes it, and records what it actually is.
    [Fact]
    public async Task ATokenIsRefusedOnTheOperatorTreeAndStillTriggersOnTheMachineOne()
    {
        var trigger = new FakeTrigger();
        await using var host = await StartAnonymousAsync(trigger);

        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var dashboard = await host.Client.PostAsync(UiPath, content: null);

        Assert.Equal(HttpStatusCode.Forbidden, dashboard.StatusCode);
        Assert.Null(trigger.LastTrigger);

        var machine = await host.Client.PostAsync(MachinePath, content: null);

        Assert.Equal(HttpStatusCode.Accepted, machine.StatusCode);
        Assert.Equal(TriggerKind.Api, trigger.LastTrigger);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefusedOnTheCookieTree()
    {
        var trigger = new FakeTrigger();
        await using var host = await StartAnonymousAsync(trigger);

        var response = await host.Client.PostAsync(UiPath, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(trigger.LastTrigger);
    }

    /// <summary>The dashboard's own shape: a signed-in person on the cookie tree.</summary>
    private static async Task<ApiTestHost> StartAsync(FakeTrigger? trigger = null)
    {
        var host = await StartAnonymousAsync(trigger);

        await host.SignInAsync("u1", Operator);
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        return host;
    }

    // Replace, not Add: AddCadence registers IJobTrigger with TryAddSingleton and this hook runs
    // after it, so adding a second registration would leave which one resolves to ordering.
    private static Task<ApiTestHost> StartAnonymousAsync(FakeTrigger? trigger = null)
        => ApiTestHost.StartWithOidcAsync(
            configure: api => api.Tokens.Add(Token),
            services: collection =>
            {
                if (trigger is not null)
                {
                    collection.Replace(ServiceDescriptor.Singleton<IJobTrigger>(trigger));
                }
            },
            endpoints: routes => CadenceUiRoutes.Map(routes, Cookie));

    // The machine tree as a machine reaches it. Sent as a request of its own so the ticket the
    // client carries for the operator tree is not what authenticates this call.
    private static HttpRequestMessage WithToken(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        return request;
    }

    private sealed class FakeTrigger : IJobTrigger
    {
        public DispatchResult Result { get; init; } = DispatchResult.Started(Guid.NewGuid());

        public Exception? Throws { get; init; }

        public TriggerKind? LastTrigger { get; private set; }

        public JsonElement? LastPayload { get; private set; }

        public Task<DispatchResult> TriggerAsync(
            string jobName,
            TriggerKind trigger = TriggerKind.Manual,
            JsonElement? payload = null,
            CancellationToken cancellationToken = default)
        {
            LastTrigger = trigger;
            LastPayload = payload;

            return Throws is not null
                ? Task.FromException<DispatchResult>(Throws)
                : Task.FromResult(Result);
        }
    }
}
