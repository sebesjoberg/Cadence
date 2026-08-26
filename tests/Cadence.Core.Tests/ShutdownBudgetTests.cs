using Cadence.Validation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Core.Tests;

public class ShutdownBudgetTests
{
    [Fact]
    public void ADrainTooShortForTheLongestJobIsReported()
    {
        var problems = ShutdownBudget.Check(
            hostShutdownTimeout: TimeSpan.FromMinutes(11),
            shutdownDrainTimeout: TimeSpan.FromSeconds(30),
            jobs: [Job("nightly-invoices", TimeSpan.FromMinutes(10))]);

        var problem = Assert.Single(problems);

        // Read at deploy time by someone with no other context, so it has to name the property to
        // change, the job that forced the bound, and both numbers.
        Assert.Contains(nameof(CadenceOptions.ShutdownDrainTimeout), problem, StringComparison.Ordinal);
        Assert.Contains("nightly-invoices", problem, StringComparison.Ordinal);
        Assert.Contains("00:00:30", problem, StringComparison.Ordinal);
        Assert.Contains("00:10:00", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostShutdownTimeoutShorterThanTheDrainIsReported()
    {
        var problems = ShutdownBudget.Check(
            hostShutdownTimeout: TimeSpan.FromSeconds(5),
            shutdownDrainTimeout: TimeSpan.FromSeconds(30),
            jobs: []);

        var problem = Assert.Single(problems);

        // Both properties, because the fix could be either one and the reader has to choose.
        Assert.Contains("ShutdownTimeout", problem, StringComparison.Ordinal);
        Assert.Contains(nameof(CadenceOptions.ShutdownDrainTimeout), problem, StringComparison.Ordinal);
        Assert.Contains("00:00:05", problem, StringComparison.Ordinal);
        Assert.Contains("00:00:30", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeWarnsRatherThanFailingTheHost()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var probe = Probe(logs, drain: TimeSpan.FromSeconds(30), longestJob: TimeSpan.FromMinutes(10));

        probe.Report();

        // A warning, not an error: two of the three timeouts in the chain are outside the process,
        // so this can never be more than advice.
        var entry = Assert.Single(logs);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(CadenceOptions.ShutdownDrainTimeout), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeIsSilentWhenTheBudgetIsConsistent()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var probe = Probe(logs, drain: TimeSpan.FromSeconds(30), longestJob: TimeSpan.FromSeconds(5));

        probe.Report();

        Assert.Empty(logs);
    }

    private static ShutdownBudgetProbe Probe(
        List<(LogLevel Level, string Message)> logs,
        TimeSpan drain,
        TimeSpan longestJob)
        => new(
            new JobRegistry([Job("nightly-invoices", longestJob)]),
            Options.Create(new CadenceOptions { ShutdownDrainTimeout = drain }),
            Options.Create(new HostOptions()),
            new CapturingLogger<ShutdownBudgetProbe>(logs));

    private static JobDescriptor Job(string name, TimeSpan? maxDuration) => new()
    {
        Name = name,
        ImplementationType = typeof(object),
        DefaultCron = "* * * * *",
        MaxDuration = maxDuration,
    };
}
