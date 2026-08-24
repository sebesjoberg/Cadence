using Cadence.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Core.Tests;

public class JobGraphValidatorTests
{
    [Fact]
    public async Task AResolvableJobPasses()
    {
        var validator = Build(StartupValidation.ThrowOnStartup, registerDependencies: true);

        var disabled = await validator.ValidateAsync(CancellationToken.None);

        Assert.Empty(disabled);
    }

    [Fact]
    public async Task AnUnresolvableJobTakesTheHostDownByDefault()
    {
        var validator = Build(StartupValidation.ThrowOnStartup, registerDependencies: false);

        var exception = await Assert.ThrowsAsync<CadenceStartupException>(
            () => validator.ValidateAsync(CancellationToken.None));

        // The message has to name the job and the type, because this is read at deploy time by
        // someone who has no other context.
        Assert.Contains("unresolvable", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(UnresolvableJob), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisableFailingJobsStartsTheHostAndReportsWhichJobsAreOut()
    {
        var validator = Build(StartupValidation.DisableFailingJobs, registerDependencies: false);

        var disabled = await validator.ValidateAsync(CancellationToken.None);

        Assert.Equal("unresolvable", Assert.Single(disabled));
    }

    [Fact]
    public async Task WarnOnlyLeavesTheJobScheduled()
    {
        var validator = Build(StartupValidation.WarnOnly, registerDependencies: false);

        var disabled = await validator.ValidateAsync(CancellationToken.None);

        Assert.Empty(disabled);
    }

    private static JobGraphValidator Build(StartupValidation behaviour, bool registerDependencies)
    {
        var services = new ServiceCollection();
        services.AddTransient<UnresolvableJob>();

        if (registerDependencies)
        {
            services.AddTransient<IDisposable>(_ => new MemoryStream());
        }

        var provider = services.BuildServiceProvider();

        var registry = new JobRegistry(
        [
            new JobDescriptor
            {
                Name = "unresolvable",
                ImplementationType = typeof(UnresolvableJob),
                DefaultCron = "* * * * *",
            },
        ]);

        return new JobGraphValidator(
            registry,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new RegistrationDiagnostics([]),
            Options.Create(new CadenceOptions { Validation = behaviour }),
            NullLogger<JobGraphValidator>.Instance);
    }
}
