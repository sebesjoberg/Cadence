using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cadence.Api.Routing;
using Cadence.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// §13.2's dividing line, as the operator tree implements it: a token can start work and stop work,
/// and only a person can change when work happens.
/// </summary>
public sealed class ScheduleEndpointTests
{
    private const string JobName = ApiTestJobs.NightlyName;

    private const string SchedulePath =
        CadenceApiDefaults.UiPath + "/jobs/" + JobName + "/schedule";

    private const string RoutePattern = "/jobs/{name}/schedule";

    /// <summary>The audit line's event id. Cadence.Api owns 3000-3007; 3200+ is the dashboard's.</summary>
    private const int AuditEventId = 3007;

    /// <summary>The seeded row's expression, which the audit line has to report as the old one.</summary>
    private const string OldCron = "0 3 * * *";

    private const string NewCron = "0 0 4 * * *";

    /// <summary>The signed-in person every write here is made by, and the name the audit records.</summary>
    private const string Operator = "Ada Lovelace";

    /// <summary>Stands in for a policy the host owns, as an app with its own gate would write one.</summary>
    private const string HostPolicy = "cadence-ops";

    /// <summary>A machine credential, which is what the host policy below admits.</summary>
    private const string Token = "s3cret-token-value-32-chars-long";

    private static readonly CadenceUiMapOptions Open =
        new() { CookiePolicy = false, LoopbackOnly = false };

    private static readonly CadenceUiMapOptions Cookie =
        new() { CookiePolicy = true, LoopbackOnly = false };

    private static readonly CadenceUiMapOptions UnderHostPolicy =
        new() { CookiePolicy = false, LoopbackOnly = false, PolicyName = HostPolicy };

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

