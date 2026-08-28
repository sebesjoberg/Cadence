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

    private const string MachinePath =
        CadenceApiDefaults.ApiPath + "/jobs/" + ApiTestJobs.OnDemandName + "/trigger";

    private const string UiPath =
        CadenceApiDefaults.UiPath + "/jobs/" + ApiTestJobs.OnDemandName + "/trigger";

    private static readonly CadenceUiMapOptions Open =
        new() { CookiePolicy = false, LoopbackOnly = false };

    private static readonly CadenceUiMapOptions Cookie =
        new() { CookiePolicy = true, LoopbackOnly = false };

    // The distinction the second route exists for, asserted where it is observable: on the runs the
    // two calls recorded, for one job, in one host.
    [Fact]
    public async Task TheOperatorTreeRecordsManualAndTheMachineTreeRecordsApi()
    {
        await using var host = await StartAsync();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var machine = await host.Client.PostAsync(MachinePath, content: null);
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
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

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
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await host.Client.PostAsync(path, content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already running here", await response.Content.ReadAsStringAsync());
    }

    // The cookie tree's writes carry Operate, the same pair pausing does.
    [Fact]
    public async Task ASignedInUserTriggersFromTheOperatorTree()
    {
        var trigger = new FakeTrigger();

        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => collection.Replace(ServiceDescriptor.Singleton<IJobTrigger>(trigger)),
            endpoints: routes => CadenceUiRoutes.Map(routes, Cookie));

        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PostAsync(UiPath, content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(TriggerKind.Manual, trigger.LastTrigger);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefusedOnTheCookieTree()
    {
        var trigger = new FakeTrigger();

        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => collection.Replace(ServiceDescriptor.Singleton<IJobTrigger>(trigger)),
            endpoints: routes => CadenceUiRoutes.Map(routes, Cookie));

        var response = await host.Client.PostAsync(UiPath, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(trigger.LastTrigger);
    }

    // Replace, not Add: AddCadence registers IJobTrigger with TryAddSingleton and this hook runs
    // after it, so adding a second registration would leave which one resolves to ordering.
    private static Task<ApiTestHost> StartAsync(FakeTrigger? trigger = null) => ApiTestHost.StartAsync(
        api => api.Tokens.Add(Token),
        services =>
        {
            if (trigger is not null)
            {
                services.Replace(ServiceDescriptor.Singleton<IJobTrigger>(trigger));
            }
        },
        endpoints: routes => CadenceUiRoutes.Map(routes, Open));

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
