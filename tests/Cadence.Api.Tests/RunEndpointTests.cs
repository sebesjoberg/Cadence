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
    public async Task TheRunListAsksTheStoreNotToAttachLogs()
    {
        RecordingRunHistoryStore? recorder = null;
        await using var host = await ApiTestHost.StartAsync(
            api => api.Tokens.Add(Token),
            services: collection => recorder = RecordingRunHistoryStore.Install(collection));
        Assert.NotNull(recorder);
        recorder.Clear();

        var response = await host.Client.SendAsync(Get("/cadence/api/runs"));

        response.EnsureSuccessStatusCode();
        Assert.False(Assert.Single(recorder.Queries).IncludeLog);
    }

    [Fact]
    public async Task TheDateAndInstanceFiltersNarrowTheList()
    {
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        var boundary = Origin.AddMinutes(30);
        await using var host = await StartAsync(async store =>
        {
            await store.StartAsync(Start(early) with { InstanceId = "test:early" }, default);
            await store.StartAsync(
                Start(late) with { StartedAt = Origin.AddHours(1), InstanceId = "test:late" },
                default);
        });

        // A swapped From/To would answer each of the first two with the other run.
        Assert.Equal(late, Assert.Single((await PageAsync(host, $"?from={Iso(boundary)}")).Runs).RunId);
        Assert.Equal(early, Assert.Single((await PageAsync(host, $"?to={Iso(boundary)}")).Runs).RunId);
        Assert.Equal(late, Assert.Single((await PageAsync(host, "?instance=test:late")).Runs).RunId);
    }

    [Fact]
    public async Task RunInstantsAreReportedInUtcWhateverTheStoreRecorded()
    {
        var runId = Guid.NewGuid();
        var offset = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(2));
        await using var host = await StartAsync(async store =>
        {
            await store.StartAsync(Start(runId) with { StartedAt = offset, ScheduledFor = offset }, default);
            await store.CompleteAsync(
                runId,
                JobRunResult.Success(TimeSpan.FromSeconds(1), offset.AddSeconds(1)),
                default);
            await store.AppendLogAsync(
                runId,
                new JobLogEntry { Timestamp = offset, Message = "recorded in local time" },
                default);
        });

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}"));

        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<RunDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(TimeSpan.Zero, detail.Run.StartedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, detail.Run.ScheduledForUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, detail.Run.CompletedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, Assert.Single(detail.Log).TimestampUtc.Offset);
        Assert.DoesNotContain("+02:00", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownRunIsRefusedWithAProblemBody()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":404", body, StringComparison.Ordinal);
        Assert.Contains("problems/run-not-found", body, StringComparison.Ordinal);
        Assert.Contains(runId.ToString(), body, StringComparison.Ordinal);
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

    private static string Iso(DateTimeOffset instant) => Uri.EscapeDataString(instant.ToString("O"));

    private static async Task<RunPageResponse> PageAsync(ApiTestHost host, string query)
    {
        var response = await host.Client.SendAsync(Get($"/cadence/api/runs{query}"));

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<RunPageResponse>();
        Assert.NotNull(page);
        return page;
    }

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
