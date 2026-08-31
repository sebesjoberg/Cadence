using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§13.2: the job reads.</summary>
public sealed class JobEndpointTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    [Fact]
    public async Task TheJobListNamesEveryRegisteredJob()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get("/cadence/api/jobs"));

        response.EnsureSuccessStatusCode();
        var jobs = await response.Content.ReadFromJsonAsync<List<JobSummaryResponse>>();
        Assert.NotNull(jobs);
        var job = Assert.Single(jobs, candidate => candidate.Name == ApiTestJobs.NightlyName);
        Assert.Equal(ApiTestJobs.NightlyCron, job.Cron);
        Assert.NotNull(job.NextOccurrenceUtc);
    }

    [Fact]
    public async Task AJobIsReadableByName()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get($"/cadence/api/jobs/{ApiTestJobs.NightlyName}"));

        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<JobDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(ApiTestJobs.NightlyName, detail.Job.Name);
        Assert.Empty(detail.RecentRuns);
    }

    [Fact]
    public async Task AnUnregisteredJobIsNotFound()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get("/cadence/api/jobs/no-such-job"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithoutATokenTheRoutesAreUnauthorizedNotMissing()
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync("/cadence/api/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheJobDetailAsksTheStoreNotToAttachLogs()
    {
        RecordingRunHistoryStore? recorder = null;
        await using var host = await ApiTestHost.StartAsync(
            api => api.Tokens.Add(Token),
            services: collection => recorder = RecordingRunHistoryStore.Install(collection));
        Assert.NotNull(recorder);
        recorder.Clear();

        var response = await host.Client.SendAsync(Get($"/cadence/api/jobs/{ApiTestJobs.NightlyName}"));

        response.EnsureSuccessStatusCode();
        Assert.False(Assert.Single(recorder.Queries).IncludeLog);
    }

    [Fact]
    public async Task TheNextOccurrenceIsReportedInUtcEvenForAZonedJob()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get($"/cadence/api/jobs/{ApiTestJobs.ZonedName}"));

        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<JobDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(ApiTestJobs.ZonedTimeZone, detail.Job.TimeZone);
        Assert.NotNull(detail.Job.NextOccurrenceUtc);
        Assert.Equal(TimeSpan.Zero, detail.Job.NextOccurrenceUtc.Value.Offset);
    }

    // AllowUnauthenticated is a statement about what Cadence adds, not a licence to drop a token
    // the operator also configured. The policy still applies, so the "no authentication" warning
    // must not fire alongside it.
    [Fact]
    public async Task AllowUnauthenticatedAlongsideATokenStillEnforcesTheToken()
    {
        var logs = new LogCapture();
        await using var host = await ApiTestHost.StartAsync(
            api =>
            {
                api.AllowUnauthenticated = true;
                api.Tokens.Add(Token);
            },
            logs: logs);

        var anonymous = await host.Client.GetAsync("/cadence/api/jobs");
        var authenticated = await host.Client.SendAsync(Get("/cadence/api/jobs"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.False(logs.HasWarning(3001));
    }

    [Fact]
    public async Task AnUnregisteredJobIsRefusedWithAProblemBody()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(Get("/cadence/api/jobs/no-such-job"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":404", body, StringComparison.Ordinal);
        Assert.Contains("urn:cadence:problem:job-not-found", body, StringComparison.Ordinal);
        Assert.Contains("no-such-job", body, StringComparison.Ordinal);
    }

    // The built-in policy is registered only when a token is, so applying it regardless would
    // authenticate against a scheme that is not there — a 500 on every request in exactly the two
    // deployments that expect none.
    [Fact]
    public async Task WithNothingConfiguredInDevelopmentTheRoutesAnswerUnauthenticated()
    {
        await using var host = await ApiTestHost.StartAsync(environment: Environments.Development);

        var response = await host.Client.GetAsync("/cadence/api/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AllowUnauthenticatedAnswersUnauthenticated()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.AllowUnauthenticated = true);

        var response = await host.Client.GetAsync("/cadence/api/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static Task<ApiTestHost> StartAsync() =>
        ApiTestHost.StartAsync(api => api.Tokens.Add(Token));
}