    [Fact]
    public async Task ReadsTheStoredScheduleSoTheEditorHasAVersionToSpend()
    {
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 7));

        await using var host = await StartAsync(source);

        var schedule = await host.Client.GetFromJsonAsync<ScheduleResponse>(SchedulePath);

        Assert.Equal(OldCron, schedule!.CronExpression);
        Assert.Equal(7, schedule.Version);
    }

    [Fact]
    public async Task ReadsTheCodeDeclaredScheduleWhereTheSourceHoldsNoRow()
    {
        // Version zero, which is what the write then reads as "there was nothing here".
        var source = new FakeWritableScheduleSource();

        await using var host = await StartAsync(source);

        var schedule = await host.Client.GetFromJsonAsync<ScheduleResponse>(SchedulePath);

        Assert.Equal(ApiTestJobs.NightlyCron, schedule!.CronExpression);
        Assert.Equal(0, schedule.Version);
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

    // Zero cancels every run the instant it begins and a negative value throws inside the executor,
    // so this route enforces what [ScheduledJob] and JobBuilder.MaxDuration enforce at startup.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task RefusesAMaxDurationThatIsNotPositive(int minutes)
    {
        var source = new FakeWritableScheduleSource();

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath, Edit(NewCron) with { MaxDuration = TimeSpan.FromMinutes(minutes) });

        var problem = await RefusalAsync(response, HttpStatusCode.BadRequest);

        Assert.Equal("urn:cadence:problem:invalid-max-duration", problem.Type);
        Assert.Null(source.Last);
    }

    [Fact]
    public async Task AStaleVersionIsAConflict()
    {
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 4));

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath, Edit(NewCron) with { Version = 3 });

        var problem = await RefusalAsync(response, HttpStatusCode.Conflict);

        Assert.Equal("urn:cadence:problem:schedule-conflict", problem.Type);
        Assert.Null(source.Last);
    }

    [Fact]
    public async Task AWriteOverAStoredRowWithoutAVersionIsAConflict()
    {
        // Both storage tiers read version zero as "just make it so", so defaulting an absent field
        // to zero would make forgetting it indistinguishable from asking for last-write-wins.
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 4));

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath, Edit(NewCron) with { Version = null });

        var problem = await RefusalAsync(response, HttpStatusCode.Conflict);

        Assert.Equal("urn:cadence:problem:schedule-conflict", problem.Type);
        Assert.Null(source.Last);
    }

    [Fact]
    public async Task AWriteThatOmitsSettingsKeepsTheStoredOnes()
    {
        // Absent means "I did not supply this", not "make it empty" -- the rule Version follows on
        // the same request object, and an editor that sends only a new cron must not wipe the rest.
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 4, settings: new Dictionary<string, string> { ["batch"] = "500" }));

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath, Edit(NewCron) with { Version = 4 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("500", source.Last!.Settings["batch"]);

        var stored = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.Equal("500", stored!.Settings["batch"]);
    }

    [Fact]
    public async Task AnEmptySettingsObjectClearsThem()
    {
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 4, settings: new Dictionary<string, string> { ["batch"] = "500" }));

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath,
            Edit(NewCron) with { Version = 4, Settings = new Dictionary<string, string>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(source.Last!.Settings);
    }

    [Fact]
    public async Task SettingsWithEntriesReplaceTheStoredOnesWholesale()
    {
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 4, settings: new Dictionary<string, string> { ["batch"] = "500" }));

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath,
            Edit(NewCron) with
            {
                Version = 4,
                Settings = new Dictionary<string, string> { ["retries"] = "3" },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var written = source.Last!.Settings;

        Assert.Equal("3", written["retries"]);
        Assert.DoesNotContain("batch", written.Keys);
    }

    [Fact]
    public async Task AFirstWriteNeedsNoVersion()
    {
        var source = new FakeWritableScheduleSource();

        await using var host = await StartAsync(source);

        var response = await host.Client.PutAsJsonAsync(
            SchedulePath, Edit(NewCron) with { Version = null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(NewCron, source.Last!.CronExpression);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task AnUnregisteredJobIsNotFoundAndTheDetailSaysHowManyThereAre(string method)
    {
        var source = new FakeWritableScheduleSource();

        await using var host = await StartAsync(source);

        const string Path = CadenceApiDefaults.UiPath + "/jobs/nothing-declares-this/schedule";

        var response = method == "GET"
            ? await host.Client.GetAsync(Path)
            : await host.Client.PutAsJsonAsync(Path, Edit(NewCron));

        var problem = await RefusalAsync(response, HttpStatusCode.NotFound);

        Assert.Equal("urn:cadence:problem:job-not-found", problem.Type);

        // §13.6: a replica that serves the dashboard and registers no jobs 404s every name, and the
        // count is what tells an operator that from the response body.
        Assert.Contains("registered job(s)", problem.Detail!, StringComparison.Ordinal);
        Assert.Null(source.Last);
    }

    [Fact]
    public async Task TheRouteIsAbsentWithoutAWritableSource()
    {
        // 404 from routing to a signed-in person, not 403: a deployment on a read-only source has
        // no schedule write to reach at all.
        await using var host = await ApiTestHost.StartWithOidcAsync(
            endpoints: routes => CadenceUiRoutes.Map(routes, Cookie));

        await SignInAsync(host);

        var response = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheMachineTreeNeverMountsTheScheduleRoutes()
    {
        // The milestone's defining rule: a changed cron expression is silent and permanent, so the
        // machine-callable tree carries no schedule route however the storage tier is configured.
        IReadOnlyList<Endpoint> built = [];
        var source = new FakeWritableScheduleSource();

        await using var host = await ApiTestHost.StartAsync(
            configure: api => api.AllowUnauthenticated = true,
            services: collection => Register(collection, source),
            endpoints: routes =>
            {
                CadenceUiRoutes.Map(routes, Open);
                built = [.. routes.DataSources.SelectMany(dataSource => dataSource.Endpoints)];
            });

        var patterns = built.OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains(CadenceApiDefaults.UiPath + RoutePattern, patterns);
        Assert.DoesNotContain(CadenceApiDefaults.ApiPath + RoutePattern, patterns);
    }

    [Fact]
    public async Task AnOperateTokenCannotChangeTheSchedule()
    {
        // The strongest scope a token can hold, and it is still not a person. Operate is what pause
        // and trigger take; §13.2 puts changing when work happens on the other side of the line.
        var source = new FakeWritableScheduleSource();
        var tokens = new FakeApiTokenStore();
        var (secret, digest) = ApiTokenSecret.Create();

        await tokens.CreateAsync(
            new ApiTokenCreation("deploy", ApiTokenScope.Operate, null, null, null), digest, default);

        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => Register(collection, source),
            store: tokens,
            endpoints: routes => CadenceUiRoutes.Map(routes, Cookie));

        host.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", secret);

        var write = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron));
        var read = await host.Client.GetAsync(SchedulePath);

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        // The read is gated with the write: a version is only useful to whoever may spend it.
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Null(source.Last);
    }

    // The one exception to the rule above, and §13.7 states it: a host-named policy governs alone,
    // so Cadence adds no user-principal check and whatever that policy admits -- here a bearer
    // token, which is not a person -- may rewrite the schedule. The same trade token administration
    // makes, and the branch that turns the milestone's defining check off, so it is tested.
    [Fact]
    public async Task AHostNamedPolicyGovernsTheScheduleWriteAlone()
    {
        var source = new FakeWritableScheduleSource();

        await using var host = await ApiTestHost.StartAsync(
            configure: api =>
            {
                api.Tokens.Add(Token);
                api.RequireAuthorization(HostPolicy);
            },
            services: collection =>
            {
                Register(collection, source);
                collection.AddAuthorizationBuilder().AddPolicy(
                    HostPolicy,
                    policy => policy
                        .AddAuthenticationSchemes(CadenceApiDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser());
            },
            endpoints: routes => CadenceUiRoutes.Map(routes, UnderHostPolicy));

        host.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);

        var write = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron));
        var read = await host.Client.GetAsync(SchedulePath);

        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(NewCron, source.Last!.CronExpression);
    }

    [Fact]
    public async Task LogsWhoChangedTheScheduleAndFromWhatTo()
    {
        var logs = new LogCapture();
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 0));

        await using var host = await StartAsync(source, logs: logs);

        var response = await host.Client.PutAsJsonAsync(SchedulePath, Edit(NewCron));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = Assert.Single(logs.Records, record => record.EventId == AuditEventId);

        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Contains(JobName, audit.Message, StringComparison.Ordinal);
        Assert.Contains(Operator, audit.Message, StringComparison.Ordinal);
        Assert.Contains(OldCron, audit.Message, StringComparison.Ordinal);
        Assert.Contains(NewCron, audit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedWriteIsNotAudited()
    {
        // The audit line answers "what changed"; a refusal changed nothing, and a log that says
        // otherwise is worse than no log.
        var logs = new LogCapture();
        var source = new FakeWritableScheduleSource();
        source.Seed(Stored(version: 4));

        await using var host = await StartAsync(source, logs: logs);

        var invalid = await host.Client.PutAsJsonAsync(SchedulePath, Edit("not a cron"));
        var stale = await host.Client.PutAsJsonAsync(
            SchedulePath, Edit(NewCron) with { Version = 3 });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.DoesNotContain(logs.Records, record => record.EventId == AuditEventId);
    }

    private static ScheduleWriteRequest Edit(string cron, string zone = "UTC") =>
        new(cron, zone, Enabled: true, Overlap: null, MaxDuration: null, Settings: null, Version: 0);

    private static JobSchedule Stored(
        int version, IReadOnlyDictionary<string, string>? settings = null) => new()
    {
        JobName = JobName,
        CronExpression = OldCron,
        TimeZoneId = "UTC",
        Enabled = true,
        Settings = settings ?? ImmutableDictionary<string, string>.Empty,
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

    /// <summary>
    /// The dashboard's own shape: a signed-in person on the cookie tree. Every route here needs one,
    /// because they require a user principal rather than a scope a token could hold.
    /// </summary>
    private static async Task<ApiTestHost> StartAsync(
        FakeWritableScheduleSource source, LogCapture? logs = null)
    {
        var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => Register(collection, source),
            logs: logs,
            endpoints: routes => CadenceUiRoutes.Map(routes, Cookie));

        await SignInAsync(host);

        return host;
    }

    private static async Task SignInAsync(ApiTestHost host)
    {
        await host.SignInAsync("u1", Operator);
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");
    }

    // Both interfaces, as a storage package registers them: the gate reads the writable one and the
    // rest of the surface reads the schedules through the other.
    private static void Register(IServiceCollection collection, FakeWritableScheduleSource source)
    {
        collection.AddSingleton<IScheduleSource>(source);
        collection.AddSingleton<IWritableScheduleSource>(source);
    }
}
