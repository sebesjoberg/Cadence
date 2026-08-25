using System.Text.RegularExpressions;

namespace Cadence.Storage.Sql;

/// <summary>Settings for the SQL Server storage tier.</summary>
public sealed partial class SqlStorageOptions
{
    /// <summary>Connection string for the Cadence database.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Schema the Cadence tables live in. Substituted into DDL, so it is validated as a plain
    /// identifier rather than quoted — a schema name is not the kind of thing that arrives from
    /// user input, and refusing anything exotic is cheaper than pretending to escape it.
    /// </summary>
    public string SchemaName { get; set; } = "dbo";

    /// <summary>
    /// Whether to create or upgrade the schema at startup.
    /// </summary>
    /// <remarks>
    /// On by default, so adding one line to <c>AddCadence</c> works against an empty database. Turn
    /// it off where the application's principal has no DDL rights, or where schema changes go
    /// through a release process; the same scripts are in the repository under <c>scripts/sql</c>,
    /// and the migrator is a no-op once they have been applied.
    /// </remarks>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>How long any single command may run before it is cancelled.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the migrator waits for another instance to finish migrating.</summary>
    /// <remarks>
    /// Instances starting together all try to migrate; one wins the application lock and the rest
    /// wait here, then find nothing to do. Generous, because losing the race is normal and failing
    /// a deployment over it is not.
    /// </remarks>
    public TimeSpan MigrationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often this instance refreshes its heartbeat row.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How stale an instance's heartbeat must be before the janitor treats it as gone and marks its
    /// unfinished runs <see cref="RunStatus.Lost"/>.
    /// </summary>
    /// <remarks>
    /// Must be comfortably larger than <see cref="HeartbeatInterval"/>: a GC pause or a brief
    /// network blip should not get a live instance's runs reaped out from under it. The default is
    /// four intervals.
    /// </remarks>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the janitor purges history, trims per job and reaps abandoned runs.</summary>
    public TimeSpan JanitorInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many rows the janitor deletes or updates per statement, looping until a pass is done.
    /// </summary>
    /// <remarks>
    /// Batched rather than one big statement: a single delete of a large backlog takes enough row
    /// locks to escalate to a table lock, which blocks the claim insert — so the janitor would stall
    /// scheduling while tidying up after it.
    /// </remarks>
    public int JanitorBatchSize { get; set; } = 1000;

    /// <summary>
    /// How often the schedule source checks whether an external actor changed configuration.
    /// </summary>
    /// <remarks>
    /// One single-row read of the version table, not a re-read of every schedule, so this can be
    /// short without much cost. It bounds how long a dashboard edit takes to reach an instance.
    /// </remarks>
    public TimeSpan SchedulePollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many progress entries to buffer before flushing, and the ceiling on one flush batch.
    /// </summary>
    public int ProgressBatchSize { get; set; } = 100;

    /// <summary>
    /// How long a buffered progress entry may wait before being flushed even if the batch is not
    /// full.
    /// </summary>
    /// <remarks>
    /// Progress is buffered because <c>JobContext.Report</c> is called from job code at whatever
    /// rate the job likes; writing straight through lets a chatty loop hammer the database. The
    /// trade is latency: an entry can be this far behind before the dashboard sees it.
    /// </remarks>
    public TimeSpan ProgressFlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Validates the options and throws when a value cannot work.</summary>
    /// <exception cref="ArgumentException">The connection string or schema name is unusable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its supported range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException(
                "No connection string was supplied. Pass one to UseSqlStorage, or set " +
                $"{nameof(SqlStorageOptions)}.{nameof(ConnectionString)}.",
                nameof(ConnectionString));
        }

        if (!IdentifierPattern().IsMatch(SchemaName))
        {
            throw new ArgumentException(
                $"'{SchemaName}' is not usable as a schema name. It is substituted into DDL rather " +
                "than passed as a parameter, so it is restricted to letters, digits and underscores.",
                nameof(SchemaName));
        }

        RequirePositive(CommandTimeout, nameof(CommandTimeout));
        RequirePositive(MigrationTimeout, nameof(MigrationTimeout));
        RequirePositive(HeartbeatInterval, nameof(HeartbeatInterval));
        RequirePositive(HeartbeatTimeout, nameof(HeartbeatTimeout));
        RequirePositive(JanitorInterval, nameof(JanitorInterval));
        RequirePositive(SchedulePollInterval, nameof(SchedulePollInterval));
        RequirePositive(ProgressFlushInterval, nameof(ProgressFlushInterval));

        if (HeartbeatTimeout <= HeartbeatInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatTimeout),
                HeartbeatTimeout,
                $"The heartbeat timeout must be longer than {nameof(HeartbeatInterval)} " +
                $"({HeartbeatInterval}), and should be several times longer. Otherwise a single " +
                "missed beat lets the janitor mark a live instance's runs as lost.");
        }

        if (JanitorBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(JanitorBatchSize), JanitorBatchSize, "The janitor batch size must be at least one.");
        }

        if (ProgressBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProgressBatchSize), ProgressBatchSize, "The progress batch size must be at least one.");
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
