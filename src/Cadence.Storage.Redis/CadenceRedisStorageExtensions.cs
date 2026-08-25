using Cadence.DependencyInjection;
using Cadence.Storage.Redis.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Storage.Redis;

/// <summary>Adds Redis persistence and clustering to Cadence.</summary>
public static class CadenceRedisStorageExtensions
{
    /// <summary>
    /// Moves schedules, run history and occurrence claiming into Redis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An alternative to <c>UseSqlStorage</c>, not an addition to it. Both replace the same three
    /// services, so calling both means whichever ran last wins on some of them and not others —
    /// there is no configuration in which mixing the two is what someone meant.
    /// </para>
    /// <para>
    /// What changes: claims go through a key only one caller can create, so only one instance starts
    /// each occurrence; history survives restarts and is shared across instances; schedules come
    /// from a hash that can be edited while the application runs, with changes pushed to every
    /// instance; a heartbeat makes this instance visible to the janitor, which purges old history
    /// and resolves runs nobody finished.
    /// </para>
    /// <para>
    /// What does not change: the guarantee is still that at most one instance <em>starts</em> a
    /// given occurrence, not that at most one run of a job is ever in flight.
    /// </para>
    /// <para>
    /// <strong>Durability is Redis's, not a database's.</strong> With the default configuration a
    /// Redis restart can lose recent writes, and that includes claims — an occurrence whose claim
    /// vanished can be claimed again. Enable AOF with <c>appendfsync everysec</c> if history and
    /// claims matter as much as scheduling does, and read the tier's section in the README before
    /// choosing this over SQL Server.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Cadence builder.</param>
    /// <param name="connectionString">StackExchange.Redis configuration string.</param>
    /// <param name="configure">Adjusts intervals, key prefix and progress batching.</param>
    /// <returns>The builder.</returns>
    public static CadenceBuilder UseRedisStorage(
        this CadenceBuilder builder,
        string connectionString,
        Action<RedisStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new RedisStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        options.Validate();

        var services = builder.Services;

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new RedisConnection(sp.GetRequiredService<RedisStorageOptions>()));

        // Replace, not TryAdd: AddCadence offers its in-memory defaults with TryAdd after the
        // configuration callback has run, so whatever is registered here wins.
        services.Replace(ServiceDescriptor.Singleton<IOccurrenceCoordinator>(sp =>
            new RedisOccurrenceCoordinator(
                sp.GetRequiredService<RedisConnection>(),
                sp.GetRequiredService<ISystemClock>(),
                sp.GetRequiredService<IOptions<CadenceOptions>>())));

        services.TryAddSingleton(sp => new RedisRunHistoryStore(
            sp.GetRequiredService<RedisConnection>(),
            sp.GetRequiredService<RedisStorageOptions>(),
            sp.GetRequiredService<ILogger<RedisRunHistoryStore>>()));

        services.Replace(ServiceDescriptor.Singleton<IRunHistoryStore>(
            sp => sp.GetRequiredService<RedisRunHistoryStore>()));

        services.TryAddSingleton(sp => new RedisScheduleSource(
            sp.GetRequiredService<RedisConnection>(),
            sp.GetRequiredService<RedisStorageOptions>(),
            sp.GetRequiredService<ILogger<RedisScheduleSource>>()));

        services.Replace(ServiceDescriptor.Singleton<IScheduleSource>(
            sp => sp.GetRequiredService<RedisScheduleSource>()));

        services.Replace(ServiceDescriptor.Singleton<IWritableScheduleSource>(
            sp => sp.GetRequiredService<RedisScheduleSource>()));

        services.Replace(ServiceDescriptor.Singleton<IPauseStore>(sp => new RedisPauseStore(
            sp.GetRequiredService<RedisConnection>(),
            sp.GetRequiredService<ISystemClock>())));

        // No schema initialiser, and nothing to migrate. Redis creates a key when it is first
        // written, which removes the whole question the SQL tier answers with a migrator, an
        // application lock and a folder of reviewable scripts.
        services.AddSingleton(sp => new RedisInstanceRegistry(
            sp.GetRequiredService<RedisConnection>(),
            sp.GetRequiredService<RedisStorageOptions>(),
            sp.GetRequiredService<ISystemClock>(),
            sp.GetRequiredService<IOptions<CadenceOptions>>(),
            sp.GetRequiredService<ILogger<RedisInstanceRegistry>>()));

        services.AddHostedService(sp => sp.GetRequiredService<RedisInstanceRegistry>());

        services.TryAddSingleton<IStorageMaintenance>(sp =>
            new RedisStorageMaintenance(sp.GetRequiredService<RedisConnection>()));

        services.TryAddSingleton(sp =>
        {
            var redis = sp.GetRequiredService<RedisStorageOptions>();

            var janitor = new JanitorOptions
            {
                Interval = redis.JanitorInterval,
                BatchSize = redis.JanitorBatchSize,
                HeartbeatTimeout = redis.HeartbeatTimeout,
            };

            janitor.Validate();
            return janitor;
        });

        services.AddSingleton<CadenceJanitor>(sp => new CadenceJanitor(
            sp.GetRequiredService<IStorageMaintenance>(),
            sp.GetRequiredService<JanitorOptions>(),
            sp.GetRequiredService<ISystemClock>(),
            sp.GetRequiredService<IOptions<CadenceOptions>>(),
            sp.GetRequiredService<ILogger<CadenceJanitor>>()));

        services.AddHostedService(sp => sp.GetRequiredService<CadenceJanitor>());

        return builder;
    }
}
