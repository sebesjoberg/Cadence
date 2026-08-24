using Cadence.DependencyInjection;
using Cadence.Storage.Sql.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Storage.Sql;

/// <summary>Adds SQL Server persistence and clustering to Cadence.</summary>
public static class CadenceSqlStorageExtensions
{
    /// <summary>
    /// Moves schedules, run history and occurrence claiming into SQL Server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one documented step from "works on my machine" to "works in production", and it
    /// brings persistence and clustering together on purpose. Splitting them would let someone
    /// deploy two instances with persistent history and no coordinator, which runs every occurrence
    /// twice while looking correct in the logs.
    /// </para>
    /// <para>
    /// What changes: claims go through the unique index on <c>CadenceJobRun</c> so only one instance
    /// starts each occurrence; history survives restarts and is shared across instances; schedules
    /// come from a table that can be edited while the application runs; a heartbeat row makes this
    /// instance visible to the janitor, which purges old history and resolves runs nobody finished.
    /// </para>
    /// <para>
    /// What does not change: the guarantee is still that at most one instance <em>starts</em> a given
    /// occurrence, not that at most one run of a job is ever in flight. A run that overruns its slot
    /// can be joined by the next occurrence on another instance.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Cadence builder.</param>
    /// <param name="connectionString">Connection string for the Cadence database.</param>
    /// <param name="configure">Adjusts intervals, retention batching and schema handling.</param>
    /// <returns>The builder.</returns>
    public static CadenceBuilder UseSqlStorage(
        this CadenceBuilder builder,
        string connectionString,
        Action<SqlStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new SqlStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        options.Validate();

        var services = builder.Services;

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new SqlDatabase(sp.GetRequiredService<SqlStorageOptions>()));

        // Replace, not TryAdd: AddCadence offers its in-memory defaults with TryAdd after the
        // configuration callback has run, so whatever is registered here wins.
        services.Replace(ServiceDescriptor.Singleton<IOccurrenceCoordinator>(sp =>
            new SqlOccurrenceCoordinator(
                sp.GetRequiredService<SqlDatabase>(),
                sp.GetRequiredService<ISystemClock>(),
                sp.GetRequiredService<IOptions<CadenceOptions>>(),
                sp.GetRequiredService<ILogger<SqlOccurrenceCoordinator>>())));

        // Registered as the concrete type as well, because the janitor needs the maintenance
        // operations that are not on IRunHistoryStore -- per-job trimming and reaping. Both
        // registrations resolve the same singleton, so there is one log-flush buffer, not two.
        services.TryAddSingleton(sp => new SqlRunHistoryStore(
            sp.GetRequiredService<SqlDatabase>(),
            sp.GetRequiredService<SqlStorageOptions>(),
            sp.GetRequiredService<ILogger<SqlRunHistoryStore>>()));

        services.Replace(ServiceDescriptor.Singleton<IRunHistoryStore>(
            sp => sp.GetRequiredService<SqlRunHistoryStore>()));

        services.TryAddSingleton(sp => new SqlScheduleSource(
            sp.GetRequiredService<SqlDatabase>(),
            sp.GetRequiredService<SqlStorageOptions>(),
            sp.GetRequiredService<ISystemClock>(),
            sp.GetRequiredService<ILogger<SqlScheduleSource>>()));

        services.Replace(ServiceDescriptor.Singleton<IScheduleSource>(
            sp => sp.GetRequiredService<SqlScheduleSource>()));

        services.Replace(ServiceDescriptor.Singleton<IWritableScheduleSource>(
            sp => sp.GetRequiredService<SqlScheduleSource>()));

        // Registration order is start order for hosted services, and UseSqlStorage is called from
        // inside the AddCadence callback -- which runs before AddCadence registers the scheduler.
        // So the schema is in place before the first tick tries to claim anything.
        services.AddHostedService<SqlSchemaInitializer>();

        services.AddSingleton<SqlInstanceRegistry>(sp => new SqlInstanceRegistry(
            sp.GetRequiredService<SqlDatabase>(),
            sp.GetRequiredService<SqlStorageOptions>(),
            sp.GetRequiredService<ISystemClock>(),
            sp.GetRequiredService<IOptions<CadenceOptions>>(),
            sp.GetRequiredService<ILogger<SqlInstanceRegistry>>()));

        services.AddHostedService(sp => sp.GetRequiredService<SqlInstanceRegistry>());

        services.AddSingleton<CadenceJanitor>(sp => new CadenceJanitor(
            sp.GetRequiredService<SqlDatabase>(),
            sp.GetRequiredService<SqlRunHistoryStore>(),
            sp.GetRequiredService<SqlStorageOptions>(),
            sp.GetRequiredService<ISystemClock>(),
            sp.GetRequiredService<IOptions<CadenceOptions>>(),
            sp.GetRequiredService<ILogger<CadenceJanitor>>()));

        services.AddHostedService(sp => sp.GetRequiredService<CadenceJanitor>());

        return builder;
    }
}
