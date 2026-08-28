using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cadence.Api.Routing;
using Cadence.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// The write the machine tree deliberately does not carry: a triggered run is loud and over, a
/// changed cron expression is silent and permanent, so only the operator tree edits one.
/// </summary>
public sealed class ScheduleEndpointTests
{
    private const string JobName = ApiTestJobs.NightlyName;

    private const string SchedulePath =
        CadenceApiDefaults.UiPath + "/jobs/" + JobName + "/schedule";

    /// <summary>The seeded row's expression, which the audit line has to report as the old one.</summary>
    private const string OldCron = "0 3 * * *";

    private const string NewCron = "0 0 4 * * *";

    private static readonly CadenceUiMapOptions Open =
        new() { CookiePolicy = false, LoopbackOnly = false };

    private static readonly CadenceUiMapOptions Cookie =
        new() { CookiePolicy = true, LoopbackOnly = false };

    [Fact]
    public async Task WritesTheScheduleAndReturnsItsNewVersion()
    {
        var source = new FakeWritableScheduleSource();

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await response.Content.ReadFromJsonAsync<ScheduleResponse>();

        Assert.Equal(NewCron, source.Last!.CronExpression);
        Assert.Equal(NewCron, stored!.CronExpression);
        Assert.Equal(JobName, stored.JobName);

        // The store's version, not the one the editor sent: that is what makes the next write safe.
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task WritesTheRestOfTheRowAndNotJustTheCron()
    {
        var source = new FakeWritableScheduleSource();

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath,
            new ScheduleWriteRequest(
                NewCron,
                ApiTestJobs.ZonedTimeZone,
                Enabled: false,
                nameof(OverlapPolicy.AllowConcurrent),
                TimeSpan.FromMinutes(30),
                new Dictionary<string, string> { ["batch"] = "500" },
                Version: 0));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var written = source.Last!;

        Assert.Equal(ApiTestJobs.ZonedTimeZone, written.TimeZoneId);
        Assert.False(written.Enabled);
        Assert.Equal(OverlapPolicy.AllowConcurrent, written.Overlap);
        Assert.Equal(TimeSpan.FromMinutes(30), written.MaxDuration);
        Assert.Equal("500", written.Settings["batch"]);
    }

    [Theory]
    [InlineData("not a cron", "UTC", "invalid-cron", "cronExpression")]
    [InlineData(NewCron, "Mars/Olympus", "unknown-time-zone", "timeZoneId")]
    public async Task RefusesInvalidInputWithAProblemNamingTheField(
        string cron, string zone, string slug, string field)
    {
        var source = new FakeWritableScheduleSource();

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(SchedulePath, Edit(cron, zone));

        var problem = await RefusalAsync(response, HttpStatusCode.BadRequest);

        Assert.Equal($"urn:cadence:problem:{slug}", problem.Type);
        Assert.Contains(field, problem.Detail!, StringComparison.Ordinal);

        // Refused before the store saw it: an unparseable expression must never reach a row.
        Assert.Null(source.Last);
    }

    [Fact]
    public async Task RefusesAnOverlapPolicyThatNamesNothing()
    {
        var source = new FakeWritableScheduleSource();

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath, Edit(NewCron) with { Overlap = "whenever" });

        var problem = await RefusalAsync(response, HttpStatusCode.BadRequest);

        Assert.Equal("urn:cadence:problem:invalid-overlap-policy", problem.Type);
        Assert.Null(source.Last);
    }

    [Fact]
    public async Task AStaleVersionIsAConflict()
    {
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 4));

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron) with { Version = 3 });

        var problem = await RefusalAsync(response, HttpStatusCode.Conflict);

        Assert.Equal("urn:cadence:problem:schedule-conflict", problem.Type);
        Assert.Null(source.Last);
    }

    [Fact]
    public async Task TheRouteIsAbsentWithoutAWritableSource()
    {
        // 404 from routing, not a handler that mounted and then apologised: a deployment on a
        // read-only source has no schedule write to reach.
        await using var host = await ApiTestHost.StartAsync(
            configure: api => api.AllowUnauthenticated = true,
            endpoints: routes => CadenceUiRoutes.Map(routes, Open));

        var response = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AReadTokenCannotChangeTheSchedule()
    {
        // The tree's own policy admits a read-scoped token, so without Operate on the write a leaked
        // monitoring credential could move when work happens -- silently, and permanently.
        var source = new FakeWritableScheduleSource();
        var tokens = new FakeApiTokenStore();
        var (secret, digest) = ApiTokenSecret.Create();

        await tokens.CreateAsync(
            new ApiTokenCreation("monitoring", ApiTokenScope.Read, null, null, null), digest, default);

        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => Register(collection, source),
            store: tokens,
            endpoints: routes => CadenceUiRoutes.Map(routes, Cookie));

        host.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", secret);

        var response = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(source.Last);
    }

    [Fact]
    public async Task LogsWhoChangedTheScheduleAndFromWhatTo()
    {
        var logs = new LogCapture();
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 0));

        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => Register(collection, source),
            logs: logs,
            endpoints: routes => CadenceUiRoutes.Map(routes, Cookie));

        await host.SignInAsync("u1", "Ada Lovelace");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = Assert.Single(logs.Records, record => record.EventId == 3210);

        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Contains(JobName, audit.Message, StringComparison.Ordinal);
        Assert.Contains("Ada Lovelace", audit.Message, StringComparison.Ordinal);
        Assert.Contains(OldCron, audit.Message, StringComparison.Ordinal);
        Assert.Contains(NewCron, audit.Message, StringComparison.Ordinal);
    }

    private static ScheduleWriteRequest Edit(string cron, string zone = "UTC") =>
        new(cron, zone, Enabled: true, Overlap: null, MaxDuration: null, Settings: null, Version: 0);

    private static JobSchedule Stored(int version) => new()
    {
        JobName = JobName,
        CronExpression = OldCron,
        TimeZoneId = "UTC",
        Enabled = true,
        Version = version,
    };

    private static async Task<ProblemDetails> RefusalAsync(
        HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);

        return problem;
    }

    private static Task<ApiTestHost> StartAsync(FakeWritableScheduleSource source)
        => ApiTestHost.StartAsync(
            configure: api => api.AllowUnauthenticated = true,
            services: collection => Register(collection, source),
            endpoints: routes => CadenceUiRoutes.Map(routes, Open));

    // Both interfaces, as a storage package registers them: the gate reads the writable one and the
    // rest of the surface reads the schedules through the other.
    private static void Register(IServiceCollection collection, FakeWritableScheduleSource source)
    {
        collection.AddSingleton<IScheduleSource>(source);
        collection.AddSingleton<IWritableScheduleSource>(source);
    }
}
