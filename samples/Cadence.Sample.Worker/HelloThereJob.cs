using Microsoft.Extensions.Logging;

namespace Cadence.Sample.Worker;

/// <summary>
/// Does nothing useful on purpose. Its job is to prove the plumbing: it is resolved from DI per run,
/// it writes through its own injected logger, and it reports progress that fans out to the trace,
/// the log pipeline and run history.
/// </summary>
[ScheduledJob(
    Name = "hello-there",
    Cron = "*/10 * * * * *",
    MaxDuration = "00:00:30",
    Triggers = TriggerKind.Schedule | TriggerKind.Manual)]
public sealed class HelloThereJob(IGreetingService greetings, ILogger<HelloThereJob> logger) : IJob
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var name = greetings.NextName();

        // The job's own logger. Cadence has already opened a scope carrying JobName, RunId and
        // InstanceId, so this line is correlated without the job author doing anything.
        logger.Greeted(name);

        // Progress goes three places at once: an event on this run's activity, the MEL pipeline
        // (and therefore any OTLP log exporter), and run history for the dashboard to read back.
        context.Report($"greeted {name}", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["trigger"] = context.Trigger.ToString(),
        });

        // Enough work to be visible as a span with a duration, and to prove the token is observed.
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }
}

/// <summary>Supplies a different name each run, so successive runs are distinguishable at a glance.</summary>
public interface IGreetingService
{
    string NextName();
}

/// <inheritdoc />
public sealed class GreetingService : IGreetingService
{
    private static readonly string[] Names =
    [
        "Obi-Wan", "Ada", "Grace", "Alan", "Barbara", "Edsger", "Margaret", "Linus", "Radia", "Tony",
    ];

    public string NextName() => Names[Random.Shared.Next(Names.Length)];
}
