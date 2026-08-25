using Cadence.Storage.Conformance;
using Cadence.Storage.Sql.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// One SQL Server container for the whole test assembly, with a fresh database per test.
/// </summary>
/// <remarks>
/// <para>
/// A container the tests own and drop, rather than a server that happens to be reachable. Starting
/// SQL Server takes long enough that one per test would be untenable, and isolating by database
/// instead is both faster and stricter than isolating by table prefix.
/// </para>
/// <para>
/// When no Docker daemon is reachable the fixture records why and every test that needs it skips,
/// so the rest of the suite stays runnable on a machine without Docker.
/// </para>
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>The SQL Server image the tests run against.</summary>
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    private MsSqlContainer? _container;
    private int _databaseCounter;

    /// <summary>Why the container is unavailable, or null when it started.</summary>
    public string? SkipReason { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (DockerDaemon.SkipReason is { } noDocker)
        {
            SkipReason = noDocker;
            return;
        }

        try
        {
            // The image is named explicitly rather than left to the library's default: which SQL
            // Server the tests ran against is part of what a green build means, and a floating
            // default can change under us between package versions.
            _container = new MsSqlBuilder(SqlServerImage).Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            // Recorded rather than thrown, so the suite skips instead of failing.
            SkipReason = $"A SQL Server container could not be started: {ex.Message}";
            _container = null;
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Skips the calling test when there is no container to talk to.</summary>
    public void RequireContainer() => Skip.If(SkipReason is not null, SkipReason ?? string.Empty);

    /// <summary>Creates an empty database and returns a connection string for it.</summary>
    /// <remarks>
    /// The name carries a counter rather than a random suffix, so a database left behind by a crash
    /// is traceable to the test that created it. Everything lives inside a container that is dropped
    /// at the end of the run regardless.
    /// </remarks>
    /// <param name="label">Short label identifying the caller, used in the database name.</param>
    public async Task<string> CreateDatabaseAsync(string label)
    {
        RequireContainer();

        var name = $"cadence_test_{Sanitise(label)}_{Interlocked.Increment(ref _databaseCounter)}";
        var master = _container!.GetConnectionString();

        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{name}];";
        await command.ExecuteNonQueryAsync();

        return new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>Creates an empty database with the Cadence schema already applied.</summary>
    /// <param name="label">Short label identifying the caller.</param>
    /// <param name="configure">Adjusts the options before the schema is created.</param>
    public async Task<SqlStorageOptions> CreateMigratedAsync(
        string label,
        Action<SqlStorageOptions>? configure = null)
    {
        var options = new SqlStorageOptions { ConnectionString = await CreateDatabaseAsync(label) };
        configure?.Invoke(options);
        options.Validate();

        await MigrateAsync(options);
        return options;
    }

    /// <summary>Applies the schema to a database.</summary>
    /// <param name="options">Options naming the database and schema.</param>
    public static Task MigrateAsync(SqlStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var migrator = new SqlMigrator(new SqlDatabase(options), options, NullLogger.Instance);
        return migrator.MigrateAsync(default);
    }

    private static string Sanitise(string label)
        => new([.. label.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

/// <summary>Shares one container across every test class in the assembly.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollectionDefinition : ICollectionFixture<SqlServerFixture>
{
    /// <summary>The collection name test classes opt into.</summary>
    public const string Name = "sql-server";
}
