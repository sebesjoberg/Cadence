using System.Diagnostics.Metrics;
using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Scheduling;
using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Storage.Redis.Tests;

/// <summary>
/// Several instances, one Redis, one occurrence.
/// </summary>
/// <remarks>
/// <para>
/// The SQL tier has a test of this shape, and the point of having it twice is that "an alternative
/// to SQL Server" has to mean the same guarantee and not merely the same interface. The coordinator
/// conformance suite already proves the claim contends correctly; this proves the tick loop built on
/// top of it does too.
/// </para>
/// <para>
/// Each instance gets its own service provider, its own executor, its own connection and its own
/// instance id, so nothing is shared except Redis. Each drives the real <see cref="ScheduleTicker"/>
/// with a fake clock, so this is deterministic and instant rather than a race against a real timer.
/// </para>
/// <para>
/// The harness below is a near-twin of the SQL tier's. Unifying them means abstracting a factory for
/// three services over two very different construction paths, which is worth doing when a third tier
/// turns up and is not worth doing for the second.
/// </para>
/// </remarks>
[Collection(RedisCollectionDefinition.Name)]
public sealed class ClusteredSchedulingTests
{
    private const string Hourly = "0 * * * *";
    private const string JobName = "clustered-job";

    private static readonly DateTimeOffset Start = new(2026, 8, 24, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);

    private readonly RedisFixture _fixture;

    public ClusteredSchedulingTests(RedisFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task TwoInstancesRunOneOccurrenceOnce()
    {
        await using var cluster = await Cluster.CreateAsync(_fixture, instances: 2);

        await cluster.TickAllAsync();                                // seeds the evaluation point
        await cluster.AdvanceAndTickAllAsync(TimeSpan.FromMinutes(30));

        var run = Assert.Single(await cluster.RunsAsync());

        Assert.Equal(Occurrence, run.ScheduledFor);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Contains(run.InstanceId, cluster.InstanceIds);
        Assert.Equal(1, cluster.TotalExecutions);
    }

    [SkippableFact]
    public async Task FiveInstancesTickingTogetherStillRunOneOccurrenceOnce()
    {
        // Instances in a cluster with synchronised clocks all tick on the same second, so the
        // contended case is the normal one rather than an edge case.
        await using var cluster = await Cluster.CreateAsync(_fixture, instances: 5);

        await cluster.TickAllAsync();
        cluster.AdvanceAll(TimeSpan.FromMinutes(30));
        await cluster.TickAllConcurrentlyAsync();
        await cluster.WaitForIdleAsync();

        Assert.Single(await cluster.RunsAsync());
        Assert.Equal(1, cluster.TotalExecutions);
    }

    [SkippableFact]
    public async Task SuccessiveOccurrencesCanLandOnDifferentInstances()
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
    public async Task AScheduleEditedThroughOneInstanceReachesTheOthers()
    {
        // The product claim: schedules live in a store and change at runtime. An edit that only took
        // effect on the instance that made it would make the dashboard misleading.
        await using var cluster = await Cluster.CreateAsync(_fixture, instances: 2);

        await cluster.TickAllAsync();

        var stored = await cluster.Sources[0].GetAsync(JobName, default);
        Assert.NotNull(stored);

        await cluster.Sources[0].UpsertAsync(stored with { Enabled = false }, default);

        // Every instance polls the version and picks the change up. The push path reaches them too,
        // but polling is what makes this deterministic rather than a wait on a subscription.
        foreach (var source in cluster.Sources)
        {
            await source.PollAsync(default);
        }

        await cluster.AdvanceAndTickAllAsync(TimeSpan.FromMinutes(30));

        Assert.Empty(await cluster.RunsAsync());
        Assert.Equal(0, cluster.TotalExecutions);
    }

    /// <summary>A set of independent Cadence instances over one Redis key space.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private readonly List<Instance> _instances = [];

        public IReadOnlyList<string> InstanceIds => [.. _instances.Select(i => i.Id)];

        public IReadOnlyList<RedisScheduleSource> Sources => [.. _instances.Select(i => i.Source)];

        /// <summary>How many times the job body actually ran, across every instance.</summary>
        public int TotalExecutions => _instances.Sum(i => i.Counter.Count);

        public static async Task<Cluster> CreateAsync(RedisFixture fixture, int instances)
        {
            var options = fixture.CreateOptions("cluster");
            var cluster = new Cluster();

            // Seeded through a source that is then thrown away, so no instance has a head start.
            await using (var seedConnection = new RedisConnection(options))
            {
                await using var seed = new RedisScheduleSource(
                    seedConnection, options, NullLogger<RedisScheduleSource>.Instance);

                await seed.UpsertAsync(
                    new JobSchedule
                    {
                        JobName = JobName,
                        CronExpression = Hourly,
                        TimeZoneId = "UTC",
                        Enabled = true,
                    },
                    default);
            }

            for (var i = 0; i < instances; i++)
            {
                cluster._instances.Add(Instance.Create(options, $"instance-{i}"));
            }

            return cluster;
        }

        /// <summary>Ticks every instance in turn, then waits for the work to finish.</summary>
        public async Task TickAllAsync()
        {
            foreach (var instance in _instances)
            {
                await instance.Ticker.TickAsync(instance.Clock.UtcNow, default);
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
                await instance.Ticker.TickAsync(instance.Clock.UtcNow, default);
            }).ToArray();

            ready.SetResult();
            await Task.WhenAll(ticks);
        }

