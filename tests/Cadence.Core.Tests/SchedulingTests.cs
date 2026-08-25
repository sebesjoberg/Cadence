using System.Diagnostics.Metrics;
using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Scheduling;
using Cadence.Storage;
using Cadence.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Core.Tests;

/// <summary>
/// Drives the tick loop with a fake clock. Nothing here sleeps, and nothing depends on a real
/// timer firing.
/// </summary>
public class SchedulingTests
{
    private const string Hourly = "0 * * * *";

    [Fact]
    public async Task NothingRunsBeforeAnOccurrenceIsDue()
    {
        await using var host = TickHost.Create(Hourly);

        await host.TickAsync();                            // 10:30 — seeds the evaluation point
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(20));   // 10:50

        Assert.Empty(await host.RunsAsync());
    }

    [Fact]
    public async Task ADueOccurrenceRunsExactlyOnce()
    {
        await using var host = TickHost.Create(Hourly);

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));   // 11:00 is now due
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(1));    // and must not run again

        var run = Assert.Single(await host.RunsAsync());
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(TriggerKind.Schedule, run.Trigger);
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 11, 0), run.ScheduledFor);
    }

    [Fact]
    public async Task TheClaimIsTakenForTheOccurrenceInstantNotTheCurrentTime()
    {
        await using var host = TickHost.Create(Hourly);

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(4));

        // The tick noticed at 11:00:04 but the claim key must be the occurrence, so two instances
        // whose clocks differ still contend for the same slot.
        var attempt = Assert.Single(host.Coordinator.Attempts);
        Assert.Equal("scheduled-job", attempt.JobName);
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 11, 0), attempt.Occurrence);
    }

    [Fact]
    public async Task TheRunIsRecordedUnderTheIdTheClaimWasTakenWith()
    {
        await using var host = TickHost.Create(Hourly);

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));

        // A store where the claim and the run are the same row depends on this: if the executor
        // invented its own id after the claim, the two writes would collide on the occurrence.
        var attempt = Assert.Single(host.Coordinator.Attempts);
        var run = Assert.Single(await host.RunsAsync());
        Assert.Equal(attempt.RunId, run.RunId);
        Assert.NotEqual(Guid.Empty, attempt.RunId);
    }

    [Fact]
    public async Task ASkippedOccurrenceIsRecordedUnderTheClaimedId()
    {
        await using var host = TickHost.Create(Hourly, maxConcurrentRuns: 0);

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));

        // The claim already consumed the slot, so the skip record has to reuse its identity rather
        // than introduce a second one for the same occurrence.
        var attempt = Assert.Single(host.Coordinator.Attempts);
        var run = Assert.Single(await host.RunsAsync());
        Assert.Equal(RunStatus.Skipped, run.Status);
        Assert.Equal(attempt.RunId, run.RunId);
    }

    [Fact]
    public async Task LosingTheClaimMeansNoRunAndNoHistoryRow()
    {
        await using var host = TickHost.Create(Hourly, grantClaims: false);

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));

        Assert.Single(host.Coordinator.Attempts);
        Assert.Empty(await host.RunsAsync());
    }

    [Fact]
    public async Task ADisabledJobDoesNotRun()
    {
        await using var host = TickHost.Create(Hourly, enabled: false);

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromHours(3));

        Assert.Empty(await host.RunsAsync());
        Assert.Empty(host.Coordinator.Attempts);
    }

    [Fact]
    public async Task ReEnablingAJobDoesNotReplayThePeriodItWasDisabledFor()
    {
        await using var host = TickHost.Create(Hourly, enabled: false, onMissed: MissedRunPolicy.RunAll);

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromHours(5));      // 15:30, still disabled

        host.Source.SetEnabled("scheduled-job", enabled: true);

        // Past the config poll interval, so the tick picks the change up.
        await host.AdvanceAndTickAsync(TimeSpan.FromSeconds(30));   // 15:30:30

        // Deliberate deviation from a literal reading of the spec: a disabled job's occurrences are
        // treated as never having existed. Replaying five hours of runs because someone toggled a
        // checkbox is a footgun even with the catch-up cap.
        Assert.Empty(await host.RunsAsync());

        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));   // 16:00:30 — one occurrence due

        var run = Assert.Single(await host.RunsAsync());
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 16, 0), run.ScheduledFor);
    }

    [Fact]
    public async Task AStoredCronExpressionOverridesTheCodeDefault()
    {
        // The code default is hourly; the store says every 15 minutes.
        await using var host = TickHost.Create(Hourly, storedCron: "*/15 * * * *");

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(15));   // 10:45

        Assert.Single(await host.RunsAsync());
    }

    [Fact]
    public async Task OneJobWithAnUnusableExpressionDoesNotStopTheOthers()
    {
        await using var host = TickHost.Create(Hourly, secondJobCron: "definitely not cron");

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));

        var runs = await host.RunsAsync();
        Assert.All(runs, r => Assert.Equal("scheduled-job", r.JobName));
        Assert.Single(runs);
    }

    [Fact]
    public async Task ATickThatSpansSeveralOccurrencesAppliesTheMissedRunPolicy()
    {
        await using var host = TickHost.Create(Hourly, onMissed: MissedRunPolicy.RunOnce);

        await host.TickAsync();
        await host.AdvanceAndTickAsync(TimeSpan.FromHours(4));      // 14:30: 11:00-14:00 all due

        var run = Assert.Single(await host.RunsAsync());
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 14, 0), run.ScheduledFor);
    }

    [Fact]
    public async Task APausedScheduleClaimsNothing()
    {
        await using var host = TickHost.Create(Hourly);

        await host.TickAsync();
        await host.Pauses.SetAsync(PauseScope.Schedule, "incident", "ops", default);
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));   // 11:00 would be due

        Assert.Empty(await host.RunsAsync());
    }

    [Fact]
    public async Task ResumingDoesNotReplayThePausedWindow()
    {
        await using var host = TickHost.Create(Hourly);

        await host.TickAsync();
        await host.Pauses.SetAsync(PauseScope.Schedule, "incident", "ops", default);

        await host.AdvanceAndTickAsync(TimeSpan.FromHours(3));      // 11:00, 12:00, 13:00 pass
        await host.Pauses.SetAsync(PauseScope.None, reason: null, setBy: null, default);
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(31));   // 14:00

        var run = Assert.Single(await host.RunsAsync());
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 14, 0), run.ScheduledFor);
    }

    [Fact]
    public async Task PausingTriggersLeavesTheScheduleRunning()
    {
        await using var host = TickHost.Create(Hourly);

        await host.TickAsync();
        await host.Pauses.SetAsync(PauseScope.Triggers, "incident", "ops", default);
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));

        Assert.Single(await host.RunsAsync());

        await Assert.ThrowsAsync<SchedulerPausedException>(
            () => host.Trigger.TriggerAsync("scheduled-job"));
    }

    [Fact]
    public async Task PausingTheScheduleLeavesTriggersWorking()
    {
        await using var host = TickHost.Create(Hourly);

        await host.TickAsync();
        await host.Pauses.SetAsync(PauseScope.Schedule, "incident", "ops", default);
        await host.AdvanceAndTickAsync(TimeSpan.FromMinutes(30));

        var result = await host.Trigger.TriggerAsync("scheduled-job");
        await host.WaitForIdleAsync();

        Assert.True(result.WasStarted);

        var run = Assert.Single(await host.RunsAsync());
        Assert.Null(run.ScheduledFor);
        Assert.Equal(TriggerKind.Manual, run.Trigger);
    }

    private sealed class TickHost : IAsyncDisposable
    {
        private ServiceProvider _provider = null!;
        private ScheduleTicker _ticker = null!;
        private JobExecutor _executor = null!;
        private FakeClock _clock = null!;

        public MutableScheduleSource Source { get; } = new();

        public ScriptedCoordinator Coordinator { get; private set; } = null!;

        public InMemoryRunHistoryStore History { get; } = new();

        public InMemoryPauseStore Pauses { get; private set; } = null!;

        public JobTrigger Trigger { get; private set; } = null!;

        public static TickHost Create(
            string codeCron,
            string? storedCron = null,
            bool enabled = true,
            bool grantClaims = true,
            MissedRunPolicy onMissed = MissedRunPolicy.SkipToNext,
            string? secondJobCron = null,
            int? maxConcurrentRuns = null)
        {
            var clock = new FakeClock(Occurrences.Utc(2026, 8, 24, 10, 30));

            var host = new TickHost
            {
                Coordinator = new ScriptedCoordinator(grantClaims),
                Pauses = new InMemoryPauseStore(clock),
                _clock = clock,
            };

            var descriptors = new List<JobDescriptor>
            {
                new()
                {
                    Name = "scheduled-job",
                    ImplementationType = typeof(SucceedingJob),
                    DefaultCron = codeCron,
                    AllowedTriggers = TriggerKind.Schedule | TriggerKind.Manual,
                    OnMissed = onMissed,
                },
            };

            host.Source.Set(new JobSchedule
            {
                JobName = "scheduled-job",
                CronExpression = storedCron ?? codeCron,
                TimeZoneId = "UTC",
                Enabled = enabled,
            });

            if (secondJobCron is not null)
            {
                descriptors.Add(new JobDescriptor
                {
                    Name = "broken-job",
                    ImplementationType = typeof(ReportingJob),
                    DefaultCron = secondJobCron,
                });
            }

            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton(new JobSpy());
            services.AddScoped<ScopeMarker>();
            services.AddTransient<SucceedingJob>();
            services.AddTransient<ReportingJob>();
            host._provider = services.BuildServiceProvider();

            var registry = new JobRegistry(descriptors);
            var options = Options.Create(new CadenceOptions
            {
                InstanceId = "test:1:aaaaaaaa",
                ConfigPollInterval = TimeSpan.FromSeconds(15),
                // Zero is below what Validate() allows, which is the point: it forces the capacity
                // gate without the test having to start a long-running job first.
                MaxConcurrentRuns = maxConcurrentRuns ?? 20,
            });

            var metrics = new CadenceMetrics(host._provider.GetRequiredService<IMeterFactory>());
            var scopeFactory = host._provider.GetRequiredService<IServiceScopeFactory>();

            host._executor = new JobExecutor(
                scopeFactory,
                host.History,
                new RunHistoryProgressSink(host.History, host._clock, NullLogger<RunHistoryProgressSink>.Instance),
                host._clock,
                metrics,
                options,
                NullLogger<JobExecutor>.Instance);

            var resolver = new ScheduleResolver(registry, host.Source);
            host.Trigger = new JobTrigger(registry, resolver, host.Pauses, host._executor);

            host._ticker = new ScheduleTicker(
                registry,
                resolver,
                host.Coordinator,
                host.History,
                host.Pauses,
                host._executor,
                new LastSuccessCache(host._clock),
                host._clock,
                metrics,
                options,
                NullLogger<ScheduleTicker>.Instance);

            return host;
        }

        public async Task TickAsync()
        {
            await _ticker.TickAsync(_clock.UtcNow, CancellationToken.None);
            await _executor.WaitForIdleAsync();
        }

        public async Task AdvanceAndTickAsync(TimeSpan by)
        {
            _clock.Advance(by);
            await TickAsync();
        }

        public Task WaitForIdleAsync() => _executor.WaitForIdleAsync();

        public async Task<IReadOnlyList<JobRun>> RunsAsync()
            => await History.QueryAsync(new RunQuery { Limit = 100 }, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _executor.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
