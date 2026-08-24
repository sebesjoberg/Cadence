using System.Reflection;
using Cadence.Scheduling;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cadence.DependencyInjection;

/// <summary>Configures Cadence during startup.</summary>
public sealed class CadenceBuilder
{
    private readonly Dictionary<string, JobDescriptor> _jobs = new(StringComparer.Ordinal);
    private readonly HashSet<Type> _explicitTypes = [];
    private readonly List<string> _warnings = [];

    internal CadenceBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection, for storage and dashboard packages to add their own services to.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Adjusts host-wide settings.</summary>
    /// <param name="configure">Receives the options.</param>
    /// <returns>This builder.</returns>
    public CadenceBuilder Configure(Action<CadenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>Registers a job, configured fluently.</summary>
    /// <typeparam name="TJob">The job type. Resolved from DI once per run.</typeparam>
    /// <param name="name">The job's stable, unique name. Kebab-case by convention.</param>
    /// <param name="configure">Configures schedule, policies and triggers.</param>
    /// <returns>This builder.</returns>
    public CadenceBuilder AddJob<TJob>(string name, Action<JobBuilder> configure)
        where TJob : class, IJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new JobBuilder(name, typeof(TJob));
        configure(builder);

        Register(builder.Build(), explicitlyAdded: true);
        return this;
    }

    /// <summary>Registers a job whose metadata comes from its <see cref="ScheduledJobAttribute"/>.</summary>
    /// <typeparam name="TJob">The job type, which must carry the attribute.</typeparam>
    /// <returns>This builder.</returns>
    public CadenceBuilder AddJob<TJob>()
        where TJob : class, IJob
    {
        var descriptor = DescriptorFromAttribute(typeof(TJob))
            ?? throw new CadenceStartupException(
                $"{typeof(TJob).Name} has no [ScheduledJob] attribute. Either add one, or register it " +
                "with the AddJob<TJob>(name, configure) overload.");

        Register(descriptor, explicitlyAdded: true);
        return this;
    }

    /// <summary>
    /// Registers every <see cref="IJob"/> in an assembly that carries a
    /// <see cref="ScheduledJobAttribute"/>. Types already registered explicitly are left alone.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>This builder.</returns>
    public CadenceBuilder AddJobsFrom(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass || !typeof(IJob).IsAssignableFrom(type))
            {
                continue;
            }

            if (_explicitTypes.Contains(type))
            {
                // An explicit registration is the more specific intent; scanning must not fight it.
                continue;
            }