        public void AdvanceAll(TimeSpan by)
        {
            foreach (var instance in _instances)
            {
                instance.Clock.Advance(by);
            }
        }

        public async Task AdvanceAndTickAllAsync(TimeSpan by)
        {
            AdvanceAll(by);
            await TickAllAsync();
        }

        public async Task WaitForIdleAsync()
        {
            foreach (var instance in _instances)
            {
                await instance.Executor.WaitForIdleAsync();
            }
        }

        /// <summary>Every run in the shared key space, whichever instance wrote it.</summary>
        public Task<IReadOnlyList<JobRun>> RunsAsync()
            => _instances[0].History.QueryAsync(new RunQuery { JobName = JobName }, default);

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
            ScheduleTicker Ticker,
            JobExecutor Executor,
            RedisRunHistoryStore History,
            RedisScheduleSource Source,
            RedisConnection Connection,
            ExecutionCounter Counter,
            ServiceProvider Provider) : IAsyncDisposable
        {
            public static Instance Create(RedisStorageOptions options, string instanceId)
            {
                var clock = new FixedClock { UtcNow = Start };
                var connection = new RedisConnection(options);
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
                        Name = JobName,
                        ImplementationType = typeof(CountingJob),
                        DefaultCron = Hourly,
                    },
                ]);

                var cadenceOptions = Options.Create(new CadenceOptions { InstanceId = instanceId });

                var history = new RedisRunHistoryStore(
                    connection, options, NullLogger<RedisRunHistoryStore>.Instance);

                var source = new RedisScheduleSource(
                    connection, options, NullLogger<RedisScheduleSource>.Instance);

                var coordinator = new RedisOccurrenceCoordinator(connection, clock, cadenceOptions);
                var pauses = new RedisPauseStore(connection, clock);

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

                var ticker = new ScheduleTicker(
                    registry,
                    new ScheduleResolver(registry, source),
                    coordinator,
                    history,
                    pauses,
                    executor,
                    new LastSuccessCache(clock),
                    clock,
                    metrics,
                    cadenceOptions,
                    NullLogger<ScheduleTicker>.Instance);

                return new Instance(
                    instanceId, clock, ticker, executor, history, source, connection, counter, provider);
            }

            public async ValueTask DisposeAsync()
            {
                await Executor.DisposeAsync();
                await History.DisposeAsync();
                await Source.DisposeAsync();
                await Connection.DisposeAsync();
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
