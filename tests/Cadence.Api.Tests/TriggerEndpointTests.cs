using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cadence.Execution;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§13.2: the one write a token may make, and every way it can be refused.</summary>
public sealed class TriggerEndpointTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    [Fact]
    public async Task AStartedRunIsAccepted()
    {
        var runId = Guid.NewGuid();
        var trigger = new FakeTrigger { Result = DispatchResult.Started(runId) };
        await using var host = await StartAsync(trigger);

        var response = await host.Client.SendAsync(Post("/cadence/api/jobs/nightly/trigger"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TriggerResponse>();
        Assert.NotNull(body);
        Assert.Equal(runId, body.RunId);
        Assert.Equal("nightly", body.JobName);
        Assert.False(string.IsNullOrWhiteSpace(body.InstanceId));
    }

    [Fact]
    public async Task TheEndpointTriggersAsApiNotManual()
    {
        var trigger = new FakeTrigger { Result = DispatchResult.Started(Guid.NewGuid()) };
        await using var host = await StartAsync(trigger);

        await host.Client.SendAsync(Post("/cadence/api/jobs/nightly/trigger"));

        Assert.Equal(TriggerKind.Api, trigger.LastTrigger);
    }

    [Fact]
    public async Task ASkippedRunIsAConflictNotAnEmptySuccess()
    {
        var trigger = new FakeTrigger { Result = DispatchResult.Skipped("already running here") };
        await using var host = await StartAsync(trigger);

        var response = await host.Client.SendAsync(Post("/cadence/api/jobs/nightly/trigger"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already running here", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(typeof(JobNotFoundException), HttpStatusCode.NotFound)]
    [InlineData(typeof(TriggerNotAllowedException), HttpStatusCode.BadRequest)]
    [InlineData(typeof(SchedulerPausedException), HttpStatusCode.Conflict)]
    public async Task EachRefusalGetsItsStatus(Type exceptionType, HttpStatusCode expected)
    {
        var trigger = new FakeTrigger { Throws = Build(exceptionType) };
        await using var host = await StartAsync(trigger);

        var response = await host.Client.SendAsync(Post("/cadence/api/jobs/nightly/trigger"));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task APausedSchedulerSaysWhoPausedItAndWhy()
    {
        var trigger = new FakeTrigger
        {
            Throws = new SchedulerPausedException(
                "nightly",
                new PauseState { Scope = PauseScope.Triggers, Reason = "incident 4021", SetBy = "token:bb60af61" }),
        };
        await using var host = await StartAsync(trigger);

        var response = await host.Client.SendAsync(Post("/cadence/api/jobs/nightly/trigger"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("token:bb60af61", body);
        Assert.Contains("incident 4021", body);
    }

    [Fact]
    public async Task AnUnrecognisedFailureIsNotFlattenedIntoARefusal()
    {
        var trigger = new FakeTrigger { Throws = new InvalidOperationException("the store fell over") };
        await using var host = await StartAsync(trigger);

        // This host mounts no exception handler, so the failure arrives intact rather than as a
        // status. That it is not a problem document is the point: a bug must not read as a refusal.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Client.SendAsync(Post("/cadence/api/jobs/nightly/trigger")));

        Assert.Equal("the store fell over", thrown.Message);
    }

    [Fact]
    public async Task TheTriggerRouteIsBehindThePolicy()
    {
        var trigger = new FakeTrigger();
        await using var host = await StartAsync(trigger);

        var response = await host.Client.PostAsync("/cadence/api/jobs/nightly/trigger", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(trigger.LastTrigger);
    }

    private static Exception Build(Type type) => type switch
    {
        _ when type == typeof(JobNotFoundException) => new JobNotFoundException("nightly"),
        _ when type == typeof(TriggerNotAllowedException) =>
            new TriggerNotAllowedException("nightly", TriggerKind.Api, "'nightly' allows Schedule."),
        _ => new SchedulerPausedException(
            "nightly",
            new PauseState { Scope = PauseScope.Triggers, Reason = "incident 4021" }),
    };

    private static HttpRequestMessage Post(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    // Replace, not Add: AddCadence registers IJobTrigger with TryAddSingleton and this hook runs
    // after it, so adding a second registration would leave which one resolves to ordering.
    private static Task<ApiTestHost> StartAsync(FakeTrigger trigger) => ApiTestHost.StartAsync(
        api => api.Tokens.Add(Token),
        services => services.Replace(ServiceDescriptor.Singleton<IJobTrigger>(trigger)));

    private sealed class FakeTrigger : IJobTrigger
    {
        public DispatchResult Result { get; init; } = DispatchResult.Started(Guid.NewGuid());

        public Exception? Throws { get; init; }

        public TriggerKind? LastTrigger { get; private set; }

        public Task<DispatchResult> TriggerAsync(
            string jobName,
            TriggerKind trigger = TriggerKind.Manual,
            JsonElement? payload = null,
            CancellationToken cancellationToken = default)
        {
            LastTrigger = trigger;

            return Throws is not null
                ? Task.FromException<DispatchResult>(Throws)
                : Task.FromResult(Result);
        }
    }
}
