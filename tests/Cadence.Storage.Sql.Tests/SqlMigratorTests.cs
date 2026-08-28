using Cadence.Storage.Conformance;
using Cadence.Storage.Sql.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// The migrator, against a real database.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlMigratorTests
{
    private readonly SqlServerFixture _fixture;

    public SqlMigratorTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task EveryTableAndTheClaimIndexExistAfterMigrating()
    {
        var options = await _fixture.CreateMigratedAsync("migrate");
        var database = new SqlDatabase(options);

        foreach (var table in new[]
                 {
                     "CadenceSchemaVersion", "CadenceJobRun", "CadenceJobRunLog",
                     "CadenceJobSchedule", "CadenceScheduleVersion", "CadenceInstance",
                 })
        {
            var exists = await database.ScalarAsync<int>(
                "SELECT CASE WHEN OBJECT_ID(@Name, N'U') IS NULL THEN 0 ELSE 1 END;",
                command => SqlValues.AddText(command, "@Name", database.Table(table), 400),
                default);

            Assert.Equal(1, exists);
        }

        // The filtered unique index is the clustering guarantee. A migration that created the table
        // but not this index would leave a cluster running every occurrence on every instance, and
        // nothing else would complain.
        var index = await database.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sys.indexes
            WHERE name = N'UX_CadenceJobRun_Occurrence' AND is_unique = 1 AND has_filter = 1;
            """,
            bind: null,
            default);

        Assert.Equal(1, index);
    }

    [SkippableFact]
    public async Task TheIdentityScriptIsAppliedAndJournalled()
    {
        var options = await _fixture.CreateMigratedAsync("migrateidentity");
        var database = new SqlDatabase(options);

        var applied = await database.ScalarAsync<int>(
            $"""
            SELECT COUNT(*) FROM {database.Table("CadenceSchemaVersion")}
            WHERE ScriptName LIKE '003%';
            """,
            bind: null,
            default);

        Assert.Equal(1, applied);

        foreach (var table in new[] { "CadenceApiToken", "CadenceDataProtectionKey" })
        {
            var rows = await database.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM {database.Table(table)};", bind: null, default);

            Assert.Equal(0, rows);
        }

        // The unique digest index is what makes resolution one seek; a table without it would pass
        // every conformance test and scan on every authenticated request.
        var index = await database.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sys.indexes
            WHERE name = N'UX_CadenceApiToken_Digest' AND is_unique = 1;
            """,
            bind: null,
            default);

        Assert.Equal(1, index);
    }

    [SkippableFact]
    public async Task MigratingTwiceChangesNothing()
    {
        var options = await _fixture.CreateMigratedAsync("twice");

        await SqlServerFixture.MigrateAsync(options);

        var applied = await new SqlDatabase(options).ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {new SqlDatabase(options).Table("CadenceSchemaVersion")};",
            bind: null,
            default);

        Assert.Equal(SqlMigrator.LoadScripts().Count, applied);
    }

    [SkippableFact]
    public async Task ConcurrentMigratorsSerialiseAndAllSucceed()
    {
        // What a rolling deployment does: replicas start together and all reach the migrator within
        // milliseconds. Without the application lock they race on CREATE TABLE and the losers fail
        // startup, which looks like a broken deployment rather than a lost race.
        var connectionString = await _fixture.CreateDatabaseAsync("race");

        var migrations = Enumerable.Range(0, 6).Select(_ =>
        {
            var options = new SqlStorageOptions { ConnectionString = connectionString };
            options.Validate();

            return new SqlMigrator(new SqlDatabase(options), options, NullLogger.Instance)
                .MigrateAsync(default);
        }).ToArray();

        await Task.WhenAll(migrations);

        var database = new SqlDatabase(new SqlStorageOptions { ConnectionString = connectionString });

        var applied = await database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {database.Table("CadenceSchemaVersion")};", bind: null, default);

        // Journalled exactly once despite six migrators, so none of them applied a script twice.
        Assert.Equal(SqlMigrator.LoadScripts().Count, applied);
    }

    [SkippableFact]
    public async Task ACustomSchemaIsCreatedAndUsed()
    {
        var options = await _fixture.CreateMigratedAsync("custom", o => o.SchemaName = "cadence");
        var database = new SqlDatabase(options);

        var exists = await database.ScalarAsync<int>(
            "SELECT CASE WHEN OBJECT_ID(@Name, N'U') IS NULL THEN 0 ELSE 1 END;",
            command => SqlValues.AddText(command, "@Name", database.Table("CadenceJobRun"), 400),
            default);

        Assert.Equal(1, exists);
        Assert.Equal("cadence", database.Schema);
    }

    [SkippableFact]
    public async Task ApplyingTheScriptByHandFirstIsNotAnError()
    {
        // The AutoMigrate = false path: a DBA runs scripts/sql, then the application starts with
        // migration on anyway. Every statement in the script is guarded, so this has to be a no-op.
        var connectionString = await _fixture.CreateDatabaseAsync("byhand");
        var options = new SqlStorageOptions { ConnectionString = connectionString };
        options.Validate();

        var database = new SqlDatabase(options);
        var (_, body) = SqlMigrator.LoadScripts()[0];

        await using var connection = await database.OpenAsync(default);

        foreach (var batch in SqlMigrator.SplitBatches(
                     body.Replace("{schema}", options.SchemaName, StringComparison.Ordinal)))
        {
            await using var command = database.Command(connection, batch);
            await command.ExecuteNonQueryAsync();
        }

        // Nothing is journalled yet, so the migrator runs the script again over its own output.
        await SqlServerFixture.MigrateAsync(options);

        var applied = await database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {database.Table("CadenceSchemaVersion")};", bind: null, default);

        Assert.Equal(SqlMigrator.LoadScripts().Count, applied);
    }

    [Fact]
    public void BatchesSplitOnALoneGOLine()
    {
        var batches = SqlMigrator.SplitBatches("SELECT 1;\nGO\nSELECT 2;\ngo\nSELECT 3;");

        Assert.Equal(3, batches.Count);
        Assert.Equal("SELECT 1;", batches[0]);
        Assert.Equal("SELECT 2;", batches[1]);
        Assert.Equal("SELECT 3;", batches[2]);
    }

    [Fact]
    public void GOInsideAStatementIsNotASeparator()
    {
        // Only a line that is nothing but GO separates batches, so an identifier or string containing
        // those letters is left alone.
        var batches = SqlMigrator.SplitBatches("SELECT 'GOING';\nSELECT Category FROM T;");

        Assert.Single(batches);
    }

    [Fact]
    public void EmptyBatchesAreDropped()
    {
        Assert.Empty(SqlMigrator.SplitBatches("GO\n\nGO\n   \nGO"));
    }

    [SkippableFact]
    public async Task AnUnreachableDatabaseFailsLoudlyRatherThanQuietly()
    {
        Skip.If(DockerDaemon.SkipReason is not null, DockerDaemon.SkipReason ?? string.Empty);

        var options = new SqlStorageOptions
        {
            // A port nothing listens on, and a short timeout so the test does not sit here.
            ConnectionString = "Server=127.0.0.1,14333;Database=nope;User Id=sa;Password=nope;"
                             + "TrustServerCertificate=true;Connect Timeout=3;",
        };

        options.Validate();

        await Assert.ThrowsAnyAsync<SqlException>(() => SqlServerFixture.MigrateAsync(options));
    }
}
