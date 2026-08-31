using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>Collecting what a run produced: the description on run detail, and the download.</summary>
public sealed class RunResultEndpointTests
{
    private const string Token = "s3cret-token-value-32-chars-long";
    private static readonly DateTimeOffset Origin = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARunDetailDescribesItsResultWithoutTransferringIt()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(runId, JobResult.Csv("customer,rows\nContoso,3\n", "report.csv"));

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}"));

        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<RunDetailResponse>();

        Assert.NotNull(detail?.Result);
        Assert.Equal("text/csv; charset=utf-8", detail.Result.ContentType);
        Assert.Equal("report.csv", detail.Result.FileName);
        Assert.Equal(24, detail.Result.Length);
    }

    [Fact]
    public async Task ARunThatProducedNothingDescribesNoResult()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(runId, result: null);

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}"));

        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<RunDetailResponse>();

        Assert.NotNull(detail);
        Assert.Null(detail.Result);
    }

    [Fact]
    public async Task TheResultDownloadsWithItsContentTypeAndFilename()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(runId, JobResult.Csv("customer,rows\nContoso,3\n", "report.csv"));

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}/result"));

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("report.csv", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("customer,rows\nContoso,3\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AResultWithNoFilenameIsServedInline()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(
            runId, JobResult.Bytes(Encoding.UTF8.GetBytes("{}"), "application/json; charset=utf-8"));

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}/result"));

        response.EnsureSuccessStatusCode();
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task AFilenameNeedingEncodingSurvivesTheHeader()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(runId, JobResult.Csv("x\n", "rapport, år 2026.csv"));

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}/result"));

        response.EnsureSuccessStatusCode();

        // The comma would truncate a naively concatenated header, and the non-ASCII characters
        // cannot go in the plain filename parameter at all.
        Assert.Equal(
            "rapport, år 2026.csv",
            response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task AnUnknownRunIsNotFoundRatherThanAnEmptyDownload()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{Guid.NewGuid()}/result"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("run-not-found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWhoseResultHasGoneIsToldApartFromAnUnknownRun()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(runId, result: null);

        var response = await host.Client.SendAsync(Get($"/cadence/api/runs/{runId}/result"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Two different 404s, because the fix differs: one is a mistyped id, the other is a result
        // that has passed its retention.
        Assert.Contains("result-not-found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectingAResultNeedsAuthenticationLikeEveryOtherRead()
    {
        var runId = Guid.NewGuid();
        await using var host = await StartAsync(runId, JobResult.Csv("x\n", "report.csv"));

        var response = await host.Client.GetAsync($"/cadence/api/runs/{runId}/result");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task<ApiTestHost> StartAsync(Guid? runId = null, JobResult? result = null)
    {
        var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        if (runId is { } id)
        {
            await host.Services.GetRequiredService<IRunHistoryStore>().StartAsync(
                new JobRunStart
                {
                    RunId = id,
                    JobName = "nightly",
                    Trigger = TriggerKind.Api,
                    InstanceId = "test:1",
                    StartedAt = Origin,
                },
                default);

            if (result is not null)
            {
                await host.Services.GetRequiredService<IJobResultStore>()
                    .SaveAsync(id, result, Origin.AddDays(7), default);
            }
        }

        return host;
    }
}
