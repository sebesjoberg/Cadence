using Cadence.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Core.Tests;

public class RegistrationTests
{
    [Fact]
    public void Attribute_metadata_becomes_the_descriptor()
    {
        var registry = BuildRegistry(cadence => cadence.AddJob<AttributedJob>());

        Assert.True(registry.TryGet("attributed-job", out var descriptor));
        Assert.Equal(typeof(AttributedJob), descriptor!.ImplementationType);
        Assert.Equal("0 */15 * * * *", descriptor.DefaultCron);
        Assert.Equal("Europe/Stockholm", descriptor.DefaultTimeZone.Id);
        Assert.Equal(OverlapPolicy.AllowConcurrent, descriptor.Overlap);
        Assert.Equal(TimeSpan.FromMinutes(10), descriptor.MaxDuration);
        Assert.Equal(TriggerKind.Schedule | TriggerKind.Api, descriptor.AllowedTriggers);
    }

    [Fact]
    public void Fluent_registration_produces_the_same_shape()
    {
        var registry = BuildRegistry(cadence => cadence.AddJob<SucceedingJob>(
            "invoice-sync",
            job => job
                .Cron("0 */15 * * * *", Occurrences.Stockholm)
                .Overlap(OverlapPolicy.Skip)
                .MaxDuration(TimeSpan.FromMinutes(10))
                .OnMissed(MissedRunPolicy.RunOnce)));

        Assert.True(registry.TryGet("invoice-sync", out var descriptor));
        Assert.Equal(MissedRunPolicy.RunOnce, descriptor!.OnMissed);
        Assert.True(descriptor.IsScheduled);
    }

    [Fact]
    public void An_invalid_cron_literal_fails_at_registration_not_at_the_first_tick()
    {
        var exception = Assert.Throws<CadenceStartupException>(() => BuildRegistry(
            cadence => cadence.AddJob<SucceedingJob>("bad", job => job.Cron("not a cron"))));

        Assert.Contains("bad", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scheduled_job_with_no_cron_is_rejected_with_a_hint()
    {
        var exception = Assert.Throws<CadenceStartupException>(() => BuildRegistry(
            cadence => cadence.AddJob<SucceedingJob>("no-cron", _ => { })));

        Assert.Contains("ApiOnly()", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiOnly_registers_a_job_with_no_schedule()
    {
        var registry = BuildRegistry(cadence => cadence.AddJob<SucceedingJob>(
            "rebuild-search-index",
            job => job.ApiOnly().MaxDuration(TimeSpan.FromHours(2))));

        Assert.True(registry.TryGet("rebuild-search-index", out var descriptor));
        Assert.False(descriptor!.IsScheduled);
        Assert.Null(descriptor.DefaultCron);
        Assert.True(descriptor.AllowedTriggers.HasFlag(TriggerKind.Api));
    }

    [Fact]
    public void Two_jobs_with_the_same_name_fail_at_registration()
    {
        var exception = Assert.Throws<CadenceStartupException>(() => BuildRegistry(cadence => cadence
            .AddJob<SucceedingJob>("clash", job => job.Cron("* * * * *"))
            .AddJob<FailingJob>("clash", job => job.Cron("* * * * *"))));

        Assert.Contains("must be unique", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_positive_maximum_duration_is_rejected()
    {
        Assert.Throws<CadenceStartupException>(() => BuildRegistry(
            cadence => cadence.AddJob<SucceedingJob>(
                "zero", job => job.Cron("* * * * *").MaxDuration(TimeSpan.Zero))));
    }

    [Fact]
    public void Jobs_are_registered_as_transient_by_default()
    {
        var services = new ServiceCollection();
        CadenceServiceCollectionExtensions.AddCadenceCore(
            services,
            cadence => cadence.AddJob<SucceedingJob>("t", job => job.Cron("* * * * *")),
            scanAssembly: null);

        var registration = services.Single(sd => sd.ServiceType == typeof(SucceedingJob));
        Assert.Equal(ServiceLifetime.Transient, registration.Lifetime);
    }

    [Fact]
    public void A_singleton_job_registration_is_warned_about_rather_than_rejected()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SucceedingJob>();

        CadenceServiceCollectionExtensions.AddCadenceCore(
            services,
            cadence => cadence.AddJob<SucceedingJob>("captive", job => job.Cron("* * * * *")),
            scanAssembly: null);

        var provider = services.BuildServiceProvider();
        var diagnostics = provider.GetRequiredService<RegistrationDiagnostics>();

        var warning = Assert.Single(diagnostics.Warnings);
        Assert.Contains("captive-dependency", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Assembly_scanning_picks_up_attributed_jobs()
    {
        var registry = BuildRegistry(cadence => cadence.AddJobsFromAssemblyOf<AttributedJob>());

        Assert.True(registry.TryGet("attributed-job", out _));
    }

    [Fact]
    public void Explicit_registration_wins_over_scanning_rather_than_colliding()
    {
        // Both paths see AttributedJob. Registering it explicitly first must not then trip the
        // duplicate-name check when the scanner reaches it.
        var registry = BuildRegistry(cadence => cadence
            .AddJob<AttributedJob>()
            .AddJobsFromAssemblyOf<AttributedJob>());

        Assert.Single(registry.All, d => d.ImplementationType == typeof(AttributedJob));
    }

    private static IJobRegistry BuildRegistry(Action<CadenceBuilder> configure)
    {
        var services = new ServiceCollection();

        CadenceServiceCollectionExtensions.AddCadenceCore(services, configure, scanAssembly: null);

        return services.BuildServiceProvider().GetRequiredService<IJobRegistry>();
    }
}
