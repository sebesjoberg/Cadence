using Cadence.Storage.Sql.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// SQL-tier behaviour the shared conformance suite deliberately does not cover.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public SqlStorageTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_claim_writes_the_run_row_itself()
    {
        // Design plan 3.2: in SQL the claim *is* the run row, so there is no window where a slot is
        // taken but unrecorded. A process that dies right after claiming leaves something visible.
        var options = await _fixture.CreateMigratedAsync("claimrow");
        var runId = Guid.NewGuid();

        var coordinator = Coordinator(options, "instance-a");

        Assert.True(await coordinator.TryClaimAsync("job", Now, runId, default));

        await using var history = History(options);
        var run = await history.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(runId, run.RunId);
        Assert.Equal(Now, run.ScheduledFor);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal("instance-a", run.InstanceId);
        Assert.Equal(TriggerKind.Schedule, run.Trigger);
    }

    [SkippableFact]
    public async Task Starting_a_claimed_run_updates_the_claim_row_rather_than_inserting_a_second()
    {
        var options = await _fixture.CreateMigratedAsync("startclaimed");
        var runId = Guid.NewGuid();

        Assert.True(await Coordinator(options, "instance-a").TryClaimAsync("job", Now, runId, default));

        await using var history = History(options);

        await history.StartAsync(
            new JobRunStart
            {
                RunId = runId,
                JobName = "job",
                ScheduledFor = Now,
                Trigger = TriggerKind.Schedule,
                InstanceId = "instance-a",
                StartedAt = Now.AddSeconds(1),
            },
            default);

        var runs = await history.QueryAsync(new RunQuery { JobName = "job" }, default);

        // One row, not two. A second insert would violate the occurrence index and break every
        // scheduled run.
        var run = Assert.Single(runs);
        Assert.Equal(Now.AddSeconds(1), run.StartedAt);
    }

    [SkippableFact]
    public async Task An_unclaimed_run_is_inserted_by_the_history_store()
    {
        // Manual and API triggers never claim, and neither does a scheduled run under a coordinator
        // that does not write rows. Both still have to end up in history.
        var options = await _fixture.CreateMigratedAsync("unclaimed");
        await using var history = History(options);

        var runId = Guid.NewGuid();

        await history.StartAsync(
            new JobRunStart
            {
                RunId = runId,
                JobName = "job",
                Trigger = TriggerKind.Manual,
                InstanceId = "instance-a",
                StartedAt = Now,
            },
            default);

        var run = await history.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(runId, run.RunId);
        Assert.Null(run.ScheduledFor);
        Assert.Equal(TriggerKind.Manual, run.Trigger);
    }

    [SkippableFact]
    public async Task Triggered_runs_are_exempt_from_the_occurrence_index()
    {
        // The index is filtered on ScheduledForUtc IS NOT NULL precisely so that any number of
        // manual runs of one job can coexist. Without the filter, the second would be rejected.
        var options = await _fixture.CreateMigratedAsync("exempt");
        await using var history = History(options);

        for (var i = 0; i < 3; i++)
        {
            await history.StartAsync(
                new JobRunStart
                {
                    RunId = Guid.NewGuid(),
                    JobName = "job",
                    Trigger = TriggerKind.Manual,
                    InstanceId = "instance-a",
                    StartedAt = Now.AddSeconds(i),
                },
                default);
        }

        Assert.Equal(3, (await history.QueryAsync(new RunQuery { JobName = "job" }, default)).Count);
    }

    [SkippableFact]
    public async Task An_unreachable_database_throws_out_of_a_claim_rather_than_returning_false()
    {
        /*
            The most important negative test in the package.

            Returning false here would mean "another instance won", and the tick loop would move on.
            Nothing would run, nothing would be recorded, and nobody would be told -- the worst
            failure a scheduler can have, because it is invisible. So only 2601 and 2627 may ever
            become false, and everything else has to propagate.
        */
        Skip.If(Docker.SkipReason is not null, Docker.SkipReason ?? string.Empty);

        var options = new SqlStorageOptions
        {
            ConnectionString = "Server=127.0.0.1,14333;Database=nope;User Id=sa;Password=nope;"
                             + "TrustServerCertificate=true;Connect Timeout=2;",
        };

        options.Validate();

        await Assert.ThrowsAnyAsync<SqlException>(
            () => Coordinator(options, "instance-a").TryClaimAsync("job", Now, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task A_missing_table_throws_out_of_a_claim_rather_than_returning_false()
    {
        // Same rule as an unreachable server, and easier to hit by accident: someone points Cadence
        // at a database where the schema was never applied.
        var options = new SqlStorageOptions
        {
            ConnectionString = await _fixture.CreateDatabaseAsync("noschema"),
            AutoMigrate = false,
        };

        options.Validate();

        await Assert.ThrowsAnyAsync<SqlException>(
            () => Coordinator(options, "instance-a").TryClaimAsync("job", Now, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task Progress_entries_are_written_in_batches()
    {
        var options = await _fixture.CreateMigratedAsync("batch", o => o.ProgressBatchSize = 10);
        await using var history = History(options);

        var runId = Guid.NewGuid();

        await history.StartAsync(
            new JobRunStart
            {
                RunId = runId,
                JobName = "chatty",
                Trigger = TriggerKind.Manual,
                InstanceId = "instance-a",
                StartedAt = Now,
            },
            default);

        for (var i = 0; i < 250; i++)
        {
            await history.AppendLogAsync(
                runId,
                new JobLogEntry { Timestamp = Now.AddMilliseconds(i), Message = $"step {i}" },
                default);
        }

        await history.FlushProgressAsync(default);

        var run = await history.GetLastRunAsync("chatty", default);

        Assert.NotNull(run);
        Assert.Equal(250, run.Log.Count);

        // Order is preserved across batch boundaries, which is what makes a progress log readable.
        Assert.Equal("step 0", run.Log[0].Message);
        Assert.Equal("step 249", run.Log[^1].Message);
    }

    [SkippableFact]
    public async Task A_progress_message_longer_than_the_column_is_truncated_not_dropped()
    {
        var options = await _fixture.CreateMigratedAsync("longmsg");
        await using var history = History(options);

        var runId = Guid.NewGuid();

        await history.StartAsync(
            new JobRunStart
            {
                RunId = runId,
                JobName = "job",
                Trigger = TriggerKind.Manual,
                InstanceId = "instance-a",
                StartedAt = Now,
            },
            default);

        await history.AppendLogAsync(
            runId,
            new JobLogEntry { Timestamp = Now, Message = new string('x', 5000) },
            default);

        await history.FlushProgressAsync(default);

        var run = await history.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        var entry = Assert.Single(run.Log);
        Assert.Equal(2000, entry.Message.Length);
        Assert.EndsWith("...", entry.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Structured_progress_data_round_trips()
    {
        var options = await _fixture.CreateMigratedAsync("data");
        await using var history = History(options);

        var runId = Guid.NewGuid();

        await history.StartAsync(
            new JobRunStart
            {
                RunId = runId,
                JobName = "job",
                Trigger = TriggerKind.Manual,
                InstanceId = "instance-a",
                StartedAt = Now,
            },
            default);

        await history.AppendLogAsync(
            runId,
            new JobLogEntry
            {
                Timestamp = Now,
                Message = "processed",
                Data = new Dictionary<string, object?> { ["invoices"] = 42, ["source"] = "erp" },
            },
            default);

        await history.FlushProgressAsync(default);

        var run = await history.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        var entry = Assert.Single(run.Log);
        Assert.NotNull(entry.Data);
        Assert.Equal("erp", entry.Data["source"]?.ToString());
    }

    [SkippableFact]
    public async Task The_registry_records_and_refreshes_a_heartbeat()
    {
        var options = await _fixture.CreateMigratedAsync("heartbeat");
        var database = new SqlDatabase(options);
        var clock = new FixedClock { UtcNow = Now };

        var registry = new SqlInstanceRegistry(
            database,
            options,
            clock,
            Options.Create(new CadenceOptions { InstanceId = "beating" }),
            NullLogger<SqlInstanceRegistry>.Instance);

        await registry.BeatAsync(register: true, default);

        var first = await ReadHeartbeatAsync(database, "beating");
        Assert.Equal(Now, first);

        clock.Advance(TimeSpan.FromMinutes(1));
        await registry.BeatAsync(register: false, default);

        var second = await ReadHeartbeatAsync(database, "beating");
        Assert.Equal(Now.AddMinutes(1), second);

        // One row throughout: the second beat updates rather than inserting a duplicate.
        var rows = await database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {database.Table("CadenceInstance")};", bind: null, default);

        Assert.Equal(1, rows);
    }

    [SkippableFact]
    public async Task Instants_round_trip_as_utc_whatever_offset_they_arrive_with()
    {
        // Everything is stored as UTC DATETIME2. An instant handed in with an offset has to come back
        // as the same instant, or the occurrence index would compare wall-clock times across
        // instances in different zones.
        var options = await _fixture.CreateMigratedAsync("offsets");
        await using var history = History(options);

        var offset = new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.FromHours(2));

        await history.StartAsync(
            new JobRunStart
            {
                RunId = Guid.NewGuid(),
                JobName = "job",
                ScheduledFor = offset,
                Trigger = TriggerKind.Schedule,
                InstanceId = "instance-a",
                StartedAt = offset,
            },
            default);

        var run = await history.GetLastRunAsync("job", default);

        Assert.NotNull(run);
        Assert.Equal(offset, run.ScheduledFor);
        Assert.Equal(TimeSpan.Zero, run.ScheduledFor!.Value.Offset);
        Assert.Equal(11, run.ScheduledFor!.Value.Hour);
    }

    [SkippableFact]
    public async Task Two_instances_in_different_zones_contend_for_the_same_instant()
    {
        // The consequence of the test above, stated as behaviour: the same instant expressed with
        // two different offsets is one occurrence, not two.
        var options = await _fixture.CreateMigratedAsync("zones");

        var utc = new DateTimeOffset(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);
        var sameInstantElsewhere = new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.FromHours(2));

        Assert.True(await Coordinator(options, "a").TryClaimAsync("job", utc, Guid.NewGuid(), default));
        Assert.False(await Coordinator(options, "b")
            .TryClaimAsync("job", sameInstantElsewhere, Guid.NewGuid(), default));
    }

    [Fact]
    public void Options_reject_a_schema_name_that_is_not_an_identifier()
    {
        // The schema name is substituted into DDL rather than parameterised, so anything exotic is
        // refused outright rather than escaped and hoped for.
        var options = new SqlStorageOptions { ConnectionString = "Server=.;", SchemaName = "dbo];DROP" };

        var error = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(SqlStorageOptions.SchemaName), error.ParamName);
    }

    [Fact]
    public void Options_reject_a_heartbeat_timeout_that_is_not_longer_than_the_interval()
    {
        // Otherwise a single missed beat lets the janitor mark a live instance's runs as lost.
        var options = new SqlStorageOptions
        {
            ConnectionString = "Server=.;",
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
        };

        var error = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(SqlStorageOptions.HeartbeatTimeout), error.ParamName);
    }

    [Fact]
    public void Options_reject_a_missing_connection_string()
    {
        var error = Assert.Throws<ArgumentException>(new SqlStorageOptions().Validate);
        Assert.Equal(nameof(SqlStorageOptions.ConnectionString), error.ParamName);
    }

    [Fact]
    public void Options_accept_the_defaults()
    {
        new SqlStorageOptions { ConnectionString = "Server=.;Database=cadence;" }.Validate();
    }

    private static SqlOccurrenceCoordinator Coordinator(SqlStorageOptions options, string instanceId)
        => new(
            new SqlDatabase(options),
            new FixedClock { UtcNow = Now },
            Options.Create(new CadenceOptions { InstanceId = instanceId }),
            NullLogger<SqlOccurrenceCoordinator>.Instance);

    private static SqlRunHistoryStore History(SqlStorageOptions options)
        => new(new SqlDatabase(options), options, NullLogger<SqlRunHistoryStore>.Instance);

    private static async Task<DateTimeOffset> ReadHeartbeatAsync(SqlDatabase database, string instanceId)
    {
        var values = await database.QueryAsync(
            $"SELECT LastHeartbeatUtc FROM {database.Table("CadenceInstance")} WHERE InstanceId = @Id;",
            command => SqlValues.AddText(command, "@Id", instanceId, 200),
            reader => SqlValues.GetInstant(reader, 0),
            default);

        return Assert.Single(values);
    }
}
