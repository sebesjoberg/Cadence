using System.Reflection;
using System.Runtime.CompilerServices;
using Cadence.DependencyInjection;
using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Scheduling;
using Cadence.Storage;
using Cadence.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cadence;

/// <summary>Registers Cadence with the host's service collection.</summary>
public static class CadenceServiceCollectionExtensions
{
    // Namespaced because MapCadenceHealth selects purely by tag: a host's own database check tagged
    // "ready" would otherwise land on Cadence's readiness probe.
    private const string LiveTag = "cadence.live";
    private const string ReadyTag = "cadence.ready";

    /// <summary>
    /// Adds the scheduler. With no further configuration this gives code-defined schedules,
    /// in-memory run history, single-instance coordination and OpenTelemetry output — no external
    /// infrastructure at all.
    /// </summary>
    /// <remarks>
    /// Jobs carrying <see cref="ScheduledJobAttribute"/> in the calling assembly are registered
    /// automatically. Call <see cref="CadenceBuilder.AddJobsFrom"/> for jobs that live elsewhere.
    /// </remarks>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">
    /// Adds jobs, and replaces the in-memory defaults with a storage package. Anything registered
    /// here wins over the defaults.
    /// </param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddCadence(
        this IServiceCollection services,
        Action<CadenceBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return AddCadenceCore(services, configure, Assembly.GetCallingAssembly());
    }

    internal static IServiceCollection AddCadenceCore(
        IServiceCollection services,
        Action<CadenceBuilder>? configure,
        Assembly? scanAssembly)
    {
        var builder = new CadenceBuilder(services);

        // The callback runs first so that a storage package's registrations are in place before the
        // in-memory defaults are offered with TryAdd, and therefore win.
        configure?.Invoke(builder);

        if (scanAssembly is not null)
        {
            ScanSafely(builder, scanAssembly);
        }

        services.AddOptions<CadenceOptions>();
        services.AddMetrics();

        services.TryAddSingleton<CadenceReadiness>();

        var descriptors = builder.Jobs.ToList();

        // Neither check is given a store: replicas share one, so a store-honest readiness probe
        // would empty the service on every pod at once. Guarded because AddCheck appends
        // unconditionally and duplicate names throw, so a second AddCadence call would break.
        if (!services.Any(service => service.ImplementationType == typeof(CadenceHostedService)))
        {
            services.AddHealthChecks()
                .AddCheck<LivenessHealthCheck>("cadence-live", tags: [LiveTag])
                .AddTypeActivatedCheck<ReadinessHealthCheck>(
                    "cadence-ready", failureStatus: null, tags: [ReadyTag], args: descriptors.Count);
        }

        services.AddSingleton<IJobRegistry>(_ => new JobRegistry(descriptors));
        services.AddSingleton(new RegistrationDiagnostics(builder.Warnings));

        services.TryAddSingleton<ISystemClock, SystemClock>();
        services.TryAddSingleton<IScheduleSource, CodeScheduleSource>();
        services.TryAddSingleton<IRunHistoryStore>(_ => new InMemoryRunHistoryStore());
        services.TryAddSingleton<IJobResultStore>(_ => new InMemoryJobResultStore());
        services.TryAddSingleton<IOccurrenceCoordinator, NoOpOccurrenceCoordinator>();
        services.TryAddSingleton<IPauseStore, InMemoryPauseStore>();
        services.TryAddSingleton<IApiTokenStore, ConfiguredApiTokenStore>();
        services.TryAddSingleton<IInstanceDirectory, InMemoryInstanceDirectory>();
        services.TryAddSingleton<IJobProgressSink, RunHistoryProgressSink>();

        // Open generic first, then the exact-type passthrough. A job returning JobResult binds to
        // the second and is stored verbatim; everything else falls through to JSON.
        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(IJobResultSerializer<>), typeof(JsonJobResultSerializer<>)));
        services.TryAddSingleton<IJobResultSerializer<JobResult>, JobResultPassthroughSerializer>();

        services.TryAddSingleton<CadenceMetrics>();
        services.TryAddSingleton<LastSuccessCache>();
        services.TryAddSingleton<ScheduleResolver>();
        services.TryAddSingleton<ScheduleTicker>();
        services.TryAddSingleton<JobExecutor>();
        services.TryAddSingleton<JobGraphValidator>();
        services.TryAddSingleton<ShutdownBudgetProbe>();
        services.TryAddSingleton<IJobTrigger, JobTrigger>();

        services.AddHostedService<CadenceHostedService>();

        return services;
    }

    private static void ScanSafely(CadenceBuilder builder, Assembly assembly)
    {
        try
        {
            builder.AddJobsFrom(assembly);
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Scanning is a convenience. A half-loadable assembly should not stop an application
            // whose jobs are all registered explicitly.
            throw new CadenceStartupException(
                $"Could not scan '{assembly.GetName().Name}' for [ScheduledJob] types. Register the jobs " +
                "explicitly with AddJob<TJob>() if the assembly cannot be fully loaded.",
                ex);
        }
    }
}
