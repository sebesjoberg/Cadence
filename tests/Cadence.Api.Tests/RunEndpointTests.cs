using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§13.2: the run reads, and the cap that stops one request from becoming a denial.</summary>
public sealed class RunEndpointTests
{
    private const string Token = "s3cret-token-value-32-chars-long";
    private static readonly DateTimeOffset Origin = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARunIsReadableByIdWithItsLog()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(async store =>
        {
            await store.StartAsync(Start(runId), default);
            await store.AppendLogAsync(runId, new JobLogEntry { Timestamp = Origin, Message = "halfway" }, default);
        });

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}"));

        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<RunDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(runId, detail.Run.RunId);
        Assert.Equal("halfway", Assert.Single(detail.Log).Message);
    }

    [Fact]
    public async Task AnUnknownRunIsNotFound()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheRunListCarriesNoLogs()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(async store =>
        {
            await store.StartAsync(Start(runId), default);
            await store.AppendLogAsync(runId, new JobLogEntry { Timestamp = Origin, Message = "noise" }, default);
        });

        var response = await host.Client.SendAsync(Get("/cadence/api/runs"));

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<RunPageResponse>();
        Assert.NotNull(page);
        Assert.Single(page.Runs);
        Assert.DoesNotContain("noise", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnboundedLimitIsClampedToFiveHundred()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get("/cadence/api/runs?limit=100000"));

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<RunPageResponse>();
        Assert.NotNull(page);
        Assert.Equal(500, page.Limit);
    }

    [Fact]
    public async Task AJobFilterNarrowsTheList()
    {
        await using var host = await StartAsync(async store =>
        {
            await store.StartAsync(Start(Guid.NewGuid(), "nightly"), default);
            await store.StartAsync(Start(Guid.NewGuid(), "hourly"), default);
        });

        var response = await host.Client.SendAsync(Get("/cadence/api/runs?job=nightly"));

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<RunPageResponse>();
        Assert.NotNull(page);
        Assert.Equal("nightly", Assert.Single(page.Runs).JobName);
    }

    [Fact]
    public async Task AStatusFilterNarrowsTheList()
    {
        var succeeded = Guid.NewGuid();
        await using var host = await StartAsync(async store =>
        {
            await store.StartAsync(Start(succeeded), default);
            await store.CompleteAsync(succeeded, JobRunResult.Success(TimeSpan.FromSeconds(1), Origin), default);
            await store.StartAsync(Start(Guid.NewGuid()), default);
        });

        var response = await host.Client.SendAsync(Get("/cadence/api/runs?status=Succeeded"));

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<RunPageResponse>();
        Assert.NotNull(page);
        Assert.Equal(succeeded, Assert.Single(page.Runs).RunId);
    }

    [Fact]
    public async Task AnUnknownStatusIsABadRequestNotAServerError()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get("/cadence/api/runs?status=Exploded"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnOffsetPagesPastTheNewestRun()
    {
        var older = Guid.NewGuid();
        await using var host = await StartAsync(async store =>
        {
            await store.StartAsync(Start(older) with { StartedAt = Origin }, default);
            await store.StartAsync(Start(Guid.NewGuid()) with { StartedAt = Origin.AddMinutes(1) }, default);
        });

        var response = await host.Client.SendAsync(Get("/cadence/api/runs?offset=1"));

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<RunPageResponse>();
        Assert.NotNull(page);
        Assert.Equal(1, page.Offset);
        Assert.Equal(older, Assert.Single(page.Runs).RunId);
    }

    [Fact]
    public async Task WithoutATokenTheRoutesAreUnauthorizedNotMissing()
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync("/cadence/api/runs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static JobRunStart Start(Guid runId, string jobName = "nightly") => new()
    {
        RunId = runId,
        JobName = jobName,
        Trigger = TriggerKind.Api,
        InstanceId = "test:1",
        StartedAt = Origin,
    };

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task<ApiTestHost> StartAsync(Func<IRunHistoryStore, Task>? seed = null)
    {
        var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        if (seed is not null)
        {
            await seed(host.Services.GetRequiredService<IRunHistoryStore>());
        }

        return host;
    }
}
