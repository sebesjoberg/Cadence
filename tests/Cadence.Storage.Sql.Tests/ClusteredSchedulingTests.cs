using System.Diagnostics.Metrics;
using Cadence.DependencyInjection;
using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Scheduling;
using Cadence.Storage.Sql.Internal;
using Cadence.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// Several instances, one database, one occurrence.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the whole v0.2 milestone exists to make possible. The guarantee — at most one
/// instance <em>starts</em> a given occurrence — cannot be demonstrated in a single process against
/// the no-op coordinator, which grants everything: single-instance correctness says nothing about a
/// cluster.
/// </para>
/// <para>
/// Each instance gets its own service provider, its own executor and its own instance id, so nothing
/// is shared except the database. The tick loop is driven directly with a fake clock, so the test is
/// deterministic and instant rather than a race against a real timer.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ClusteredSchedulingTests
{
    private const string Hourly = "0 * * * *";
    private static readonly DateTimeOffset Start = new(2026, 8, 24, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public ClusteredSchedulingTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Two_instances_run_one_occurrence_once()
    {
        await using var cluster = await Cluster.CreateAsync(_fixture, instances: 2);

        await cluster.TickAllAsync();                                // seeds the evaluation point
        await cluster.AdvanceAndTickAllAsync(TimeSpan.FromMinutes(30));

        var runs = await cluster.RunsAsync();
        var run = Assert.Single(runs);

        Assert.Equal(Occurrence, run.ScheduledFor);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        // Exactly one instance executed the job, and history says which.
        Assert.Contains(run.InstanceId, cluster.InstanceIds);
        Assert.Equal(1, cluster.TotalExecutions);
    }

    [SkippableFact]
    public async Task Five_instances_ticking_together_still_run_one_occurrence_once()
    {
        // Instances in a cluster with synchronised clocks all tick on the same second, so the
        // contended case is the normal one rather than an edge case.
        await using var cluster = await Cluster.CreateAsync(_fixture, instances: 5);

        await cluster.TickAllAsync();
        await cluster.AdvanceAllAsync(TimeSpan.FromMinutes(30));
        await cluster.TickAllConcurrentlyAsync();
        await cluster.WaitForIdleAsync();

        Assert.Single(await cluster.RunsAsync());
        Assert.Equal(1, cluster.TotalExecutions);
    }

    [SkippableFact]
    public async Task Successive_occurrences_can_land_on_different_instances()
    {
        // The claim is per occurrence, not per job, so nothing pins a job to whichever instance won
        // last time. Both slots run exactly once between them, which is the property that matters.
        await using var cluster = await Cluster.CreateAsync(_fixture, instances: 2);

        await cluster.TickAllAsync();
        await cluster.AdvanceAndTickAllAsync(TimeSpan.FromMinutes(30));   // 11:00
        await cluster.AdvanceAndTickAllAsync(TimeSpan.FromHours(1));      // 12:00

        var runs = await cluster.RunsAsync();

        Assert.Equal(2, runs.Count);
        Assert.Equal(2, cluster.TotalExecutions);
        Assert.Equal(
            [Occurrence, Occurrence.AddHours(1)],
            runs.Select(r => r.ScheduledFor).Order());
    }

    [SkippableFact]
    public async Task A_schedule_edited_through_one_instance_reaches_the_others()
    {
        // The product claim: schedules live in a database and change at runtime. An edit that only
        // took effect on the instance that made it would make the dashboard misleading.
        await using var cluster = await Cluster.CreateAsync(_fixture, instances: 2);

        await cluster.TickAllAsync();

        var stored = await cluster.Sources[0].GetAsync("clustered-job", default);
        Assert.NotNull(stored);

        await cluster.Sources[0].UpsertAsync(stored with { Enabled = false }, default);

        // Every instance polls the version row and picks the change up.
        foreach (var source in cluster.Sources)
        {
            await source.PollAsync(default);
        }

        await cluster.AdvanceAndTickAllAsync(TimeSpan.FromMinutes(30));

        Assert.Empty(await cluster.RunsAsync());
        Assert.Equal(0, cluster.TotalExecutions);
    }

    /// <summary>
    /// A set of independent Cadence instances over one database.
    /// </summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private readonly List<Instance> _instances = [];
        private SqlStorageOptions _options = null!;

        public IReadOnlyList<string> InstanceIds => [.. _instances.Select(i => i.Id)];

        public IReadOnlyList<SqlScheduleSource> Sources => [.. _instances.Select(i => i.Source)];

        /// <summary>How many times the job body actually ran, across every instance.</summary>
        public int TotalExecutions => _instances.Sum(i => i.Counter.Count);

        public static async Task<Cluster> CreateAsync(SqlServerFixture fixture, int instances)
        {
            var cluster = new Cluster
            {
                _options = await fixture.CreateMigratedAsync("cluster"),
            };

            // Seed the schedule once, through a source that is then thrown away, so no instance has a
            // head start on the others.
            var seed = new SqlScheduleSource(
                new SqlDatabase(cluster._options),
                cluster._options,
                new FixedClock { UtcNow = Start },
                NullLogger<SqlScheduleSource>.Instance);

            await seed.UpsertAsync(
                new JobSchedule
                {
                    JobName = "clustered-job",
                    CronExpression = Hourly,
                    TimeZoneId = "UTC",
                    Enabled = true,
                },
                default);

            seed.Dispose();

            for (var i = 0; i < instances; i++)
            {
                cluster._instances.Add(Instance.Create(cluster._options, $"instance-{i}"));
            }

            return cluster;
        }

        /// <summary>Ticks every instance in turn, then waits for the work to finish.</summary>
        public async Task TickAllAsync()
        {
            foreach (var instance in _instances)
            {
                await instance.Service.TickAsync(instance.Clock.UtcNow, default);
            }

            await WaitForIdleAsync();
        }

        /// <summary>Ticks every instance at once, so their claims genuinely collide.</summary>
        public async Task TickAllConcurrentlyAsync()
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var ticks = _instances.Select(async instance =>
            {
                await ready.Task;
                await instance.Service.TickAsync(instance.Clock.UtcNow, default);
            }).ToArray();

            ready.SetResult();
            await Task.WhenAll(ticks);
        }

        public Task AdvanceAllAsync(TimeSpan by)
        {
            foreach (var instance in _instances)
            {
                instance.Clock.Advance(by);
            }

            return Task.CompletedTask;
        }

        public async Task AdvanceAndTickAllAsync(TimeSpan by)
        {
            await AdvanceAllAsync(by);
            await TickAllAsync();
        }

        public async Task WaitForIdleAsync()
        {
            foreach (var instance in _instances)
            {
                await instance.Executor.WaitForIdleAsync();
            }
        }

        /// <summary>Every run in the shared database, whichever instance wrote it.</summary>
        public Task<IReadOnlyList<JobRun>> RunsAsync()
            => _instances[0].History.QueryAsync(new RunQuery { JobName = "clustered-job" }, default);

        public async ValueTask DisposeAsync()
        {
            foreach (var instance in _instances)
            {
                await instance.DisposeAsync();
            }
        }

        private sealed record Instance(
            string Id,
            FixedClock Clock,
            CadenceHostedService Service,
            JobExecutor Executor,
            SqlRunHistoryStore History,
            SqlScheduleSource Source,
            ExecutionCounter Counter,
            ServiceProvider Provider) : IAsyncDisposable
        {
            public static Instance Create(SqlStorageOptions options, string instanceId)
            {
                var clock = new FixedClock { UtcNow = Start };
                var database = new SqlDatabase(options);
                var counter = new ExecutionCounter();

                var services = new ServiceCollection();
                services.AddMetrics();
                services.AddSingleton(counter);
                services.AddTransient<CountingJob>();
                var provider = services.BuildServiceProvider();

                var registry = new JobRegistry(
                [
                    new JobDescriptor
                    {
                        Name = "clustered-job",
                        ImplementationType = typeof(CountingJob),
                        DefaultCron = Hourly,
                    },
                ]);

                var cadenceOptions = Options.Create(new CadenceOptions { InstanceId = instanceId });

                var history = new SqlRunHistoryStore(
                    database, options, NullLogger<SqlRunHistoryStore>.Instance);

                var source = new SqlScheduleSource(
                    database, options, clock, NullLogger<SqlScheduleSource>.Instance);

                var coordinator = new SqlOccurrenceCoordinator(
                    database, clock, cadenceOptions, NullLogger<SqlOccurrenceCoordinator>.Instance);

                var metrics = new CadenceMetrics(provider.GetRequiredService<IMeterFactory>());
                var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

                var executor = new JobExecutor(
                    scopeFactory,
                    history,
                    new RunHistoryProgressSink(history, clock, NullLogger<RunHistoryProgressSink>.Instance),
                    clock,
                    metrics,
                    cadenceOptions,
                    NullLogger<JobExecutor>.Instance);

                var service = new CadenceHostedService(
                    registry,
                    new ScheduleResolver(registry, source),
                    source,
                    coordinator,
                    history,
                    executor,
                    new JobGraphValidator(
                        registry,
                        scopeFactory,
                        new RegistrationDiagnostics([]),
                        cadenceOptions,
                        NullLogger<JobGraphValidator>.Instance),
                    new LastSuccessCache(clock),
                    clock,
                    metrics,
                    cadenceOptions,
                    NullLogger<CadenceHostedService>.Instance);

                return new Instance(
                    instanceId, clock, service, executor, history, source, counter, provider);
            }

            public async ValueTask DisposeAsync()
            {
                await Executor.DisposeAsync();
                await History.DisposeAsync();
                Source.Dispose();
                await Provider.DisposeAsync();
            }
        }
    }

    /// <summary>Counts how many times the job body ran on one instance.</summary>
    private sealed class ExecutionCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingJob : IJob
    {
        private readonly ExecutionCounter _counter;

        public CountingJob(ExecutionCounter counter) => _counter = counter;

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            _counter.Increment();
            return Task.CompletedTask;
        }
    }
}
