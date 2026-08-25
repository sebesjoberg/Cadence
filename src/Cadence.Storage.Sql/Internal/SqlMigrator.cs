using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Cadence.Storage.Sql.Internal;

/// <summary>
/// Applies the embedded schema scripts, once, in order.
/// </summary>
/// <remarks>
/// <para>
/// The scripts ship as embedded resources so there is no file to deploy alongside the assembly, and
/// are journalled by name in <c>CadenceSchemaVersion</c> so applying them twice is a no-op.
/// </para>
/// <para>
/// The whole run is wrapped in <c>sp_getapplock</c>, and that is the reason this is hand-written
/// rather than delegated to a script-runner library. Replicas deploy together and boot together, so
/// several instances reach this code within milliseconds of each other; without a lock they race on
/// <c>CREATE TABLE</c> and the losers fail startup. With one, the first migrates and the rest wait,
/// then find the journal already full. The lock is session-scoped, so it is released even if this
/// process is killed mid-migration.
/// </para>
/// </remarks>
internal sealed class SqlMigrator
{
    private const string LockResource = "Cadence.Schema";

    private readonly SqlDatabase _database;
    private readonly SqlStorageOptions _options;
    private readonly ILogger _logger;

    public SqlMigrator(SqlDatabase database, SqlStorageOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _logger = logger;
    }

    /// <summary>Brings the database up to the schema this assembly expects.</summary>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <exception cref="CadenceStartupException">
    /// The application lock could not be acquired within <see cref="SqlStorageOptions.MigrationTimeout"/>.
    /// </exception>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var scripts = LoadScripts();

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        await AcquireLockAsync(connection, cancellationToken).ConfigureAwait(false);

        try
        {
            var applied = 0;

            foreach (var (name, body) in scripts)
            {
                if (await IsAppliedAsync(connection, name, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                await ApplyAsync(connection, name, body, cancellationToken).ConfigureAwait(false);
                applied++;
            }

            if (applied == 0)
            {
                _logger.SchemaUpToDate(scripts.Count);
            }
            else
            {
                _logger.SchemaMigrated(applied, _database.Schema);
            }
        }
        finally
        {
            // Best effort: the lock is session-scoped, so closing the connection releases it anyway.
            // Releasing explicitly just frees it a moment earlier for the instances still waiting.
            await ReleaseLockAsync(connection).ConfigureAwait(false);
        }
    }

    /// <summary>The embedded scripts, ordered by name.</summary>
    /// <remarks>
    /// Ordinal ordering over a zero-padded numeric prefix, so 010 sorts after 009 rather than after
    /// 1. The convention is enforced by nothing but the file names; there are few enough of them
    /// that a review catches a mistake.
    /// </remarks>
    public static IReadOnlyList<(string Name, string Body)> LoadScripts()
    {
        var assembly = typeof(SqlMigrator).Assembly;
        const string prefix = "Cadence.Storage.Sql.Scripts.";

        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                     && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var scripts = new List<(string, string)>(names.Count);

        foreach (var resource in names)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded script '{resource}' could not be opened.");

            using var reader = new StreamReader(stream);
            scripts.Add((resource[prefix.Length..], reader.ReadToEnd()));
        }

        if (scripts.Count == 0)
        {
            throw new InvalidOperationException(
                "No schema scripts are embedded in Cadence.Storage.Sql. The package is built wrong: " +
                "Scripts/*.sql must be included as EmbeddedResource.");
        }

        return scripts;
    }

    private async Task AcquireLockAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = _database.Command(connection, "sp_getapplock");
        command.CommandType = CommandType.StoredProcedure;

        // The lock wait is its own timeout, and it can legitimately exceed CommandTimeout: waiting
        // for a peer to finish migrating is expected, not a fault. Add a margin so the command does
        // not time out before sp_getapplock gives its answer.
        var waitMilliseconds = (int)_options.MigrationTimeout.TotalMilliseconds;
        command.CommandTimeout = (int)_options.MigrationTimeout.TotalSeconds + 30;

