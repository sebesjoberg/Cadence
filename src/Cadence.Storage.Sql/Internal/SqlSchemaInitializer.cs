using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cadence.Storage.Sql.Internal;

/// <summary>
/// Brings the schema up to date before the scheduler starts.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IHostedService"/> rather than a <see cref="BackgroundService"/>, and the work happens
/// in <see cref="StartAsync"/>: the host starts hosted services one at a time, in registration order,
/// and waits for each. Because <c>UseSqlStorage</c> is called from inside the <c>AddCadence</c>
/// callback, this is registered before the scheduler's own hosted service — so the tables exist
/// before the first tick tries to claim anything.
/// </para>
/// <para>
/// A migration failure is allowed to stop the host. That looks harsh next to the rule that a store
/// blip must never fail boot, but the two cases are different: a transient read failure means "carry
/// on with what you have", while a schema that could not be created means every claim and every
/// history write will fail from now on. Failing at deploy time is the kinder outcome.
/// </para>
/// </remarks>
internal sealed class SqlSchemaInitializer : IHostedService
{
    private readonly SqlDatabase _database;
    private readonly SqlStorageOptions _options;
    private readonly ILogger<SqlSchemaInitializer> _logger;

    public SqlSchemaInitializer(
        SqlDatabase database,
        SqlStorageOptions options,
        ILogger<SqlSchemaInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoMigrate)
        {
            _logger.SchemaMigrationSkipped(_options.SchemaName);
            return;
        }

        var migrator = new SqlMigrator(_database, _options, _logger);
        await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
