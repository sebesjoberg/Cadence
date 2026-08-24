using Cadence.Storage.Sql.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// The janitor's four passes, against a real database.
/// </summary>
/// <remarks>
/// Every pass is driven directly with a fake clock rather than by waiting for the timer, so nothing
/// here sleeps and nothing is timing-dependent.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class CadenceJanitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public CadenceJanitorTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task History_older_than_the_retention_age_is_purged()
    {
        var harness = await CreateAsync("age", retention: new RetentionOptions { MaxAge = TimeSpan.FromDays(7) });

        var old = await harness.RecordAsync("job", Now.AddDays(-30), RunStatus.Succeeded);
        var recent = await harness.RecordAsync("job", Now.AddDays(-1), RunStatus.Succeeded);

        await harness.Janitor.RunPassAsync(default);

        var remaining = await harness.History.QueryAsync(new RunQuery { JobName = "job" }, default);

        Assert.Equal(recent, Assert.Single(remaining).RunId);
        Assert.DoesNotContain(old, remaining.Select(r => r.RunId));
    }

    [SkippableFact]
    public async Task Each_job_is_trimmed_to_the_per_job_cap_keeping_the_newest()
    {
        var harness = await CreateAsync(
            "trim", retention: new RetentionOptions { MaxRunsPerJob = 3, MaxAge = TimeSpan.FromDays(365) });

        var ids = new List<Guid>();

        for (var i = 0; i < 8; i++)
        {
            ids.Add(await harness.RecordAsync("job", Now.AddMinutes(i), RunStatus.Succeeded));
        }

        await harness.Janitor.RunPassAsync(default);

        var remaining = await harness.History.QueryAsync(new RunQuery { JobName = "job" }, default);

        Assert.Equal(3, remaining.Count);
        Assert.Equal(ids[^3..].Order(), remaining.Select(r => r.RunId).Order());
    }

    [SkippableFact]
    public async Task The_per_job_cap_is_applied_per_job_not_across_all_jobs()
    {
        var harness = await CreateAsync(
            "perjob", retention: new RetentionOptions { MaxRunsPerJob = 2, MaxAge = TimeSpan.FromDays(365) });

        for (var i = 0; i < 4; i++)
        {
            await harness.RecordAsync("a", Now.AddMinutes(i), RunStatus.Succeeded);
            await harness.RecordAsync("b", Now.AddMinutes(i), RunStatus.Succeeded);
        }

        await harness.Janitor.RunPassAsync(default);

        Assert.Equal(2, (await harness.History.QueryAsync(new RunQuery { JobName = "a" }, default)).Count);
        Assert.Equal(2, (await harness.History.QueryAsync(new RunQuery { JobName = "b" }, default)).Count);
    }

    [SkippableFact]
    public async Task A_run_left_behind_by_a_dead_instance_becomes_lost()
    {
        var harness = await CreateAsync("reap");

        // An instance that heartbeated once and then stopped -- a process that was killed, so nothing
        // ever recorded an outcome for the run it was executing.
        await harness.RegisterInstanceAsync("dead", Now.AddMinutes(-30));
        var abandoned = await harness.RecordAsync("job", Now.AddMinutes(-25), RunStatus.Running, "dead");

        await harness.Janitor.RunPassAsync(default);

        var run = await harness.History.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(abandoned, run.RunId);

        // Lost, not Aborted: Aborted means a shutdown cancelled the run and something recorded that.
        // Nobody recorded anything here, which is a different fact.
        Assert.Equal(RunStatus.Lost, run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.NotNull(run.Duration);
    }

    [SkippableFact]
    public async Task A_live_instance_keeps_its_running_run()
    {
        var harness = await CreateAsync("live");

        await harness.RegisterInstanceAsync("alive", Now.AddSeconds(-5));
        await harness.RecordAsync("job", Now.AddMinutes(-45), RunStatus.Running, "alive");

        await harness.Janitor.RunPassAsync(default);

        var run = await harness.History.GetLastRunAsync("job", default);

        // Still running, however long it has been going. Duration is not evidence of death; a missing
        // heartbeat is. Reaping on age alone would report failures that never happened for any job
        // legitimately slower than the timeout.
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Running, run.Status);
    }

    [SkippableFact]
    public async Task A_running_run_whose_instance_deregistered_is_reaped()
    {
        // A graceful stop deletes the instance row. A run still Running afterwards was never
        // completed either, so it should not wait out the heartbeat timeout to be resolved.
        var harness = await CreateAsync("gone");

        await harness.RecordAsync("job", Now.AddMinutes(-2), RunStatus.Running, "vanished");

        await harness.Janitor.RunPassAsync(default);

        var run = await harness.History.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(RunStatus.Lost, run.Status);
    }

    [SkippableFact]
    public async Task A_reaped_run_extends_the_failure_streak()
    {
        // The point of reaping: a job whose host keeps dying should trip an alert threshold, not sit
        // at zero consecutive failures forever because nothing ever wrote a failure.
        var harness = await CreateAsync("streak");

        await harness.RecordAsync("job", Now.AddMinutes(-5), RunStatus.Running, "vanished");
        await harness.Janitor.RunPassAsync(default);

        Assert.Equal(1, await harness.History.CountConsecutiveFailuresAsync("job", default));
    }

    [SkippableFact]
    public async Task A_long_dead_instance_row_is_removed()
    {
        var harness = await CreateAsync("instances");

        await harness.RegisterInstanceAsync("ancient", Now.AddDays(-2));
        await harness.RegisterInstanceAsync("current", Now);

        await harness.Janitor.RunPassAsync(default);

        var remaining = await harness.Database.QueryAsync(
            $"SELECT InstanceId FROM {harness.Database.Table("CadenceInstance")};",
            bind: null,
            reader => reader.GetString(0),
            default);

        Assert.Equal("current", Assert.Single(remaining));
    }

    [SkippableFact]
    public async Task A_recently_dead_instance_row_outlives_the_decision_that_it_was_dead()
    {
        // Deleting the row at the same moment its runs are reaped would leave history pointing at an
        // instance nothing can explain -- which is exactly the question someone reads history to
        // answer after an incident.
        var harness = await CreateAsync("keepdead");

        await harness.RegisterInstanceAsync("recent", Now.AddMinutes(-5));
        await harness.RecordAsync("job", Now.AddMinutes(-4), RunStatus.Running, "recent");

        await harness.Janitor.RunPassAsync(default);

        var run = await harness.History.GetLastRunAsync("job", default);
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Lost, run.Status);

        var instances = await harness.Database.QueryAsync(
            $"SELECT InstanceId FROM {harness.Database.Table("CadenceInstance")};",
            bind: null,
            reader => reader.GetString(0),
            default);

        Assert.Contains("recent", instances);
    }

    [SkippableFact]
    public async Task A_pass_over_an_empty_database_does_nothing_and_does_not_throw()
    {
        var harness = await CreateAsync("empty");

        await harness.Janitor.RunPassAsync(default);
        await harness.Janitor.RunPassAsync(default);

        Assert.Empty(await harness.History.QueryAsync(new RunQuery(), default));
    }

    [SkippableFact]
    public async Task Batching_does_not_change_the_outcome()
    {
        // The batch loop has to keep going until a pass is done, or a backlog larger than one batch
        // would be trimmed a slice per interval and never catch up.
        var harness = await CreateAsync(
            "batched",
            retention: new RetentionOptions { MaxRunsPerJob = 1, MaxAge = TimeSpan.FromDays(365) },
            configure: o => o.JanitorBatchSize = 2);

        for (var i = 0; i < 9; i++)
        {
            await harness.RecordAsync("job", Now.AddMinutes(i), RunStatus.Succeeded);
        }

        await harness.Janitor.RunPassAsync(default);

        Assert.Single(await harness.History.QueryAsync(new RunQuery { JobName = "job" }, default));
    }

    [SkippableFact]
    public async Task Purging_a_run_takes_its_progress_entries_with_it()
    {
        var harness = await CreateAsync(
            "cascade", retention: new RetentionOptions { MaxAge = TimeSpan.FromDays(1) });

        var runId = await harness.RecordAsync("job", Now.AddDays(-10), RunStatus.Succeeded);

        await harness.History.AppendLogAsync(
            runId, new JobLogEntry { Timestamp = Now.AddDays(-10), Message = "progress" }, default);

        await harness.History.FlushProgressAsync(default);

        await harness.Janitor.RunPassAsync(default);

        var orphans = await harness.Database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {harness.Database.Table("CadenceJobRunLog")};", bind: null, default);

        Assert.Equal(0, orphans);
    }

    private async Task<Harness> CreateAsync(
        string label,
        RetentionOptions? retention = null,
        Action<SqlStorageOptions>? configure = null)
    {
        var options = await _fixture.CreateMigratedAsync(label, configure);
        var database = new SqlDatabase(options);
        var clock = new FixedClock { UtcNow = Now };

        var history = new SqlRunHistoryStore(
            database, options, NullLogger<SqlRunHistoryStore>.Instance);

        var cadence = Options.Create(new CadenceOptions
        {
            InstanceId = "janitor-test",
            Retention = retention ?? new RetentionOptions(),
        });

        var janitor = new CadenceJanitor(
            database, history, options, clock, cadence, NullLogger<CadenceJanitor>.Instance);

        return new Harness(database, history, janitor, options);
    }

    private sealed record Harness(
        SqlDatabase Database,
        SqlRunHistoryStore History,
        CadenceJanitor Janitor,
        SqlStorageOptions Options)
    {
        /// <summary>Writes a run in a chosen terminal state, bypassing the executor.</summary>
        public async Task<Guid> RecordAsync(
            string jobName,
            DateTimeOffset startedAt,
            RunStatus status,
            string instanceId = "janitor-test")
        {
            var runId = Guid.NewGuid();

            await History.StartAsync(
                new JobRunStart
                {
                    RunId = runId,
                    JobName = jobName,
                    Trigger = TriggerKind.Schedule,
                    InstanceId = instanceId,
                    StartedAt = startedAt,
                },
                default);

            if (status != RunStatus.Running)
            {
                await History.CompleteAsync(
                    runId,
                    new JobRunResult { Status = status, Duration = TimeSpan.Zero, CompletedAt = startedAt },
                    default);
            }

            return runId;
        }

        /// <summary>Writes an instance row with a chosen last-heartbeat instant.</summary>
        public async Task RegisterInstanceAsync(string instanceId, DateTimeOffset lastHeartbeat)
            => await Database.ExecuteAsync(
                $"""
                INSERT INTO {Database.Table("CadenceInstance")}
                    (InstanceId, MachineName, ProcessId, StartedAtUtc, LastHeartbeatUtc)
                VALUES (@InstanceId, 'test', 1, @Heartbeat, @Heartbeat);
                """,
                command =>
                {
                    SqlValues.AddText(command, "@InstanceId", instanceId, 200);
                    SqlValues.AddInstant(command, "@Heartbeat", lastHeartbeat);
                },
                default).ConfigureAwait(false);
    }
}