        command.Parameters.AddWithValue("@Resource", LockResource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", waitMilliseconds);

        var result = command.Parameters.Add("@Result", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // 0 granted, 1 granted after waiting. Negative values are timeout, cancel, deadlock or a
        // parameter error, none of which are safe to proceed through.
        var code = (int)result.Value;

        if (code is not (0 or 1))
        {
            throw new CadenceStartupException(
                $"Could not acquire the Cadence schema lock (sp_getapplock returned {code}) within " +
                $"{_options.MigrationTimeout}. Another instance may be migrating; if none is, raise " +
                $"{nameof(SqlStorageOptions)}.{nameof(SqlStorageOptions.MigrationTimeout)} or apply " +
                "the scripts in scripts/sql by hand and set AutoMigrate to false.");
        }
    }

    private async Task ReleaseLockAsync(SqlConnection connection)
    {
        try
        {
            await using var command = _database.Command(connection, "sp_releaseapplock");
            command.CommandType = CommandType.StoredProcedure;
            command.Parameters.AddWithValue("@Resource", LockResource);
            command.Parameters.AddWithValue("@LockOwner", "Session");

            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            _logger.SchemaLockReleaseFailed(ex);
        }
    }

    private async Task<bool> IsAppliedAsync(
        SqlConnection connection,
        string scriptName,
        CancellationToken cancellationToken)
    {
        // The journal table is created by the first script, so on a fresh database it does not exist
        // yet. Ask the catalogue first rather than letting a missing-object error stand in for
        // "not applied" -- that would swallow a permissions problem too.
        var journal = _database.Table("CadenceSchemaVersion");

        await using var command = _database.Command(
            connection,
            $"""
            IF OBJECT_ID(@Journal, N'U') IS NULL
                SELECT CAST(0 AS BIT);
            ELSE
                SELECT CAST(CASE WHEN EXISTS (
                    SELECT 1 FROM {journal} WHERE ScriptName = @ScriptName
                ) THEN 1 ELSE 0 END AS BIT);
            """);

        SqlValues.AddText(command, "@Journal", journal, 400);
        SqlValues.AddText(command, "@ScriptName", scriptName, 200);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is bool applied && applied;
    }

    private async Task ApplyAsync(
        SqlConnection connection,
        string scriptName,
        string body,
        CancellationToken cancellationToken)
    {
        _logger.SchemaApplyingScript(scriptName);

        foreach (var batch in SplitBatches(body.Replace("{schema}", _database.Schema, StringComparison.Ordinal)))
        {
            await using var command = _database.Command(connection, batch);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var journal = _database.Command(
            connection,
            $"""
            INSERT INTO {_database.Table("CadenceSchemaVersion")} (ScriptName, AppliedAtUtc)
            VALUES (@ScriptName, @AppliedAtUtc);
            """);

        SqlValues.AddText(journal, "@ScriptName", scriptName, 200);
        SqlValues.AddInstant(journal, "@AppliedAtUtc", DateTimeOffset.UtcNow);

        await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a script on its GO separators.
    /// </summary>
    /// <remarks>
    /// GO is a client-side batch separator, not T-SQL, so the driver rejects a script containing it.
    /// The scripts need batches because <c>CREATE SCHEMA</c> must commit before later statements can
    /// reference it. This handles the form the scripts actually use — GO alone on a line — and
    /// nothing more; there is no attempt at a general parser, because a script that needed one would
    /// be a script that should be split into two files.
    /// </remarks>
    /// <param name="script">The script text.</param>
    public static IReadOnlyList<string> SplitBatches(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var batches = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMeaningful(batches, current);
                current.Clear();
                continue;
            }

            current.Append(line).Append('\n');
        }

        AddIfMeaningful(batches, current);
        return batches;
    }

    private static void AddIfMeaningful(List<string> batches, System.Text.StringBuilder current)
    {
        var text = current.ToString().Trim();

        if (text.Length > 0)
        {
            batches.Add(text);
        }
    }
}
