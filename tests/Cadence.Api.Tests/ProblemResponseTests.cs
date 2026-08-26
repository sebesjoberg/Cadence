using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cadence.Execution;
using Cadence.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// §13.2's status table as it reaches a caller: the problem type each refusal is identified by, and
/// the wire shape every one of them goes out in.
/// </summary>
public sealed class ProblemResponseTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    [Theory]
    [InlineData(typeof(JobNotFoundException), HttpStatusCode.NotFound, "job-not-found")]
    [InlineData(typeof(TriggerNotAllowedException), HttpStatusCode.BadRequest, "trigger-not-allowed")]
    [InlineData(typeof(SchedulerPausedException), HttpStatusCode.Conflict, "scheduler-paused")]
    public async Task EachTriggerRefusalNamesItsProblemType(Type exceptionType, HttpStatusCode status, string slug)
    {
        await using var host = await StartAsync(new FakeTrigger { Throws = Build(exceptionType) });

        var problem = await RefuseAsync(host, Post("/cadence/api/jobs/nightly/trigger"), status);

        Assert.Equal($"urn:cadence:problem:{slug}", problem.Type);
    }

    [Fact]
    public async Task ARefusedTriggerCarriesTheRuleItBrokeInTheDetail()
    {
        await using var host = await StartAsync(
            new FakeTrigger { Throws = Build(typeof(TriggerNotAllowedException)) });

        var problem = await RefuseAsync(
            host, Post("/cadence/api/jobs/nightly/trigger"), HttpStatusCode.BadRequest);

        Assert.Contains("'nightly' allows Schedule.", problem.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASkippedDispatchNamesItsProblemType()
    {
        await using var host = await StartAsync(
            new FakeTrigger { Result = DispatchResult.Skipped("already running here") });

        var problem = await RefuseAsync(
            host, Post("/cadence/api/jobs/nightly/trigger"), HttpStatusCode.Conflict);

        Assert.Equal("urn:cadence:problem:run-skipped", problem.Type);
    }

    [Fact]
    public async Task AnUnparseablePauseScopeNamesItsProblemType()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var request = new HttpRequestMessage(HttpMethod.Put, "/cadence/api/pause")
        {
            Content = JsonContent.Create(new PauseRequest("Sideways", null)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var problem = await RefuseAsync(host, request, HttpStatusCode.BadRequest);

        Assert.Equal("urn:cadence:problem:invalid-pause-scope", problem.Type);
    }

    [Fact]
    public async Task TheLoopbackRefusalNamesItsProblemType()
    {
        await using var host = await ApiTestHost.StartAsync(
            environment: Environments.Development,
            remoteIp: IPAddress.Parse("203.0.113.7"));

        var problem = await RefuseAsync(
            host,
            new HttpRequestMessage(HttpMethod.Get, "/cadence/api/pause"),
            HttpStatusCode.Forbidden);

        Assert.Equal("urn:cadence:problem:not-loopback", problem.Type);
    }

    // Replaces two assertions that used to run the serializer context directly: the field names are
    // camelCase, and the fields the mapper never sets stay off the wire rather than going out null.
    [Fact]
    public async Task AProblemBodyCarriesExactlyTheFieldsTheMapperSets()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var request = new HttpRequestMessage(HttpMethod.Get, "/cadence/api/jobs/no-such-job");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await host.Client.SendAsync(request);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();

        Assert.Equal<string[]>(["detail", "status", "title", "type"], names);
    }

    private static async Task<ProblemDetails> RefuseAsync(
        ApiTestHost host,
        HttpRequestMessage request,
        HttpStatusCode expected)
    {
        var response = await host.Client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);

        return problem;
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

    private static Task<ApiTestHost> StartAsync(FakeTrigger trigger) => ApiTestHost.StartAsync(
        api => api.Tokens.Add(Token),
        services => services.Replace(ServiceDescriptor.Singleton<IJobTrigger>(trigger)));

    private sealed class FakeTrigger : IJobTrigger
    {
        public DispatchResult Result { get; init; } = DispatchResult.Started(Guid.NewGuid());

        public Exception? Throws { get; init; }

        public Task<DispatchResult> TriggerAsync(
            string jobName,
            TriggerKind trigger = TriggerKind.Manual,
            JsonElement? payload = null,
            CancellationToken cancellationToken = default) =>
            Throws is not null
                ? Task.FromException<DispatchResult>(Throws)
                : Task.FromResult(Result);
    }
}
