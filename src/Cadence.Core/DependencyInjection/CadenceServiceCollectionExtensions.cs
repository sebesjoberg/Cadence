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

        var descriptors = builder.Jobs.ToList();
        services.AddSingleton<IJobRegistry>(_ => new JobRegistry(descriptors));
        services.AddSingleton(new RegistrationDiagnostics(builder.Warnings));

        services.TryAddSingleton<ISystemClock, SystemClock>();
        services.TryAddSingleton<IScheduleSource, CodeScheduleSource>();
        services.TryAddSingleton<IRunHistoryStore>(_ => new InMemoryRunHistoryStore());
        services.TryAddSingleton<IOccurrenceCoordinator, NoOpOccurrenceCoordinator>();
        services.TryAddSingleton<IPauseStore, InMemoryPauseStore>();
        services.TryAddSingleton<IJobProgressSink, RunHistoryProgressSink>();

        services.TryAddSingleton<CadenceMetrics>();
        services.TryAddSingleton<LastSuccessCache>();
        services.TryAddSingleton<ScheduleResolver>();
        services.TryAddSingleton<ScheduleTicker>();
        services.TryAddSingleton<JobExecutor>();
        services.TryAddSingleton<JobGraphValidator>();
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