            var descriptor = DescriptorFromAttribute(type);
            if (descriptor is not null)
            {
                Register(descriptor, explicitlyAdded: false);
            }
        }

        return this;
    }

    /// <summary>Registers every attributed job in the assembly containing a type.</summary>
    /// <typeparam name="TMarker">Any type in the assembly to scan.</typeparam>
    /// <returns>This builder.</returns>
    public CadenceBuilder AddJobsFromAssemblyOf<TMarker>() => AddJobsFrom(typeof(TMarker).Assembly);

    /// <summary>
    /// Replaces how occurrences are claimed.
    /// </summary>
    /// <remarks>
    /// This is the one seam that decides which instance runs a given slot, and it is deliberately
    /// the only thing in Cadence that knows how a claim is won — so a bespoke implementation
    /// (etcd, ZooKeeper, a table you already own, a Quartz-backed adapter) needs no changes
    /// anywhere else. The contract is narrow on purpose: return true if this instance may run the
    /// occurrence, false if someone else already holds it, and let genuine infrastructure failures
    /// throw rather than reporting them as false.
    /// </remarks>
    /// <typeparam name="TCoordinator">The implementation to use.</typeparam>
    /// <returns>This builder.</returns>
    public CadenceBuilder UseCoordinator<TCoordinator>()
        where TCoordinator : class, IOccurrenceCoordinator
        => ReplaceSingleton<IOccurrenceCoordinator, TCoordinator>();

    /// <summary>Replaces how occurrences are claimed, with an instance you construct.</summary>
    /// <param name="coordinator">The implementation to use.</param>
    /// <returns>This builder.</returns>
    public CadenceBuilder UseCoordinator(IOccurrenceCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        Services.Replace(ServiceDescriptor.Singleton(coordinator));
        return this;
    }

    /// <summary>Replaces where schedules are read from.</summary>
    /// <typeparam name="TSource">The implementation to use.</typeparam>
    /// <returns>This builder.</returns>
    public CadenceBuilder UseScheduleSource<TSource>()
        where TSource : class, IScheduleSource
        => ReplaceSingleton<IScheduleSource, TSource>();

    /// <summary>Replaces where run history is recorded.</summary>
    /// <typeparam name="TStore">The implementation to use.</typeparam>
    /// <returns>This builder.</returns>
    public CadenceBuilder UseRunHistory<TStore>()
        where TStore : class, IRunHistoryStore
        => ReplaceSingleton<IRunHistoryStore, TStore>();

    /// <summary>Replaces where job-reported progress is written.</summary>
    /// <typeparam name="TSink">The implementation to use.</typeparam>
    /// <returns>This builder.</returns>
    public CadenceBuilder UseProgressSink<TSink>()
        where TSink : class, IJobProgressSink
        => ReplaceSingleton<IJobProgressSink, TSink>();

    /// <summary>Replaces the clock. Intended for tests and for the test host.</summary>
    /// <param name="clock">The clock to use.</param>
    /// <returns>This builder.</returns>
    public CadenceBuilder UseClock(ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Services.Replace(ServiceDescriptor.Singleton(clock));
        return this;
    }

    private CadenceBuilder ReplaceSingleton<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        // Replace rather than Add: calling one of these twice should mean "use the last one", not
        // leave a shadowed registration behind for someone to trip over later.
        Services.Replace(ServiceDescriptor.Singleton<TService, TImplementation>());
        return this;
    }

    internal IReadOnlyCollection<JobDescriptor> Jobs => _jobs.Values;

    internal IReadOnlyList<string> Warnings => _warnings;

    private void Register(JobDescriptor descriptor, bool explicitlyAdded)
    {
        if (_jobs.TryGetValue(descriptor.Name, out var existing))
        {
            throw new CadenceStartupException(
                $"Two jobs are registered under the name '{descriptor.Name}': " +
                $"{existing.ImplementationType.FullName} and {descriptor.ImplementationType.FullName}. " +
                "Job names are the identity that stored configuration and history hang off, so they " +
                "must be unique.");
        }

        _jobs.Add(descriptor.Name, descriptor);

        if (explicitlyAdded)
        {
            _explicitTypes.Add(descriptor.ImplementationType);
        }

        var alreadyRegistered = Services.FirstOrDefault(sd => sd.ServiceType == descriptor.ImplementationType);

        if (alreadyRegistered is null)
        {
            // Transient by default: a job gets a fresh instance per run, in that run's own scope.
            Services.TryAddTransient(descriptor.ImplementationType);
        }
        else if (alreadyRegistered.Lifetime == ServiceLifetime.Singleton)
        {
            _warnings.Add(
                $"'{descriptor.Name}' ({descriptor.ImplementationType.Name}) is registered as a singleton. " +
                "A singleton job that takes a scoped dependency captures it for the lifetime of the " +
                "process — the classic captive-dependency bug. Prefer transient or scoped.");
        }
    }

    private static JobDescriptor? DescriptorFromAttribute(Type type)
    {
        var attribute = type.GetCustomAttribute<ScheduledJobAttribute>(inherit: false);
        if (attribute is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(attribute.Name))
        {
            throw new CadenceStartupException(
                $"[ScheduledJob] on {type.Name} has no Name. Job names are the stable identity and " +
                "cannot be derived from the type, because renaming the class must not orphan its " +
                "configuration and history.");
        }

        if (attribute.Triggers.HasFlag(TriggerKind.Schedule) && string.IsNullOrWhiteSpace(attribute.Cron))
        {
            throw new CadenceStartupException(
                $"[ScheduledJob] on {type.Name} allows the schedule trigger but sets no Cron. " +
                "Set Cron, or set Triggers to exclude Schedule.");
        }

        if (attribute.Cron is not null && !CronParser.TryParse(attribute.Cron, out _, out var cronError))
        {
            throw new CadenceStartupException($"[ScheduledJob] on {type.Name}: {cronError}");
        }

        if (!CronParser.TryResolveTimeZone(attribute.TimeZone, out var timeZone, out var zoneError))
        {
            throw new CadenceStartupException($"[ScheduledJob] on {type.Name}: {zoneError}");
        }

        TimeSpan? maxDuration = null;
        if (attribute.MaxDuration is { } text)
        {
            if (!TimeSpan.TryParse(text, out var parsed) || parsed <= TimeSpan.Zero)
            {
                throw new CadenceStartupException(
                    $"[ScheduledJob] on {type.Name} has MaxDuration '{text}', which is not a positive " +
                    "TimeSpan. Use a form like '00:10:00'.");
            }

            maxDuration = parsed;
        }

        return new JobDescriptor
        {
            Name = attribute.Name,
            ImplementationType = type,
            AllowedTriggers = attribute.Triggers,
            DefaultCron = attribute.Cron,
            DefaultTimeZone = timeZone!,
            DefaultEnabled = attribute.Enabled,
            Overlap = attribute.Overlap,
            OnMissed = attribute.OnMissed,
            MaxDuration = maxDuration,
        };
    }
}
